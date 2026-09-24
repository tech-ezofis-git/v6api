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

        fileName = RepositoryFilePathHelper.EnsureFileNameHasExtension(fileName, contentType);
        // Prefer a real MIME from the file name when browsers send octet-stream / empty type
        // (common for .docx). Also keeps Office MIME under file_type column length.
        contentType = RepositoryOcrFileSupport.ResolveContentType(fileName, contentType);

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

        var repo = await _provisioner.GetRepositoryAsync(repositoryId, tenantId, cancellationToken)
            ?? throw new InvalidOperationException("Repository not found.");

        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

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

        var ocrFieldValues = ParseFieldsToDictionary(ocr.OcrFieldList);

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
        CancellationToken cancellationToken = default,
        bool allowIncompleteFolderMetadata = false)
    {
        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var repo = await _provisioner.GetRepositoryAsync(repositoryId, tenantId, cancellationToken);
        RepositoryStageRow? row = null;
        if (repo != null)
        {
            await _provisioner.EnsureRepositoryTablesAsync(repositoryId, tenantId, cancellationToken);
            row = await RepositoryStageStore.GetAsync(connection, repo, tenantId, stageId, cancellationToken);
        }

        if (row == null)
        {
            repo = await ResolveRepositoryForStageAsync(connection, tenantId, stageId, cancellationToken);
            if (repo == null)
                return null;

            row = await RepositoryStageStore.GetAsync(connection, repo, tenantId, stageId, cancellationToken);
            if (row == null)
                return null;

            repositoryId = repo.Id;
        }

        if (row.PromotedItemId is Guid alreadyPromoted && alreadyPromoted != Guid.Empty)
        {
            return new UploadIndexPromoteResult(
                alreadyPromoted,
                repositoryId,
                row.FileName ?? string.Empty,
                row.FilePath ?? string.Empty,
                JsonSerializer.Serialize(row.FieldValues),
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

        // Archive from stage row: field columns + OCR text become archive metadata.
        var fieldValues = new Dictionary<string, string>(row.FieldValues, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in RepositoryOcrJsonMetadataExtractor.Extract(row.OcrJson, row.SummaryJson))
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;
            fieldValues.TryAdd(key, value);
        }

        RepositoryNamingFieldMetadataInjector.InjectFromOriginalFileName(
            repo!.Fields,
            fieldValues,
            row.FileName,
            row.FileType);

        fieldValues = CollapseFieldValuesToSqlColumns(fieldValues, repo.Fields);

        var metadataJson = JsonSerializer.Serialize(fieldValues);
        var uploadRequest = new RepositoryUploadItemRequest(
            promoteBuffer,
            row.FileName,
            row.FileType,
            FileSize: row.FileSize,
            Metadata: metadataJson,
            AllowIncompleteFolderMetadata: allowIncompleteFolderMetadata);

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

        // Archive copy is in place — remove monitor temp file.
        await TryDeleteMonitorFileAsync(
            tenantId,
            row.FilePath,
            providerCode,
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

    public async Task<UploadIndexDeleteFilesResult> DeleteStageFilesAsync(
        Guid tenantId,
        IReadOnlyList<Guid> stageIds,
        Guid? repositoryId,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        if (stageIds == null || stageIds.Count == 0)
            throw new ArgumentException("At least one file id is required.");

        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var results = new List<UploadIndexDeleteFileResult>(stageIds.Count);
        foreach (var stageId in stageIds.Distinct())
        {
            try
            {
                RepositoryDetailDto? repo = null;
                RepositoryStageRow? row = null;

                if (repositoryId is Guid rid && rid != Guid.Empty)
                {
                    repo = await _provisioner.GetRepositoryAsync(rid, tenantId, cancellationToken);
                    if (repo != null)
                        row = await RepositoryStageStore.GetAsync(connection, repo, tenantId, stageId, cancellationToken);
                }

                if (row == null)
                {
                    repo = await ResolveRepositoryForStageAsync(connection, tenantId, stageId, cancellationToken);
                    if (repo == null)
                    {
                        results.Add(new UploadIndexDeleteFileResult(stageId.ToString("D"), false, "Stage file not found."));
                        continue;
                    }

                    row = await RepositoryStageStore.GetAsync(connection, repo, tenantId, stageId, cancellationToken);
                    if (row == null)
                    {
                        results.Add(new UploadIndexDeleteFileResult(stageId.ToString("D"), false, "Stage file not found."));
                        continue;
                    }
                }

                if (row.PromotedItemId is Guid promoted && promoted != Guid.Empty)
                {
                    results.Add(new UploadIndexDeleteFileResult(
                        stageId.ToString("D"),
                        false,
                        "File is already archived; delete from repository items instead."));
                    continue;
                }

                var providers = await _storageSeed.ListProvidersAsync(tenantId, cancellationToken);
                var providerCode = providers.First(p => p.Id == row.StorageProviderId).Code;
                var monitorPath = row.FilePath;

                var deleted = await RepositoryStageStore.SoftDeleteAsync(
                    connection,
                    repo!.StageTableName,
                    tenantId,
                    stageId,
                    userId,
                    cancellationToken);

                if (!deleted)
                {
                    results.Add(new UploadIndexDeleteFileResult(stageId.ToString("D"), false, "Stage file not found or already deleted."));
                    continue;
                }

                await TryDeleteMonitorFileAsync(tenantId, monitorPath, providerCode, cancellationToken);
                results.Add(new UploadIndexDeleteFileResult(stageId.ToString("D"), true));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or PostgresException)
            {
                results.Add(new UploadIndexDeleteFileResult(stageId.ToString("D"), false, ex.Message));
            }
        }

        var succeeded = results.Count(r => r.Succeeded);
        return new UploadIndexDeleteFilesResult(
            results.Count,
            succeeded,
            results.Count - succeeded,
            results);
    }

    private async Task TryDeleteMonitorFileAsync(
        Guid tenantId,
        string? relativePath,
        string providerCode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return;

        var path = relativePath.Trim().Replace('\\', '/');
        if (!path.StartsWith(RepositoryFilePathHelper.MonitorRoot + "/", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(path, RepositoryFilePathHelper.MonitorRoot, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            await _fileStorage.DeleteAsync(tenantId, path, providerCode, cancellationToken);
        }
        catch (Exception ex)
        {
            // Archive/delete already succeeded in DB — do not fail the request on blob cleanup.
            _ = ex;
        }
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
            status: string.IsNullOrWhiteSpace(request.Status) ? "Indexing" : request.Status,
            stageStatus: "Archiving",
            ocrResult: request.OcrResult,
            userId,
            cancellationToken);

        // Archive immediately (no Hangfire) — promote monitor file into repository archive.
        var promoted = await PromoteStageAsync(
            stageId,
            request.RepositoryId,
            tenantId,
            userId,
            cancellationToken);

        if (promoted == null)
            return null;

        return new UploadIndexArchiveQueuedResult(
            stageId.ToString("D"),
            HangfireJobId: string.Empty,
            Message: "Archived successfully.",
            promoted.ItemId,
            promoted.FileName,
            promoted.FilePath);
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

    public async Task<BulkUploadResult> BulkUploadAsync(
        Guid repositoryId,
        Guid tenantId,
        IReadOnlyList<(Stream Stream, string FileName, string? ContentType, long FileSize)> files,
        string? sharedFieldsJson,
        string? pageNo,
        string? ocrType,
        string? validateType,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        if (files == null || files.Count == 0)
            throw new ArgumentException("At least one file is required for bulk upload.");

        var repo = await _provisioner.GetRepositoryAsync(repositoryId, tenantId, cancellationToken)
            ?? throw new InvalidOperationException("Repository not found.");

        await _provisioner.EnsureRepositoryTablesAsync(repositoryId, tenantId, cancellationToken);

        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        var results = new List<BulkUploadFileResult>(files.Count);
        var stagedIds = new List<Guid>(files.Count);

        foreach (var file in files)
        {
            var fileName = string.IsNullOrWhiteSpace(file.FileName) ? "upload.bin" : file.FileName.Trim();
            try
            {
                await using var buffer = new MemoryStream();
                await file.Stream.CopyToAsync(buffer, cancellationToken);
                buffer.Position = 0;

                // Stage to monitor + stage table only — OCR runs in Hangfire.
                var upload = await UploadAsync(
                    repositoryId,
                    tenantId,
                    buffer,
                    fileName,
                    file.ContentType,
                    file.FileSize > 0 ? file.FileSize : buffer.Length,
                    sharedFieldsJson,
                    userId,
                    cancellationToken);

                if (!Guid.TryParse(upload.FileId, out var stageId))
                    throw new InvalidOperationException("Stage id was not returned from upload.");

                await using (var connection = new NpgsqlConnection(connectionString))
                {
                    await connection.OpenAsync(cancellationToken);
                    await RepositoryStageStore.UpdateFieldsAsync(
                        connection,
                        repo,
                        tenantId,
                        stageId,
                        ParseFieldsToDictionary(sharedFieldsJson),
                        status: "Queued",
                        stageStatus: "PendingOCR",
                        ocrResult: null,
                        userId,
                        cancellationToken);
                }

                stagedIds.Add(stageId);
                results.Add(new BulkUploadFileResult(
                    stageId.ToString("D"),
                    repositoryId,
                    fileName,
                    FilePath: string.Empty,
                    OcrJson: string.Empty,
                    OcrFieldList: upload.OcrFieldList,
                    Succeeded: true,
                    Status: "Queued"));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or PostgresException)
            {
                results.Add(new BulkUploadFileResult(
                    FileId: string.Empty,
                    repositoryId,
                    fileName,
                    FilePath: string.Empty,
                    OcrJson: string.Empty,
                    OcrFieldList: null,
                    Succeeded: false,
                    Error: ex.Message,
                    Status: "Failed"));
            }
        }

        var succeeded = results.Count(r => r.Succeeded);
        if (stagedIds.Count == 0)
        {
            return new BulkUploadResult(
                repositoryId,
                JobId: string.Empty,
                Message: "No files were uploaded.",
                results,
                succeeded,
                results.Count - succeeded);
        }

        var tenantDisplay = await _tenantDisplay.ResolveAsync(tenantId, cancellationToken);
        var jobArgs = new BulkOcrJobArgs(
            tenantId,
            repositoryId,
            string.Join(",", stagedIds.Select(id => id.ToString("D"))),
            sharedFieldsJson,
            pageNo,
            ocrType,
            validateType,
            userId);

        var jobId = BackgroundJob.Enqueue<BulkUploadOcrJob>(j =>
            j.Execute(tenantDisplay, jobArgs, null));

        return new BulkUploadResult(
            repositoryId,
            jobId,
            "Upload successful. OCR queued — poll job status for progress.",
            results,
            succeeded,
            results.Count - succeeded);
    }

    public async Task ProcessBulkOcrForStageAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid stageId,
        string? sharedFieldsJson,
        string? pageNo,
        string? ocrType,
        string? validateType,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        var repo = await _provisioner.GetRepositoryAsync(repositoryId, tenantId, cancellationToken)
            ?? throw new InvalidOperationException("Repository not found.");

        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var row = await RepositoryStageStore.GetAsync(connection, repo, tenantId, stageId, cancellationToken)
            ?? throw new InvalidOperationException($"Stage row {stageId:D} not found.");

        if (string.IsNullOrWhiteSpace(row.FilePath) || string.IsNullOrWhiteSpace(row.FileName))
            throw new InvalidOperationException("Stage row is missing file path or name.");

        // Already OCR'd (idempotent retry).
        if (string.Equals(row.Status, "OCR", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(row.OcrJson))
            return;

        var providers = await _storageSeed.ListProvidersAsync(tenantId, cancellationToken);
        var providerCode = providers.First(p => p.Id == row.StorageProviderId).Code;

        await using var source = await _fileStorage.OpenReadAsync(
            tenantId,
            row.FilePath,
            providerCode,
            cancellationToken);

        await using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

        try
        {
            var ocr = await UploadForOcrAsync(
                repositoryId,
                tenantId,
                buffer,
                sharedFieldsJson,
                pageNo,
                ocrType,
                validateType,
                row.FileName,
                cancellationToken);

            var sharedFieldValues = ParseFieldsToDictionary(sharedFieldsJson);
            var merged = ParseFieldsToDictionary(ocr.OcrFieldList);

            // Drop OCR placeholders like "null" so naming/shared values can fill them.
            foreach (var key in merged.Keys.ToList())
            {
                if (IsEmptyOcrValue(merged[key]))
                    merged.Remove(key);
            }

            // Shared bulk inputs win only when the user actually supplied a value
            // (OCR param lines like "Year, SINGLE_SELECT" parse to empty — must not wipe OCR).
            foreach (var (key, value) in sharedFieldValues)
            {
                if (string.IsNullOrWhiteSpace(key) || IsEmptyOcrValue(value))
                    continue;
                merged[key] = value;
            }

            // Keep any values already on the stage row (from initial shared-fields upload).
            foreach (var (key, value) in row.FieldValues)
            {
                if (!string.IsNullOrWhiteSpace(key) && !IsEmptyOcrValue(value))
                    merged.TryAdd(key, value);
            }

            RepositoryNamingFieldMetadataInjector.InjectFromOriginalFileName(
                repo.Fields,
                merged,
                row.FileName,
                row.FileType);

            var fieldValues = CollapseFieldValuesToSqlColumns(merged, repo.Fields);

            await RepositoryStageStore.UpdateFieldsAsync(
                connection,
                repo,
                tenantId,
                stageId,
                fieldValues,
                status: "OCR",
                stageStatus: "OCR",
                ocrResult: ocr.OcrJson,
                userId,
                cancellationToken,
                ocrText: ocr.OcrText);
        }
        catch (Exception)
        {
            await RepositoryStageStore.UpdateFieldsAsync(
                connection,
                repo,
                tenantId,
                stageId,
                row.FieldValues,
                status: "OCRFailed",
                stageStatus: "OCRFailed",
                ocrResult: null,
                userId,
                cancellationToken);
            throw;
        }
    }

    public async Task<BulkUploadJobStatusResult?> GetBulkUploadJobStatusAsync(
        string jobId,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return null;

        jobId = jobId.Trim();
        var hangfireState = ResolveHangfireState(jobId);
        if (hangfireState == null)
            return null;

        var args = TryGetBulkOcrJobArgs(jobId);
        var repositoryId = args?.RepositoryId ?? Guid.Empty;
        var stageIds = ParseBulkStageIds(args?.StageIdsCsv);

        var files = new List<BulkUploadFileStatusItem>();
        if (repositoryId != Guid.Empty && stageIds.Count > 0)
        {
            var repo = await _provisioner.GetRepositoryAsync(repositoryId, tenantId, cancellationToken);
            if (repo != null)
            {
                var connectionString = _connectionProvider.ConnectionString
                    ?? throw new InvalidOperationException("Tenant connection string not resolved.");

                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);

                foreach (var stageId in stageIds)
                {
                    var row = await RepositoryStageStore.GetAsync(connection, repo, tenantId, stageId, cancellationToken);
                    if (row == null)
                    {
                        files.Add(new BulkUploadFileStatusItem(
                            stageId.ToString("D"),
                            string.Empty,
                            "Missing",
                            "Stage row not found.",
                            Phase: "Missing",
                            OcrCompleted: false,
                            Indexed: false,
                            Completed: false));
                        continue;
                    }

                    var status = row.Status ?? row.StageStatus ?? "Unknown";
                    var promotedId = row.PromotedItemId is Guid p && p != Guid.Empty
                        ? p.ToString("D")
                        : null;
                    var (phase, ocrDone, isIndexed) = ClassifyBulkFilePhase(status, promotedId);

                    files.Add(new BulkUploadFileStatusItem(
                        stageId.ToString("D"),
                        row.FileName ?? string.Empty,
                        status,
                        Error: phase == "OcrFailed" ? status : null,
                        Phase: phase,
                        OcrCompleted: ocrDone,
                        Indexed: isIndexed,
                        Completed: isIndexed,
                        PromotedItemId: promotedId));
                }
            }
        }

        var ocrReadyToIndex = files.Count(f => f.OcrCompleted && !f.Indexed);
        var ocrFailed = files.Count(f =>
            string.Equals(f.Phase, "OcrFailed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(f.Phase, "Missing", StringComparison.OrdinalIgnoreCase));
        var indexedCount = files.Count(f => f.Indexed);
        // Pending = not OCR-success yet and not failed/missing (still waiting on Hangfire OCR).
        var ocrPending = files.Count(f =>
            string.Equals(f.Phase, "PendingOcr", StringComparison.OrdinalIgnoreCase));
        var notCompleted = files.Count(f => !f.Completed);

        var terminal = string.Equals(hangfireState, "Succeeded", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hangfireState, "Failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hangfireState, "Deleted", StringComparison.OrdinalIgnoreCase);

        string? errorMessage = null;
        if (string.Equals(hangfireState, "Failed", StringComparison.OrdinalIgnoreCase))
            errorMessage = TryGetHangfireExceptionMessage(jobId);

        return new BulkUploadJobStatusResult(
            jobId,
            hangfireState,
            terminal,
            errorMessage,
            repositoryId,
            files,
            OcrCompleted: files.Count(f => f.OcrCompleted),
            OcrPending: Math.Max(0, ocrPending),
            OcrFailed: ocrFailed,
            Indexed: indexedCount,
            ReadyToIndex: ocrReadyToIndex,
            NotCompleted: notCompleted);
    }

    public async Task<BulkUploadJobStatusResult?> GetActiveBulkUploadJobStatusAsync(
        Guid tenantId,
        Guid? repositoryId = null,
        CancellationToken cancellationToken = default)
    {
        var activeJobId = TryFindFirstActiveBulkOcrJobId(tenantId, repositoryId);
        if (string.IsNullOrWhiteSpace(activeJobId))
            return null;

        return await GetBulkUploadJobStatusAsync(activeJobId, tenantId, cancellationToken);
    }

    /// <summary>
    /// Scan Hangfire Processing then Enqueued for the first <see cref="BulkUploadOcrJob"/>
    /// matching tenant (and optional repository).
    /// </summary>
    private static string? TryFindFirstActiveBulkOcrJobId(Guid tenantId, Guid? repositoryId)
    {
        try
        {
            foreach (var jobId in EnumerateActiveBulkOcrJobIds())
            {
                var args = TryGetBulkOcrJobArgs(jobId);
                if (args == null)
                    continue;
                if (args.TenantId != tenantId)
                    continue;
                if (repositoryId is Guid repoFilter && repoFilter != Guid.Empty && args.RepositoryId != repoFilter)
                    continue;
                return jobId;
            }
        }
        catch
        {
            // Hangfire storage may be unavailable.
        }

        return null;
    }

    private static IEnumerable<string> EnumerateActiveBulkOcrJobIds()
    {
        var monitor = JobStorage.Current.GetMonitoringApi();

        // Prefer jobs already running, then queue order (from=0 is the head of the queue).
        foreach (var pair in monitor.ProcessingJobs(0, 200))
        {
            if (IsBulkUploadOcrJob(pair.Value?.Job))
                yield return pair.Key;
        }

        var queues = monitor.Queues();
        if (queues != null)
        {
            foreach (var queue in queues)
            {
                var name = string.IsNullOrWhiteSpace(queue.Name) ? "default" : queue.Name;
                foreach (var pair in monitor.EnqueuedJobs(name, 0, 200))
                {
                    if (IsBulkUploadOcrJob(pair.Value?.Job))
                        yield return pair.Key;
                }
            }
        }

        foreach (var pair in monitor.ScheduledJobs(0, 100))
        {
            if (IsBulkUploadOcrJob(pair.Value?.Job))
                yield return pair.Key;
        }
    }

    private static bool IsBulkUploadOcrJob(Hangfire.Common.Job? job)
    {
        if (job?.Type == null || string.IsNullOrWhiteSpace(job.Method?.Name))
            return false;

        if (!string.Equals(job.Method.Name, nameof(BulkUploadOcrJob.Execute), StringComparison.Ordinal))
            return false;

        return typeof(BulkUploadOcrJob).IsAssignableFrom(job.Type)
            || string.Equals(job.Type.Name, nameof(BulkUploadOcrJob), StringComparison.Ordinal);
    }

    /// <summary>
    /// Map stage status + promoted item into a stable UI phase.
    /// Indexed = exported via PUT index/{id} (promoted_item_id set).
    /// </summary>
    private static (string Phase, bool OcrCompleted, bool Indexed) ClassifyBulkFilePhase(
        string status,
        string? promotedItemId)
    {
        if (!string.IsNullOrWhiteSpace(promotedItemId))
            return ("Indexed", true, true);

        if (status.Contains("Fail", StringComparison.OrdinalIgnoreCase))
            return ("OcrFailed", false, false);

        if (string.Equals(status, "OCR", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "Indexing", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "Archiving", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "Indexed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "Archived", StringComparison.OrdinalIgnoreCase))
        {
            // Indexing/Archiving without promoted id = OCR done, export in progress or ready.
            var indexedByStatus =
                string.Equals(status, "Indexed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Archived", StringComparison.OrdinalIgnoreCase);
            return indexedByStatus
                ? ("Indexed", true, true)
                : ("OcrCompleted", true, false);
        }

        return ("PendingOcr", false, false);
    }

    private static string? ResolveHangfireState(string jobId)
    {
        try
        {
            var connection = JobStorage.Current.GetConnection();
            var state = connection.GetStateData(jobId);
            return state?.Name;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetHangfireExceptionMessage(string jobId)
    {
        try
        {
            var connection = JobStorage.Current.GetConnection();
            var state = connection.GetStateData(jobId);
            if (state?.Data != null
                && state.Data.TryGetValue("ExceptionMessage", out var message)
                && !string.IsNullOrWhiteSpace(message))
            {
                return message;
            }
        }
        catch
        {
            // Hangfire storage may be unavailable.
        }

        return null;
    }

    private static BulkOcrJobArgs? TryGetBulkOcrJobArgs(string jobId)
    {
        try
        {
            var details = JobStorage.Current.GetMonitoringApi().JobDetails(jobId);
            if (details?.Job?.Args == null || details.Job.Args.Count < 2)
                return null;

            var raw = details.Job.Args[1];
            if (raw is BulkOcrJobArgs typed)
                return typed;

            var json = JsonSerializer.Serialize(raw);
            return JsonSerializer.Deserialize<BulkOcrJobArgs>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static List<Guid> ParseBulkStageIds(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return new List<Guid>();

        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToList();
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
            var value = field.Value ?? string.Empty;
            if (IsEmptyOcrValue(value))
                continue;
            dict[field.Name.Trim()] = value.Trim();
        }

        return dict;
    }

    /// <summary>OCR often returns the literal string "null" for missing fields.</summary>
    private static bool IsEmptyOcrValue(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || value.Trim().Equals("null", StringComparison.OrdinalIgnoreCase);

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
                        fieldList = ParseFieldsList(listEl.GetRawText());
                        fieldValues = ParseFieldsToDictionary(fieldList);
                    }
                    else
                    {
                        // Full OCR payload pasted as metadata: { "ocrResult":[...], "ocr_text":"..." }
                        var fromOcr = OcrResultParser.TryParseFieldList(trimmed);
                        if (fromOcr is { Count: > 0 })
                        {
                            fieldList = fromOcr.ToList();
                            fieldValues = ParseFieldsToDictionary(fieldList);
                        }
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
                            || prop.NameEquals("ocrFieldList") || prop.NameEquals("OcrFieldList")
                            || prop.NameEquals("ocrResult") || prop.NameEquals("OcrResult")
                            || prop.NameEquals("tableResult") || prop.NameEquals("TableResult")
                            || prop.NameEquals("source_reference") || prop.NameEquals("ocr_status"))
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

        // Enrich from separate ocrJson form field (uploadForOcr response body).
        if (!string.IsNullOrWhiteSpace(ocrJson))
        {
            var fromOcrJson = OcrResultParser.TryParseFieldList(ocrJson);
            if (fromOcrJson is { Count: > 0 })
            {
                fieldList ??= fromOcrJson.ToList();
                foreach (var field in fromOcrJson)
                {
                    if (string.IsNullOrWhiteSpace(field.Name))
                        continue;
                    fieldValues.TryAdd(field.Name, field.Value ?? string.Empty);
                }
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

        // Prefer OCR-aware manual parse — STJ record deserialization often fails on
        // [{ "name", "value", "type" }] payloads from Swagger / uploadForOcr.
        var fromOcr = OcrResultParser.TryParseFieldList(trimmed);
        if (fromOcr is { Count: > 0 })
            return fromOcr.ToList();

        if (trimmed.StartsWith('{'))
        {
            var dict = RepositoryMetadataParser.Parse(trimmed);
            return dict.Count == 0
                ? null
                : dict.Select(kv => new UploadIndexFieldDto(kv.Key, kv.Value)).ToList();
        }

        if (!trimmed.StartsWith('['))
            return ParseFieldNamesFromPlainText(trimmed);

        try
        {
            return JsonSerializer.Deserialize<List<UploadIndexFieldDto>>(trimmed, JsonOptions);
        }
        catch (JsonException)
        {
            // Swagger sends each field JSON object as its own "fields" value, then we wrap those strings.
            try
            {
                var lines = JsonSerializer.Deserialize<List<string>>(trimmed, JsonOptions);
                if (lines == null)
                    return ParseFieldNamesFromPlainText(trimmed);

                var fromObjects = lines
                    .SelectMany(line => OcrResultParser.TryParseFieldList(line) ?? Array.Empty<UploadIndexFieldDto>())
                    .ToList();
                if (fromObjects.Count > 0)
                    return fromObjects;

                return lines
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
