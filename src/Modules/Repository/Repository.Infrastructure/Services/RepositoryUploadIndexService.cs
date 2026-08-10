using System.Text.Json;
using Hangfire;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SaaSApp.MultiTenancy;
using SaaSApp.Repository.Application;
using SaaSApp.Repository.Application.Contracts;
using SaaSApp.Repository.Infrastructure.Jobs;
using SaaSApp.Repository.Infrastructure.Storage;

namespace SaaSApp.Repository.Infrastructure.Services;

public sealed class RepositoryUploadIndexService : IRepositoryUploadIndexService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ITenantConnectionProvider _connectionProvider;
    private readonly IStaticRepositoryProvisioner _provisioner;
    private readonly IRepositoryStorageSeedService _storageSeed;
    private readonly IRepositoryFileStorage _fileStorage;
    private readonly IOcrExtractionService _ocrExtraction;
    private readonly ITenantDisplayResolver _tenantDisplay;

    public RepositoryUploadIndexService(
        ITenantConnectionProvider connectionProvider,
        IStaticRepositoryProvisioner provisioner,
        IRepositoryStorageSeedService storageSeed,
        IRepositoryFileStorage fileStorage,
        IOcrExtractionService ocrExtraction,
        ITenantDisplayResolver tenantDisplay)
    {
        _connectionProvider = connectionProvider;
        _provisioner = provisioner;
        _storageSeed = storageSeed;
        _fileStorage = fileStorage;
        _ocrExtraction = ocrExtraction;
        _tenantDisplay = tenantDisplay;
    }

    public async Task<UploadIndexUploadResult> UploadAsync(
        Guid repositoryId,
        Guid tenantId,
        Stream fileStream,
        string fileName,
        string? contentType,
        long fileSize,
        string? fieldsJson,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        var repo = await _provisioner.GetRepositoryAsync(repositoryId, tenantId, cancellationToken)
            ?? throw new InvalidOperationException("Repository not found.");

        await _provisioner.EnsureRepositoryTablesAsync(repositoryId, tenantId, cancellationToken);

        var storageProviderId = await _storageSeed.ResolveStorageProviderIdAsync(
            tenantId, repo.StorageProviderId, null, cancellationToken);
        var providers = await _storageSeed.ListProvidersAsync(tenantId, cancellationToken);
        var providerCode = providers.First(p => p.Id == storageProviderId).Code;

        var relativePath = RepositoryFilePathHelper.BuildMonitorRelativePath(repositoryId, fileName);
        var stageItemId = Guid.NewGuid();

        await _fileStorage.SaveAsync(
            tenantId,
            repositoryId,
            stageItemId,
            fileName,
            fileStream,
            providerCode,
            relativePath,
            cancellationToken);

        var fieldValues = ParseFieldsToDictionary(fieldsJson);
        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var stageId = await RepositoryStageStore.InsertAsync(
            connection,
            repo,
            tenantId,
            repositoryId,
            storageProviderId,
            relativePath,
            fileName,
            contentType,
            fileSize is > 0 and <= int.MaxValue ? (int)fileSize : null,
            fieldValues,
            userId,
            cancellationToken);

        var ocrFields = fieldValues.Count > 0
            ? fieldValues.Select(kv => new UploadIndexFieldDto(kv.Key, kv.Value)).ToList()
            : null;

        return new UploadIndexUploadResult(stageId.ToString("D"), ocrFields);
    }

    public async Task<UploadForOcrResult> UploadForOcrAsync(
        Guid repositoryId,
        Guid tenantId,
        Stream fileStream,
        string? fieldsJson,
        string? pageNo,
        string? ocrType,
        string? validateType,
        string? filename = null,
        CancellationToken cancellationToken = default)
    {
        var repo = await _provisioner.GetRepositoryAsync(repositoryId, tenantId, cancellationToken)
            ?? throw new InvalidOperationException("Repository not found.");

        await using var buffer = new MemoryStream();
        await fileStream.CopyToAsync(buffer, cancellationToken);
        var fileBytes = buffer.ToArray();

        var parameters = OcrFieldParameterBuilder.BuildParameters(fieldsJson, repo);
        var tableParameters = OcrFieldParameterBuilder.BuildTableParameters(repo);

        var ocr = await _ocrExtraction.ExtractFromFileAsync(
            fileBytes,
            parameters,
            tableParameters,
            pageNo,
            ocrType,
            validateType,
            filename,
            repositoryId,
            cancellationToken);

        return new UploadForOcrResult(ocr.RawJson, ocr.OcrFieldList);
    }

    public async Task<UploadIndexLoadResult?> LoadAsync(
        Guid stageId,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var repo = await ResolveRepositoryForStageAsync(connection, tenantId, stageId, cancellationToken);
        if (repo == null)
            return null;

        var row = await RepositoryStageStore.GetAsync(connection, repo, tenantId, stageId, cancellationToken);
        if (row == null)
            return null;

        return MapToLoadResult(repo, row);
    }

    public async Task<UploadIndexArchiveQueuedResult?> QueueArchiveAsync(
        Guid stageId,
        Guid tenantId,
        UploadIndexSaveRequest request,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var repo = await _provisioner.GetRepositoryAsync(request.RepositoryId, tenantId, cancellationToken)
            ?? throw new InvalidOperationException("Repository not found.");

        var row = await RepositoryStageStore.GetAsync(connection, repo, tenantId, stageId, cancellationToken);
        if (row == null)
            return null;

        var fieldValues = ParseFieldsToDictionary(request.Fields);
        foreach (var kv in row.FieldValues)
            fieldValues.TryAdd(kv.Key, kv.Value);

        await RepositoryStageStore.UpdateFieldsAsync(
            connection,
            repo,
            tenantId,
            stageId,
            fieldValues,
            status: string.IsNullOrWhiteSpace(request.Status) ? "Queued" : request.Status,
            stageStatus: "Archiving",
            ocrResult: request.OcrResult,
            userId,
            cancellationToken);

        var tenantDisplay = await _tenantDisplay.ResolveAsync(tenantId, cancellationToken);
        var jobId = BackgroundJob.Enqueue<ArchiveStageItemJob>(j =>
            j.Execute(tenantDisplay, new ArchiveStageJobArgs(tenantId, request.RepositoryId, stageId, userId), null));

        return new UploadIndexArchiveQueuedResult(
            stageId.ToString("D"),
            jobId,
            "Archive queued. Hangfire will promote the staged file into the repository archive layout.");
    }

    public async Task<UploadIndexListResult> ListIndexAsync(
        Guid tenantId,
        UploadIndexListRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.RepositoryId is not Guid repositoryId || repositoryId == Guid.Empty)
            throw new ArgumentException("repositoryId is required for index/all.");

        var repo = await _provisioner.GetRepositoryAsync(repositoryId, tenantId, cancellationToken)
            ?? throw new InvalidOperationException("Repository not found.");

        await _provisioner.EnsureRepositoryTablesAsync(repositoryId, tenantId, cancellationToken);

        var page = request.CurrentPage <= 0 ? 1 : request.CurrentPage;
        var pageSize = request.ItemsPerPage <= 0 ? 50 : request.ItemsPerPage;
        var skip = (page - 1) * pageSize;
        var includeDeleted = string.Equals(request.Mode, "trash", StringComparison.OrdinalIgnoreCase);

        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var (rows, total) = await RepositoryStageStore.ListAsync(
            connection, repo, tenantId, includeDeleted, skip, pageSize, cancellationToken);

        var items = rows.Select(r => new UploadIndexListItem(
            r.Id.ToString("D"),
            r.FileName ?? string.Empty,
            r.Status ?? r.StageStatus,
            r.RepositoryId.ToString("D"),
            repo.Name,
            r.FileSize ?? 0,
            r.CreatedAtUtc.ToString("O"),
            r.PromotedItemId?.ToString("D"))).ToList();

        return new UploadIndexListResult(items, page, pageSize, total);
    }

    private UploadIndexLoadResult MapToLoadResult(RepositoryDetailDto repo, RepositoryStageRow row)
    {
        var fields = new List<UploadIndexFieldDto>();
        foreach (var field in repo.Fields.OrderBy(f => f.Level).ThenBy(f => f.OrderId ?? int.MaxValue))
        {
            row.FieldValues.TryGetValue(field.SqlColumnName, out var bySql);
            row.FieldValues.TryGetValue(field.Name, out var byName);
            fields.Add(new UploadIndexFieldDto(
                field.Name,
                bySql ?? byName ?? string.Empty,
                field.DataType));
        }

        var folderFields = RepositoryFolderStructureHelper.OrderFolderFields(
            repo.Fields.Where(f => f.IncludeInFolderStructure));
        var pathFolderFields = RepositoryArchiveFileNameResolver.PathFolderFields(repo.Fields, folderFields);

        var archiveSegments = pathFolderFields
            .Select(f =>
            {
                row.FieldValues.TryGetValue(f.SqlColumnName, out var v1);
                row.FieldValues.TryGetValue(f.Name, out var v2);
                return (v1 ?? v2)?.Trim();
            })
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();

        var fileStem = RepositoryArchiveFileNameResolver.ResolveArchiveFileStem(repo.Fields, row.FieldValues);
        if (string.IsNullOrWhiteSpace(fileStem))
            fileStem = Path.GetFileNameWithoutExtension(row.FileName ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(fileStem))
            archiveSegments.Add(fileStem);

        var archivePath = archiveSegments.Count > 0
            ? string.Join('\\', new[] { repo.Name }.Concat(archiveSegments!))
            : repo.Name;

        return new UploadIndexLoadResult(
            Id: row.Id.ToString("D"),
            TenantId: row.TenantId.ToString("D"),
            Name: row.FileName ?? string.Empty,
            FilePath: row.FilePath ?? string.Empty,
            Size: row.FileSize ?? 0,
            Workspace: new UploadIndexRefDto("0", string.Empty),
            Repository: new UploadIndexRefDto(repo.Id.ToString("D"), repo.Name),
            ItemId: row.PromotedItemId?.ToString("D") ?? "0",
            Fields: fields,
            Error: null,
            Status: row.Status ?? row.StageStatus,
            IsVerified: false,
            ArchivePath: archivePath,
            CloudFileServer: "EZOFIS",
            UploadedFrom: "WEB",
            UploadedAt: string.Empty,
            CreatedBy: row.CreatedBy?.ToString("D"),
            CreatedAt: row.CreatedAtUtc.ToString("O"),
            ModifiedBy: row.ModifiedBy?.ToString("D"),
            ModifiedAt: row.ModifiedAtUtc?.ToString("O"),
            IsDeleted: row.IsDeleted,
            TotalPage: 0,
            PromotedItemId: row.PromotedItemId?.ToString("D"));
    }

    private static Dictionary<string, string> ParseFieldsToDictionary(IReadOnlyList<UploadIndexFieldDto>? fields)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (fields == null)
            return dict;

        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name))
                continue;
            dict[field.Name.Trim()] = field.Value ?? string.Empty;
        }

        return dict;
    }

    private static Dictionary<string, string> ParseFieldsToDictionary(string? fieldsJson)
    {
        if (string.IsNullOrWhiteSpace(fieldsJson))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return ParseFieldsToDictionary(ParseFieldsList(fieldsJson));
    }

    private static List<UploadIndexFieldDto>? ParseFieldsList(string? fieldsJson)
    {
        if (string.IsNullOrWhiteSpace(fieldsJson))
            return null;

        var trimmed = fieldsJson.Trim();
        if (trimmed.StartsWith('{'))
        {
            var dict = RepositoryMetadataParser.Parse(trimmed);
            return dict.Select(kv => new UploadIndexFieldDto(kv.Key, kv.Value)).ToList();
        }

        return JsonSerializer.Deserialize<List<UploadIndexFieldDto>>(trimmed, JsonOptions);
    }

    private async Task<RepositoryDetailDto?> ResolveRepositoryForStageAsync(
        SqlConnection connection,
        Guid tenantId,
        Guid stageId,
        CancellationToken cancellationToken)
    {
        var repos = await _provisioner.ListRepositoriesAsync(tenantId, cancellationToken);
        foreach (var summary in repos)
        {
            var repo = await _provisioner.GetRepositoryAsync(summary.Id, tenantId, cancellationToken);
            if (repo == null)
                continue;

            var row = await RepositoryStageStore.GetAsync(connection, repo, tenantId, stageId, cancellationToken);
            if (row != null)
                return repo;
        }

        return null;
    }
}
