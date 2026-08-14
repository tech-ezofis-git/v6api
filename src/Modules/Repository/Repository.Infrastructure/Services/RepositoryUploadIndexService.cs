using System.Text.Json;
using Hangfire;
using Npgsql;
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
    private readonly IRepositoryArchiveFileUploadService _archiveUpload;
    private readonly ITenantDisplayResolver _tenantDisplay;

    public RepositoryUploadIndexService(
        ITenantConnectionProvider connectionProvider,
        IStaticRepositoryProvisioner provisioner,
        IRepositoryStorageSeedService storageSeed,
        IRepositoryFileStorage fileStorage,
        IOcrExtractionService ocrExtraction,
        IRepositoryArchiveFileUploadService archiveUpload,
        ITenantDisplayResolver tenantDisplay)
    {
        _connectionProvider = connectionProvider;
        _provisioner = provisioner;
        _storageSeed = storageSeed;
        _fileStorage = fileStorage;
        _ocrExtraction = ocrExtraction;
        _archiveUpload = archiveUpload;
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

        await using var connection = new NpgsqlConnection(connectionString);
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

    public async Task<UploadWithOcrResult> UploadWithOcrAsync(
        Guid repositoryId,
        Guid tenantId,
        Stream fileStream,
        string fileName,
        string? contentType,
        long fileSize,
        string? fieldsJson,
        string? pageNo,
        string? ocrType,
        string? validateType,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        await using var buffer = new MemoryStream();
        await fileStream.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

        // fieldsJson here is an OCR hint list (name,TYPE), not pre-filled values.
        // Stage first without values; OCR results are written to the stage row below.
        var upload = await UploadAsync(
            repositoryId,
            tenantId,
            buffer,
            fileName,
            contentType,
            fileSize,
            fieldsJson: null,
            userId,
            cancellationToken);

        if (!Guid.TryParse(upload.FileId, out var stageId))
            throw new InvalidOperationException("Stage id was not returned from upload.");

        buffer.Position = 0;
        var ocr = await UploadForOcrAsync(
            repositoryId,
            tenantId,
            buffer,
            fieldsJson,
            pageNo,
            ocrType,
            validateType,
            fileName,
            cancellationToken);

        var repo = await _provisioner.GetRepositoryAsync(repositoryId, tenantId, cancellationToken)
            ?? throw new InvalidOperationException("Repository not found.");

        var ocrFieldValues = ParseFieldsToDictionary(ocr.OcrFieldList);
        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await RepositoryStageStore.UpdateFieldsAsync(
            connection,
            repo,
            tenantId,
            stageId,
            ocrFieldValues,
            status: "OCR",
            stageStatus: "OCR",
            ocrResult: ocr.OcrJson,
            userId,
            cancellationToken);

        var row = await RepositoryStageStore.GetAsync(connection, repo, tenantId, stageId, cancellationToken)
            ?? throw new InvalidOperationException("Stage row not found after OCR update.");

        return new UploadWithOcrResult(
            stageId.ToString("D"),
            repositoryId,
            row.FileName ?? fileName,
            row.FilePath ?? string.Empty,
            ocr.OcrJson,
            ocr.OcrFieldList);
    }

    public async Task<UploadIndexPromoteResult?> PromoteStageAsync(
        Guid stageId,
        Guid repositoryId,
        Guid tenantId,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        var repo = await _provisioner.GetRepositoryAsync(repositoryId, tenantId, cancellationToken)
            ?? throw new InvalidOperationException("Repository not found.");

        await _provisioner.EnsureRepositoryTablesAsync(repositoryId, tenantId, cancellationToken);

        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var row = await RepositoryStageStore.GetAsync(connection, repo, tenantId, stageId, cancellationToken);
        if (row == null)
            return null;

        if (row.PromotedItemId is Guid alreadyPromoted && alreadyPromoted != Guid.Empty)
        {
            return new UploadIndexPromoteResult(
                alreadyPromoted,
                repositoryId,
                row.FileName ?? string.Empty,
                row.FilePath ?? string.Empty,
                System.Text.Json.JsonSerializer.Serialize(row.FieldValues),
                row.FileSize,
                row.FileType);
        }

        if (string.IsNullOrWhiteSpace(row.FilePath) || string.IsNullOrWhiteSpace(row.FileName))
            throw new InvalidOperationException("Stage row is missing file path or name.");

        var providers = await _storageSeed.ListProvidersAsync(tenantId, cancellationToken);
        var providerCode = providers.First(p => p.Id == row.StorageProviderId).Code;

        await using var source = await _fileStorage.OpenReadAsync(
            tenantId,
            row.FilePath,
            providerCode,
            cancellationToken);

        await using var promoteBuffer = new MemoryStream();
        await source.CopyToAsync(promoteBuffer, cancellationToken);
        promoteBuffer.Position = 0;

        var metadataJson = System.Text.Json.JsonSerializer.Serialize(row.FieldValues);
        var uploadRequest = new RepositoryUploadItemRequest(
            promoteBuffer,
            row.FileName,
            row.FileType,
            FileSize: row.FileSize,
            Metadata: metadataJson);

        var result = await _archiveUpload.UploadItemAsync(
            repositoryId,
            tenantId,
            uploadRequest,
            userId,
            cancellationToken);

        await RepositoryStageStore.MarkArchivedAsync(
            connection,
            repo.StageTableName,
            tenantId,
            stageId,
            result.ItemId,
            cancellationToken);

        return new UploadIndexPromoteResult(
            result.ItemId,
            repositoryId,
            result.FileName,
            result.FilePath,
            metadataJson,
            row.FileSize,
            row.FileType);
    }

    public async Task<UploadIndexLoadResult?> LoadAsync(
        Guid stageId,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new NpgsqlConnection(connectionString);
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

        await using var connection = new NpgsqlConnection(connectionString);
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

        await using var connection = new NpgsqlConnection(connectionString);
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

        if (!trimmed.StartsWith('['))
            return ParseFieldNamesFromPlainText(trimmed);

        try
        {
            return JsonSerializer.Deserialize<List<UploadIndexFieldDto>>(trimmed, JsonOptions);
        }
        catch (JsonException)
        {
            // "fields" may carry OCR parameter lines (["Supplier,SHORT_TEXT"]) instead of name/value objects.
            try
            {
                var lines = JsonSerializer.Deserialize<List<string>>(trimmed, JsonOptions);
                return lines == null
                    ? null
                    : lines
                        .SelectMany(line => ParseFieldNamesFromPlainText(line) ?? new List<UploadIndexFieldDto>())
                        .ToList();
            }
            catch (JsonException)
            {
                return ParseFieldNamesFromPlainText(trimmed);
            }
        }
    }

    /// <summary>Accepts OCR parameter text ("Supplier,SHORT_TEXT") and returns field names with empty values.</summary>
    private static List<UploadIndexFieldDto>? ParseFieldNamesFromPlainText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var list = new List<UploadIndexFieldDto>();
        foreach (var part in value.Split(new[] { '\n', '\r', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var comma = part.IndexOf(',');
            var name = (comma > 0 ? part[..comma] : part).Trim();
            if (name.Length > 0)
                list.Add(new UploadIndexFieldDto(name, string.Empty));
        }

        return list.Count == 0 ? null : list;
    }

    private async Task<RepositoryDetailDto?> ResolveRepositoryForStageAsync(
        NpgsqlConnection connection,
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
