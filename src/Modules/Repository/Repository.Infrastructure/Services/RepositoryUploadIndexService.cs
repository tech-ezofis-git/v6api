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

        fileName = RepositoryFilePathHelper.EnsureFileNameHasExtension(fileName, contentType);

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

        return new UploadForOcrResult(ocr.RawJson, ocr.OcrFieldList, ocr.OcrText);
    }

    public async Task<UploadWithOcrResult> UploadWithOcrAsync(
        Guid repositoryId,
        Guid tenantId,
        Stream fileStream,
        string fileName,
        string? contentType,
        long fileSize,
        string? metadataJson,
        string? ocrJson,
        string? ocrText,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        await using var buffer = new MemoryStream();
        await fileStream.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

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

        var parsed = ParseUploadWithOcrMetadata(metadataJson, ocrJson, ocrText);
        var repo = await _provisioner.GetRepositoryAsync(repositoryId, tenantId, cancellationToken)
            ?? throw new InvalidOperationException("Repository not found.");

        // OCR does not return the archive naming field — use the original upload file name.
        RepositoryNamingFieldMetadataInjector.InjectFromOriginalFileName(
            repo.Fields,
            parsed.FieldValues,
            fileName,
            contentType);

        // Collapse display names ("Document Description") and SqlColumnName aliases to one key each.
        var fieldValues = CollapseFieldValuesToSqlColumns(parsed.FieldValues, repo.Fields);

        var ocrFieldList = parsed.OcrFieldList?.ToList() ?? new List<UploadIndexFieldDto>();
        RepositoryNamingFieldMetadataInjector.EnsureInFieldList(
            repo.Fields,
            ocrFieldList,
            fileName,
            fieldValues,
            contentType);

        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // Primary contract: stage to monitor + return fileId. Metadata/OCR column update is best-effort
        // so a DATE/type mismatch must not fail the whole upload after the file is already staged.
        try
        {
            await RepositoryStageStore.UpdateFieldsAsync(
                connection,
                repo,
                tenantId,
                stageId,
                fieldValues,
                status: "OCR",
                stageStatus: "OCR",
                ocrResult: parsed.OcrJson,
                userId,
                cancellationToken,
                ocrText: parsed.OcrText);
        }
        catch (Exception ex) when (ex is PostgresException or ArgumentException or InvalidOperationException)
        {
            // File + stage row already exist — return fileId so FE/workflow start can continue.
            _ = ex;
        }

        var row = await RepositoryStageStore.GetAsync(connection, repo, tenantId, stageId, cancellationToken)
            ?? throw new InvalidOperationException("Stage row not found after upload.");

        return new UploadWithOcrResult(
            stageId.ToString("D"),
            repositoryId,
            row.FileName ?? fileName,
            row.FilePath ?? string.Empty,
            parsed.OcrJson ?? row.OcrJson ?? string.Empty,
            ocrFieldList);
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

        var archiveSegments = folderFields
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

    /// <summary>
    /// Maps OCR/UI display names (e.g. "Document Description", "Project No") onto SqlColumnName keys
    /// so stage/archive updates never assign the same column twice.
    /// </summary>
    private static Dictionary<string, string> CollapseFieldValuesToSqlColumns(
        IReadOnlyDictionary<string, string> fieldValues,
        IReadOnlyList<RepositoryFieldDto> fields)
    {
        var aliasToSql = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.SqlColumnName))
                continue;

            var sqlCol = RepositorySqlHelper.SanitizeColumnName(field.SqlColumnName);
            aliasToSql[sqlCol] = sqlCol;
            if (!string.IsNullOrWhiteSpace(field.SqlColumnName))
                aliasToSql[field.SqlColumnName.Trim()] = sqlCol;
            if (!string.IsNullOrWhiteSpace(field.Name))
            {
                aliasToSql[field.Name.Trim()] = sqlCol;
                try
                {
                    aliasToSql[RepositorySqlHelper.SanitizeColumnName(field.Name)] = sqlCol;
                }
                catch (ArgumentException)
                {
                    // ignore unusable display names
                }
            }
        }

        var collapsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in fieldValues)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;

            var trimmed = key.Trim();
            if (aliasToSql.TryGetValue(trimmed, out var sqlCol))
            {
                collapsed[sqlCol] = value;
                continue;
            }

            try
            {
                var sanitized = RepositorySqlHelper.SanitizeColumnName(trimmed);
                if (aliasToSql.TryGetValue(sanitized, out sqlCol))
                {
                    collapsed[sqlCol] = value;
                    continue;
                }
            }
            catch (ArgumentException)
            {
                // keep original key below
            }

            collapsed[trimmed] = value;
        }

        return collapsed;
    }

    private sealed record ParsedUploadWithOcrMetadata(
        Dictionary<string, string> FieldValues,
        IReadOnlyList<UploadIndexFieldDto>? OcrFieldList,
        string? OcrJson,
        string? OcrText);

    /// <summary>
    /// Accepts metadata from uploadForOcr / FE:
    /// - flat object with field values (+ optional ocrJson / ocrText),
    /// - { ocrJson, ocrFieldList },
    /// - or [{ name, value, type }, ...].
    /// </summary>
    private static ParsedUploadWithOcrMetadata ParseUploadWithOcrMetadata(
        string? metadataJson,
        string? ocrJsonForm,
        string? ocrTextForm)
    {
        var fieldValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        List<UploadIndexFieldDto>? fieldList = null;
        string? ocrJson = string.IsNullOrWhiteSpace(ocrJsonForm) ? null : ocrJsonForm.Trim();
        string? ocrText = string.IsNullOrWhiteSpace(ocrTextForm) ? null : ocrTextForm.Trim();

        if (!string.IsNullOrWhiteSpace(metadataJson))
        {
            var trimmed = metadataJson.Trim();
            if (trimmed.StartsWith('['))
            {
                fieldList = ParseFieldsList(trimmed);
                fieldValues = ParseFieldsToDictionary(fieldList);
            }
            else if (trimmed.StartsWith('{'))
            {
                try
                {
                    using var doc = JsonDocument.Parse(trimmed);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("ocrFieldList", out var listEl)
                        || root.TryGetProperty("OcrFieldList", out listEl))
                    {
                        fieldList = JsonSerializer.Deserialize<List<UploadIndexFieldDto>>(
                            listEl.GetRawText(), JsonOptions);
                        fieldValues = ParseFieldsToDictionary(fieldList);
                    }

                    if (TryReadJsonPropertyAsString(root, "ocrJson", out var embeddedOcrJson)
                        || TryReadJsonPropertyAsString(root, "OcrJson", out embeddedOcrJson))
                    {
                        ocrJson ??= embeddedOcrJson;
                    }

                    if (TryReadJsonPropertyAsString(root, "ocrText", out var embeddedOcrText)
                        || TryReadJsonPropertyAsString(root, "OcrText", out embeddedOcrText)
                        || TryReadJsonPropertyAsString(root, "ocr_text", out embeddedOcrText))
                    {
                        ocrText ??= embeddedOcrText;
                    }

                    // Flat field map (Supplier, InvoiceNo, …) — skip known OCR payload keys.
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (prop.NameEquals("ocrJson") || prop.NameEquals("OcrJson")
                            || prop.NameEquals("ocrText") || prop.NameEquals("OcrText")
                            || prop.NameEquals("ocr_text")
                            || prop.NameEquals("ocrFieldList") || prop.NameEquals("OcrFieldList"))
                            continue;

                        if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                            continue;

                        var value = prop.Value.ValueKind == JsonValueKind.Null
                            ? string.Empty
                            : prop.Value.ToString();
                        fieldValues[prop.Name] = value;
                    }

                    fieldList ??= fieldValues
                        .Select(kv => new UploadIndexFieldDto(kv.Key, kv.Value))
                        .ToList();
                }
                catch (JsonException)
                {
                    fieldValues = ParseFieldsToDictionary(trimmed);
                    fieldList = fieldValues.Select(kv => new UploadIndexFieldDto(kv.Key, kv.Value)).ToList();
                }
            }
            else
            {
                fieldValues = ParseFieldsToDictionary(trimmed);
                fieldList = fieldValues.Select(kv => new UploadIndexFieldDto(kv.Key, kv.Value)).ToList();
            }
        }

        // If ocrJson not provided separately, persist the field list / metadata as OcrJson.
        if (string.IsNullOrWhiteSpace(ocrJson) && fieldList is { Count: > 0 })
            ocrJson = JsonSerializer.Serialize(fieldList);

        ocrText ??= OcrResultParser.TryParseOcrText(ocrJson);

        return new ParsedUploadWithOcrMetadata(fieldValues, fieldList, ocrJson, ocrText);
    }

    private static bool TryReadJsonPropertyAsString(JsonElement root, string name, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out var el))
            return false;

        value = el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Null => null,
            _ => el.GetRawText()
        };
        return !string.IsNullOrWhiteSpace(value);
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
