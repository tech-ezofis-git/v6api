using System.Globalization;
using System.Text;
using System.Text.Json;
using Azure.Storage.Blobs;
using Npgsql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Infrastructure.Options;
using SaaSApp.Workflow.Application.Forms;
using SaaSApp.Workflow.Application.Workflows;
using SaaSApp.Workflow.Application.Workflows.Commands.CreateWorkflow;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>v5 PostuploadMasterFile parity for SaaS tenants (GUID form/workflow ids, tenant blob container).</summary>
public sealed class FormMasterFileUploadService : IFormMasterFileUploadService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly ITenantContext _tenantContext;
    private readonly IWorkflowJsonStorageService _workflowJsonStorage;
    private readonly IConfiguration _configuration;
    private readonly FormMasterFileImportOptions _importOptions;
    private readonly IMasterFileImportPythonJobClient _masterFileImportJobClient;
    private readonly ILogger<FormMasterFileUploadService> _logger;

    public FormMasterFileUploadService(
        ITenantContext tenantContext,
        IWorkflowJsonStorageService workflowJsonStorage,
        IConfiguration configuration,
        IOptions<FormMasterFileImportOptions> importOptions,
        IMasterFileImportPythonJobClient masterFileImportJobClient,
        ILogger<FormMasterFileUploadService> logger)
    {
        _tenantContext = tenantContext;
        _workflowJsonStorage = workflowJsonStorage;
        _configuration = configuration;
        _importOptions = importOptions.Value;
        _masterFileImportJobClient = masterFileImportJobClient;
        _logger = logger;
    }

    public async Task<FormMasterFileUploadResult> UploadMasterFileAsync(
        FormMasterFileUploadRequest request,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (request.FileStream == null || !request.FileStream.CanRead)
            throw new ArgumentException("No file received.");

        if (string.IsNullOrWhiteSpace(request.FileName))
            throw new ArgumentException("File name is required.");

        var normalizedFormId = FormIdNaming.NormalizeFormId(request.FormId);
        if (string.IsNullOrWhiteSpace(normalizedFormId))
            throw new ArgumentException("formId is required.");

        var tenantGuid = _tenantContext.TenantId
            ?? throw new InvalidOperationException("Tenant context is required.");
        var connectionString = _tenantContext.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await EnsureMasterFileProcessTableAsync(connection, cancellationToken);

        var formMeta = await LoadFormAsync(connection, normalizedFormId, cancellationToken)
            ?? throw new InvalidOperationException("Form not found.");

        var tenantIntId = await EnsureTenantIntIdAsync(connection, tenantGuid, cancellationToken);
        var nowStamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var uploadStamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

        var safeFileName = Path.GetFileName(request.FileName);
        var fileExtension = Path.GetExtension(safeFileName);
        var fileType = string.IsNullOrWhiteSpace(fileExtension)
            ? "csv"
            : fileExtension.TrimStart('.').ToLowerInvariant();

        var blobRelativePath = $"Form Files/{normalizedFormId}/Master CSV/{uploadStamp}/{safeFileName}";
        var queueFilePath = $"Form Files/{normalizedFormId}/Master CSV/{uploadStamp}/{safeFileName}";

        await using var bufferStream = new MemoryStream();
        if (request.FileStream.CanSeek)
            request.FileStream.Position = 0;
        await request.FileStream.CopyToAsync(bufferStream, cancellationToken);
        var fileBytes = bufferStream.ToArray();
        var fileSize = request.FileSize > 0 ? request.FileSize : fileBytes.LongLength;

        var cloudFileServer = await SaveMasterFileAsync(
            tenantGuid,
            blobRelativePath,
            fileBytes,
            request.ContentType,
            cancellationToken);

        var workflowId = await ResolveWorkflowIdAsync(
            connection,
            tenantGuid,
            normalizedFormId,
            request.WorkflowId,
            cancellationToken);

        var instanceId = ParseOptionalGuid(request.InstanceId, "instanceId");

        var settingsJson = await BuildSettingsJsonAsync(
            normalizedFormId,
            workflowId,
            cancellationToken);

        var inputId = int.TryParse(normalizedFormId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var legacyFormId)
            ? legacyFormId
            : 0;

        var processId = await InsertMasterFileProcessAsync(
            connection,
            tenantIntId,
            inputId,
            safeFileName,
            Path.GetDirectoryName(blobRelativePath)?.Replace('\\', '/') ?? string.Empty,
            fileType,
            fileSize,
            cloudFileServer,
            settingsJson,
            workflowId,
            userId,
            nowStamp,
            cancellationToken);

        await FormMasterFileNotificationStore.EnsureTableAsync(connection, cancellationToken);
        var createdByLegacyId = await FormMasterFileNotificationStore.TryResolveLegacyUserIdAsync(
            connection,
            userId,
            cancellationToken);

        var notificationInputJson = new
        {
            workflowId = workflowId?.ToString("D") ?? string.Empty,
            instanceId = instanceId?.ToString("D") ?? string.Empty
        };

        var notificationId = await FormMasterFileNotificationStore.InsertAsync(
            connection,
            title: safeFileName,
            remarks: instanceId.HasValue
                ? $"Master file import queued for instance {instanceId.Value:D}."
                : $"Master file import queued (masterFileProcess {processId}).",
            notificationInputJson,
            category: _importOptions.NotificationCategory,
            createdByLegacyId,
            cancellationToken);

        var importPayload = BuildImportPayload(
            tenantGuid,
            processId,
            normalizedFormId,
            safeFileName,
            queueFilePath,
            formMeta.UniqueColumns,
            userId,
            workflowId,
            instanceId,
            settingsJson,
            notificationId);

        string? hangfireJobId = null;
        if (_importOptions.Enabled
            && _importOptions.UseHangfirePython
            && !string.IsNullOrWhiteSpace(_importOptions.PythonServiceUrl))
        {
            var payloadJson = JsonSerializer.Serialize(importPayload, JsonOptions);
            hangfireJobId = await _masterFileImportJobClient.EnqueueAsync(
                new MasterFileImportPythonJobArgs(
                    tenantGuid,
                    userId,
                    processId,
                    notificationId,
                    payloadJson),
                cancellationToken);

            _logger.LogInformation(
                "Enqueued master file import Hangfire job {JobId} for process {ProcessId}",
                hangfireJobId,
                processId);
        }

        if (_importOptions.Enabled && _importOptions.QueueBlobEnabled)
        {
            await QueueMasterFileImportAsync(
                tenantGuid,
                processId,
                normalizedFormId,
                queueFilePath,
                formMeta.UniqueColumns,
                userId,
                workflowId,
                instanceId,
                settingsJson,
                notificationId,
                cancellationToken);
        }

        _logger.LogInformation(
            "Master file uploaded for form {FormId}, masterFileprocess id {ProcessId}, notification {NotificationId}, path {Path}",
            normalizedFormId,
            processId,
            notificationId,
            blobRelativePath);

        return new FormMasterFileUploadResult(processId, blobRelativePath, notificationId, hangfireJobId);
    }

    private static Guid? ParseOptionalGuid(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (Guid.TryParse(value.Trim(), out var guid) && guid != Guid.Empty)
            return guid;

        throw new ArgumentException($"{fieldName} must be a GUID.");
    }

    private static object BuildImportPayload(
        Guid tenantGuid,
        int masterFileProcessId,
        string formId,
        string displayFileName,
        string blobFilePath,
        string? uniqueColumns,
        Guid userId,
        Guid? workflowId,
        Guid? instanceId,
        string settingsJson,
        int notificationId)
    {
        var conditionColumns = string.IsNullOrWhiteSpace(uniqueColumns)
            ? new[] { "entryId" }
            : uniqueColumns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new
        {
            fileName = displayFileName,
            filepath = blobFilePath,
            id = masterFileProcessId.ToString(CultureInfo.InvariantCulture),
            masterFileProcessId,
            formId,
            tenantId = tenantGuid.ToString("D"),
            conditionColumn = conditionColumns,
            userid = userId.ToString("D"),
            workflowId = workflowId?.ToString("D") ?? string.Empty,
            instanceId = instanceId?.ToString("D") ?? string.Empty,
            settingsJson,
            notifyId = notificationId
        };
    }

    private async Task<string> SaveMasterFileAsync(
        Guid tenantGuid,
        string blobRelativePath,
        byte[] fileBytes,
        string? contentType,
        CancellationToken cancellationToken)
    {
        if (TryGetBlobClient(tenantGuid, blobRelativePath, out var blobClient))
        {
            using var stream = new MemoryStream(fileBytes);
            var headers = new Azure.Storage.Blobs.Models.BlobHttpHeaders();
            if (!string.IsNullOrWhiteSpace(contentType))
                headers.ContentType = contentType;
            await blobClient.UploadAsync(stream, new Azure.Storage.Blobs.Models.BlobUploadOptions
            {
                HttpHeaders = headers
            }, cancellationToken);
            return "BLOB";
        }

        var localPath = Path.Combine(AppContext.BaseDirectory, tenantGuid.ToString("D"), blobRelativePath);
        var directory = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(localPath, fileBytes, cancellationToken);
        return "LOCAL";
    }

    private async Task QueueMasterFileImportAsync(
        Guid tenantGuid,
        int processId,
        string formId,
        string filePath,
        string? uniqueColumns,
        Guid userId,
        Guid? workflowId,
        Guid? instanceId,
        string settingsJson,
        int notificationId,
        CancellationToken cancellationToken)
    {
        if (!_importOptions.Enabled)
            return;

        var queuePayload = BuildImportPayload(
            tenantGuid,
            processId,
            formId,
            Path.GetFileName(filePath),
            filePath,
            uniqueColumns,
            userId,
            workflowId,
            instanceId,
            settingsJson,
            notificationId);

        var jsonData = JsonSerializer.Serialize(queuePayload, JsonOptions);
        var queuePrefix = _importOptions.QueueBlobPathPrefix;
        var queueBlobPath = $"{queuePrefix}/{tenantGuid:N}_{processId}_{DateTime.UtcNow:yyyyMMddHHmmssfff}.json";

        if (TryGetBlobClient(tenantGuid, queueBlobPath, out var blobClient))
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(jsonData));
            await blobClient.UploadAsync(stream, overwrite: true, cancellationToken);
            _logger.LogInformation("Queued master file import JSON at {BlobPath}", queueBlobPath);
            return;
        }

        var localQueuePath = Path.Combine(AppContext.BaseDirectory, tenantGuid.ToString("D"), queueBlobPath);
        var directory = Path.GetDirectoryName(localQueuePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(localQueuePath, jsonData, cancellationToken);
        _logger.LogInformation("Queued master file import JSON locally at {Path}", localQueuePath);
    }

    private async Task<string> BuildSettingsJsonAsync(
        string formId,
        Guid? workflowId,
        CancellationToken cancellationToken)
    {
        string? prefix = null;
        int? mlMasterFormId = null;
        int wFormIdLegacy = 0;
        int autoGenNo = 0;

        if (workflowId is Guid wfId)
        {
            var workflowJson = await _workflowJsonStorage.GetWorkflowJsonAsync(wfId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(workflowJson))
            {
                try
                {
                    var dto = JsonSerializer.Deserialize<WorkflowJsonDto>(workflowJson, WorkflowJsonSerializerOptions.Storage);
                    prefix = dto?.Settings?.General?.ProcessNumberPrefix;
                    var startBlock = dto?.Blocks?.FirstOrDefault(b =>
                        string.Equals(b.Type, "START", StringComparison.OrdinalIgnoreCase));
                    mlMasterFormId = startBlock?.Settings?.MlMasterFormId;
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Could not parse workflow JSON for workflow {WorkflowId}", wfId);
                }
            }
        }

        if (int.TryParse(formId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var legacyFormId))
            wFormIdLegacy = legacyFormId;

        var settings = new
        {
            masterFormId = formId,
            formGuid = formId,
            wFormId = wFormIdLegacy,
            workflowId = workflowId?.ToString("D") ?? string.Empty,
            requestNo = prefix ?? string.Empty,
            autoGenNo,
            mLMasterId = mlMasterFormId ?? 0
        };

        return JsonSerializer.Serialize(settings, JsonOptions);
    }

    private static async Task<Guid?> ResolveWorkflowIdAsync(
        NpgsqlConnection connection,
        Guid tenantGuid,
        string formId,
        string? workflowIdFromRequest,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(workflowIdFromRequest))
        {
            if (Guid.TryParse(workflowIdFromRequest, out var wfGuid))
                return wfGuid;

            throw new ArgumentException("workflowId must be a GUID.");
        }

        if (!await TableExistsAsync(connection, "WorkflowInitiateInfo", "workflow", cancellationToken))
            return null;

        const string sql = """
            SELECT "WorkflowId"
            FROM workflow."WorkflowInitiateInfo"
            WHERE "TenantId" = @TenantId
              AND (
                    "InputJson" LIKE @FormGuidPattern
                 OR "InputJson" LIKE @FormGuidPattern2
                 OR "InputJson" LIKE @LegacyFormPattern
              )
            ORDER BY "Id" DESC
            LIMIT 1;
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TenantId", tenantGuid);
        cmd.Parameters.AddWithValue("@FormGuidPattern", $"%\"formGuid\":\"{formId}\"%");
        cmd.Parameters.AddWithValue("@FormGuidPattern2", $"%\"masterFormId\":\"{formId}\"%");
        cmd.Parameters.AddWithValue("@LegacyFormPattern", $"%\"masterFormId\":{formId}%");

        var scalar = await cmd.ExecuteScalarAsync(cancellationToken);
        if (scalar is Guid guid)
            return guid;
        if (scalar is not null && scalar != DBNull.Value && Guid.TryParse(scalar.ToString(), out var parsed))
            return parsed;

        return null;
    }

    private sealed record FormMasterMeta(string FormId, string? UniqueColumns);

    private static async Task<FormMasterMeta?> LoadFormAsync(
        NpgsqlConnection connection,
        string formId,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "wForm", "dbo", cancellationToken))
            return null;

        const string sql = """
            SELECT id, "uniqueColumns"
            FROM dbo."wForm"
            WHERE id = @FormId AND "isDeleted" = false
            LIMIT 1;
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@FormId", formId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var id = reader.GetString(0);
        var uniqueColumns = reader.IsDBNull(1) ? null : reader.GetString(1);
        return new FormMasterMeta(id, uniqueColumns);
    }

    private static async Task<int> InsertMasterFileProcessAsync(
        NpgsqlConnection connection,
        int tenantIntId,
        int inputId,
        string fileName,
        string filePath,
        string fileType,
        long fileSize,
        string cloudFileServer,
        string settingsJson,
        Guid? workflowId,
        Guid userId,
        string createdAt,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo."masterFileprocess" (
                "tenantId", "inputType", "inputId", "totalRows", "fileName", "filePath", "fileType", "fileSize",
                status, remarks, "cloudFileServer", "createdAt", "createdBy", "isDeleted", "settingsJson", "workflowId")
            VALUES (
                @TenantId, @InputType, @InputId, 0, @FileName, @FilePath, @FileType, @FileSize,
                0, '', @CloudFileServer, @CreatedAt, @CreatedBy, false, @SettingsJson, @WorkflowId)
            RETURNING id;
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TenantId", tenantIntId);
        cmd.Parameters.AddWithValue("@InputType", "FORM");
        cmd.Parameters.AddWithValue("@InputId", inputId);
        cmd.Parameters.AddWithValue("@FileName", fileName);
        cmd.Parameters.AddWithValue("@FilePath", filePath);
        cmd.Parameters.AddWithValue("@FileType", fileType);
        cmd.Parameters.AddWithValue("@FileSize", fileSize);
        cmd.Parameters.AddWithValue("@CloudFileServer", cloudFileServer);
        cmd.Parameters.AddWithValue("@CreatedAt", createdAt);
        cmd.Parameters.AddWithValue("@CreatedBy", userId.ToString("D"));
        cmd.Parameters.AddWithValue("@SettingsJson", settingsJson);
        cmd.Parameters.AddWithValue(
            "@WorkflowId",
            workflowId.HasValue ? workflowId.Value.ToString("D") : (object)DBNull.Value);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// PHASE 4: dbo.masterFileprocess is always created fresh on Postgres tenant DBs with
    /// "workflowId" already varchar(50) from day one (see CREATE TABLE below) — the SQL Server
    /// EnsureMasterFileProcessWorkflowIdIsGuidAsync legacy-drift-detection (int-typed workflowId
    /// on older installs, ALTER COLUMN to widen it) has nothing to detect here, so it was dropped
    /// rather than translated. Same precedent as FormService.cs / ResolveWFormIdParameterAsync.
    /// </summary>
    private static async Task EnsureMasterFileProcessTableAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string createSql = """
            CREATE SCHEMA IF NOT EXISTS dbo;
            CREATE TABLE IF NOT EXISTS dbo."masterFileprocess" (
                id integer GENERATED ALWAYS AS IDENTITY NOT NULL PRIMARY KEY,
                "tenantId" integer NOT NULL,
                "inputType" varchar(500) NULL,
                "inputId" integer NOT NULL,
                "totalRows" bigint NOT NULL DEFAULT 0,
                "fileName" varchar(500) NULL,
                "filePath" varchar(1000) NULL,
                "fileType" varchar(50) NULL,
                "fileSize" bigint NULL,
                status integer NOT NULL DEFAULT 0,
                remarks text NULL,
                "cloudFileServer" varchar(100) NULL,
                "createdAt" varchar(50) NULL,
                "modifiedAt" varchar(50) NULL,
                "createdBy" varchar(50) NULL,
                "modifiedBy" varchar(50) NULL,
                "isDeleted" boolean NOT NULL DEFAULT false,
                "settingsJson" text NULL,
                "workflowId" varchar(50) NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_masterFileprocess_tenantId" ON dbo."masterFileprocess"("tenantId");
            CREATE INDEX IF NOT EXISTS "IX_masterFileprocess_workflowId" ON dbo."masterFileprocess"("workflowId");
            """;

        await using var createCmd = new NpgsqlCommand(createSql, connection) { CommandTimeout = 120 };
        await createCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// dbo."TenantIdMap" is retained (not eliminated like other legacy-drift shims) because the
    /// external Python master-file-import job reads dbo.masterFileprocess."tenantId" as a small
    /// integer for v5 parity — this side table is the source of that integer, not drift detection.
    /// </summary>
    private static async Task<int> EnsureTenantIntIdAsync(NpgsqlConnection conn, Guid tenantGuid, CancellationToken cancellationToken)
    {
        const string ensureSql = """
            CREATE TABLE IF NOT EXISTS dbo."TenantIdMap"(
                "TenantGuid" uuid NOT NULL PRIMARY KEY,
                "TenantIntId" integer GENERATED ALWAYS AS IDENTITY NOT NULL UNIQUE,
                "CreatedAtUtc" timestamptz NOT NULL DEFAULT now()
            );
            """;
        await using (var ensureCmd = new NpgsqlCommand(ensureSql, conn) { CommandTimeout = 120 })
            await ensureCmd.ExecuteNonQueryAsync(cancellationToken);

        const string getSql = """SELECT "TenantIntId" FROM dbo."TenantIdMap" WHERE "TenantGuid" = @TenantGuid""";
        await using (var getCmd = new NpgsqlCommand(getSql, conn))
        {
            getCmd.Parameters.AddWithValue("@TenantGuid", tenantGuid);
            var existing = await getCmd.ExecuteScalarAsync(cancellationToken);
            if (existing != null && existing != DBNull.Value)
                return Convert.ToInt32(existing, CultureInfo.InvariantCulture);
        }

        const string insertSql = """
            INSERT INTO dbo."TenantIdMap"("TenantGuid") VALUES(@TenantGuid)
            ON CONFLICT ("TenantGuid") DO NOTHING
            RETURNING "TenantIntId";
            """;
        await using var insertCmd = new NpgsqlCommand(insertSql, conn);
        insertCmd.Parameters.AddWithValue("@TenantGuid", tenantGuid);
        var inserted = await insertCmd.ExecuteScalarAsync(cancellationToken);
        if (inserted != null && inserted != DBNull.Value)
            return Convert.ToInt32(inserted, CultureInfo.InvariantCulture);

        // Concurrent insert from another request won the race; re-read.
        await using var reGetCmd = new NpgsqlCommand(getSql, conn);
        reGetCmd.Parameters.AddWithValue("@TenantGuid", tenantGuid);
        return Convert.ToInt32(await reGetCmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private bool TryGetBlobClient(Guid tenantGuid, string blobPath, out BlobClient blobClient)
    {
        blobClient = null!;
        var connectionString = _configuration["EzofisBlobStorage:ConnectionString"]
            ?? _configuration["WorkflowJsonStorage:Blob:ConnectionString"]
            ?? _configuration["WorkflowJsonStorage:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
            return false;

        var containerPrefix = (_configuration["EzofisBlobStorage:ContainerPrefix"]
            ?? _configuration["WorkflowJsonStorage:Blob:ContainerPrefix"]
            ?? "ezts").ToLowerInvariant();
        var containerName = $"{containerPrefix}{tenantGuid:N}".ToLowerInvariant();
        var normalizedPath = blobPath.Replace('\\', '/').TrimStart('/');

        var service = new BlobServiceClient(connectionString);
        var container = service.GetBlobContainerClient(containerName);
        container.CreateIfNotExists();
        blobClient = container.GetBlobClient(normalizedPath);
        return true;
    }

    private static async Task<bool> TableExistsAsync(
        NpgsqlConnection connection,
        string tableName,
        string schema,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT 1 FROM information_schema.tables
            WHERE table_name = @TableName AND table_schema = @Schema;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TableName", tableName);
        cmd.Parameters.AddWithValue("@Schema", schema);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result != null && result != DBNull.Value;
    }
}
