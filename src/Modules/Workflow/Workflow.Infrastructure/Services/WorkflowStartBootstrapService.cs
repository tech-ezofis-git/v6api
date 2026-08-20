using System.Globalization;
using System.Text;
using System.Text.Json;
using Azure.Storage.Blobs;
using Npgsql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Application.Workflows;
using SaaSApp.Workflow.Domain.Entities;
using SaaSApp.Workflow.Domain.Enums;

namespace SaaSApp.Workflow.Infrastructure.Services;

public sealed class WorkflowStartBootstrapService : IWorkflowStartBootstrapService
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false
    };

    private readonly ITenantContext _tenantContext;
    private readonly IWorkflowLegacyTransactionSyncService _legacyTransactionSync;
    private readonly IWorkflowLegacyMailboxSyncService _legacyMailboxSync;
    private readonly IWorkflowRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWorkflowStartAttachmentUploader? _attachmentUploader;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WorkflowStartBootstrapService> _logger;

    public WorkflowStartBootstrapService(
        ITenantContext tenantContext,
        IWorkflowLegacyTransactionSyncService legacyTransactionSync,
        IWorkflowLegacyMailboxSyncService legacyMailboxSync,
        IWorkflowRepository repository,
        IUnitOfWork unitOfWork,
        IConfiguration configuration,
        ILogger<WorkflowStartBootstrapService> logger,
        IWorkflowStartAttachmentUploader? attachmentUploader = null)
    {
        _tenantContext = tenantContext;
        _legacyTransactionSync = legacyTransactionSync;
        _legacyMailboxSync = legacyMailboxSync;
        _repository = repository;
        _unitOfWork = unitOfWork;
        _configuration = configuration;
        _logger = logger;
        _attachmentUploader = attachmentUploader;
    }

    public async Task<WorkflowStartBootstrapResult> RunAsync(
        WorkflowStartBootstrapRequest request,
        CancellationToken cancellationToken = default)
    {
        var workflow = request.Workflow;
        var instance = request.Instance;
        var userId = request.UserId;
        var orderedSteps = workflow.Steps.OrderBy(s => s.Order).ToList();
        var startStep = orderedSteps.FirstOrDefault()
            ?? throw new InvalidOperationException("Workflow has no steps.");

        var apAgentStep = WorkflowStepTransitionHelper.ResolveApAgentStep(orderedSteps)
            ?? throw new InvalidOperationException(
                "No AP agent step found (StepName 'Ap Agent' or Order = 2).");

        var startActivityId = !string.IsNullOrWhiteSpace(startStep.ActivityId)
            ? startStep.ActivityId
            : startStep.Id.ToString("D");

        var reviewSync = await _legacyTransactionSync.SyncTransactionByActivityIdAsync(
            workflow.Id,
            instance.Id,
            instance.ReferenceNumber,
            startStep,
            orderedSteps,
            startActivityId,
            userId,
            startStep.AssignedToUserId ?? userId,
            WorkflowStepTransitionHelper.StartProceedReview,
            mailboxForm: null,
            cancellationToken);

        if (reviewSync.WorkflowInstanceId != instance.Id)
        {
            throw new InvalidOperationException(
                $"Transaction row was not linked to workflow instance {instance.Id:D}.");
        }

        if (reviewSync.Status is LegacyTransactionSyncStatus.ReviewUpdated
            or LegacyTransactionSyncStatus.ReviewAlreadyUpdated
            or LegacyTransactionSyncStatus.StepInserted
            or LegacyTransactionSyncStatus.StepAlreadyThere)
        {
            WorkflowStepTransitionHelper.CompleteStepInstance(instance, startStep.Id, userId);
            WorkflowStepTransitionHelper.StartStepInstance(instance, apAgentStep.Id);
            await _repository.UpdateInstanceAsync(instance, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        else if (WorkflowStepTransitionHelper.FindStepInstance(instance, apAgentStep.Id)?.Status
                 == StepInstanceStatus.Pending)
        {
            // Ensure AP agent is active even when legacy sync returned an unexpected status.
            WorkflowStepTransitionHelper.CompleteStepInstance(instance, startStep.Id, userId);
            WorkflowStepTransitionHelper.StartStepInstance(instance, apAgentStep.Id);
            await _repository.UpdateInstanceAsync(instance, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var currentTransactionId = reviewSync.NextTransactionId
            ?? reviewSync.CurrentTransactionId
            ?? request.StartTransactionId;

        var connectionString = _tenantContext.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        var workflowSuffix = workflow.Id.ToString("N")[..8];
        var repositoryGuid = await ResolveRepositoryGuidAsync(
            connectionString,
            instance.TenantId,
            workflow.RepositoryId,
            cancellationToken);

        string? blobPath = null;
        Guid? repositoryItemId = null;
        if (request.AttachmentStream != null
            && !string.IsNullOrWhiteSpace(request.AttachmentFileName)
            && _attachmentUploader != null)
        {
            if (repositoryGuid is Guid repoId)
            {
                var upload = await _attachmentUploader.UploadAsync(
                    instance.TenantId,
                    repoId,
                    workflow.Id,
                    instance.Id,
                    currentTransactionId,
                    request.AttachmentStream,
                    request.AttachmentFileName,
                    request.AttachmentContentType,
                    userId,
                    cancellationToken);
                if (upload != null)
                {
                    blobPath = upload.FilePath;
                    repositoryItemId = upload.RepositoryItemId;
                }
            }
            else
            {
                _logger.LogWarning(
                    "Start attachment skipped: workflow {WorkflowId} has no resolvable repository (RepositoryId={RepositoryId}).",
                    workflow.Id,
                    workflow.RepositoryId);
            }
        }

        var formEntryItemId = await InsertFormEntryAsync(
            connectionString,
            workflow.FormId,
            userId,
            cancellationToken);

        var transactionGuid = reviewSync.NextTransactionGuid
            ?? await ResolveTransactionGuidAsync(
                connectionString,
                workflowSuffix,
                currentTransactionId,
                cancellationToken);

        var apAgentStepInstance = WorkflowStepTransitionHelper.FindStepInstance(instance, apAgentStep.Id);

        var payload = BuildStartPayload(
            blobPath,
            request.EnvType ?? _configuration["WorkflowStart:EnvType"] ?? "trial",
            instance.TenantId,
            workflow.Id,
            repositoryGuid,
            repositoryItemId,
            instance.Id,
            transactionGuid,
            formEntryItemId,
            workflow.FormId);

        await InsertProcessFormRowAsync(
            connectionString,
            workflowSuffix,
            instance.Id,
            workflow.FormId,
            formEntryItemId,
            userId,
            cancellationToken);

        var formDataJson = JsonSerializer.Serialize(payload, PayloadJsonOptions);
        var wFormId = ResolveWFormIdInt(connectionString, workflow.FormId);

        await InsertWorkflowFormRowAsync(
            connectionString,
            workflowSuffix,
            instance.TenantId,
            instance.Id,
            apAgentStepInstance?.Id,
            wFormId,
            formEntryItemId,
            formDataJson,
            userId,
            cancellationToken);

        var blobRelativePath = await SavePayloadToBlobAsync(
            instance.TenantId,
            formDataJson,
            cancellationToken);

        var payloadDict = payload.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);

        // Start flow inserts repository/form linkage after the first transaction sync.
        // Re-sync current transaction so Inbox/Sent receives repositoryId/itemId/formId/formEntryId/formData.
        if (currentTransactionId is > 0)
        {
            await _legacyMailboxSync.SyncTransactionRowAsync(
                workflow.Id,
                currentTransactionId.Value,
                cancellationToken);
        }

        _logger.LogInformation(
            "Start bootstrap completed for instance {InstanceId}: transaction {TransactionId}, form entry {FormEntryId}",
            instance.Id,
            currentTransactionId,
            formEntryItemId);

        return new WorkflowStartBootstrapResult(
            reviewSync.CurrentTransactionId,
            currentTransactionId,
            formEntryItemId,
            apAgentStepInstance?.Id,
            formDataJson,
            blobRelativePath,
            payloadDict);
    }

    /// <summary>Blob / WorkflowForms FormData JSON (GUID strings for ids).</summary>
    private static Dictionary<string, object?> BuildStartPayload(
        string? blobPath,
        string envType,
        Guid tenantGuid,
        Guid workflowGuid,
        Guid? repositoryGuid,
        Guid? repositoryItemId,
        Guid instanceGuid,
        Guid? transactionGuid,
        int formEntryItemId,
        string? formTemplateId) =>
        new()
        {
            ["blobPath"] = blobPath ?? string.Empty,
            ["filepath"] = blobPath ?? string.Empty,
            ["envType"] = envType,
            ["tenantId"] = tenantGuid.ToString("D"),
            ["workflowId"] = workflowGuid.ToString("D"),
            ["repositoryId"] = repositoryGuid?.ToString("D") ?? string.Empty,
            ["itemId"] = repositoryItemId?.ToString("D") ?? string.Empty,
            ["repositoryItemId"] = repositoryItemId?.ToString("D") ?? string.Empty,
            ["instanceId"] = instanceGuid.ToString("D"),
            ["transactionId"] = transactionGuid?.ToString("D") ?? string.Empty,
            ["formentryId"] = formEntryItemId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["formId"] = formTemplateId ?? string.Empty,
            ["formid"] = formTemplateId ?? string.Empty
        };

    private async Task<int> InsertFormEntryAsync(
        string connectionString,
        string? formId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(formId))
            throw new InvalidOperationException("Workflow FormId is not configured. Set InitiateUsing.FormId on the workflow.");

        var tableSuffix = FormIdNaming.GetEzfbTableSuffix(formId);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        if (!await EzfbTableExistsAsync(connection, tableSuffix, cancellationToken))
            await EnsureMinimalEzfbTableAsync(connection, tableSuffix, cancellationToken);

        return await InsertEzfbItemRowAsync(connection, tableSuffix, userId, cancellationToken);
    }

    /// <summary>Minimal fallback shape matching FormService.cs's ezfb_{suffix}_items system columns (snake_case).</summary>
    private static async Task EnsureMinimalEzfbTableAsync(
        NpgsqlConnection connection,
        string tableSuffix,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            CREATE SCHEMA IF NOT EXISTS dbo;
            CREATE TABLE IF NOT EXISTS dbo.ezfb_{tableSuffix}_items (
                item_id integer GENERATED ALWAYS AS IDENTITY NOT NULL PRIMARY KEY,
                created_at varchar(50) NULL,
                modified_at varchar(50) NULL,
                created_by varchar(50) NOT NULL DEFAULT '0',
                modified_by varchar(50) NOT NULL DEFAULT '0',
                is_deleted boolean NOT NULL DEFAULT false
            );
            """;
        await using var cmd = new NpgsqlCommand(sql, connection) { CommandTimeout = 120 };
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> EzfbTableExistsAsync(
        NpgsqlConnection connection,
        string tableSuffix,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(1)
            FROM information_schema.tables
            WHERE table_schema = 'dbo' AND table_name = @TableName
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TableName", $"ezfb_{tableSuffix}_items");
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task<int> InsertEzfbItemRowAsync(
        NpgsqlConnection connection,
        string tableSuffix,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var table = $"dbo.ezfb_{tableSuffix}_items";
        var createdBy = userId.ToString("D");
        var sql = $"""
            INSERT INTO {table} (created_at, created_by, is_deleted)
            VALUES (to_char(now() AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"'), @CreatedBy, false)
            RETURNING item_id;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@CreatedBy", createdBy);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
    }

    /// <summary>
    /// PHASE 4: workflow.process_form_{suffix} is created by WorkflowTableCreator.cs's
    /// GenerateProcessFormTableScript with a single fixed modern shape from day one
    /// (workflow_instance_id uuid, w_form_id varchar(64), form_entry_id integer, no ProcessId
    /// column ever existed) -- there is no cross-install drift to detect or migrate away from on
    /// Postgres, so EnsureProcessFormTableAsync / MigrateProcessFormDropProcessIdAsync /
    /// MigrateProcessFormWFormIdToNvarcharAsync / DropIndexesOnColumnAsync were dropped entirely
    /// rather than translated (same precedent as FormService.cs). A matching CREATE TABLE IF NOT
    /// EXISTS is kept inline as a cheap safety net for the (normally publish-time-created) table.
    /// </summary>
    private static async Task InsertProcessFormRowAsync(
        string connectionString,
        string workflowSuffix,
        Guid workflowInstanceId,
        string? formId,
        int formEntryItemId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await EnsureProcessFormTableAsync(connection, workflowSuffix, cancellationToken);

        var table = $"workflow.process_form_{workflowSuffix}";
        var wFormIdValue = NormalizeFormIdForStorage(formId);

        var sql = $"""
            INSERT INTO {table}
                (workflow_instance_id, w_form_id, form_entry_id, created_at, created_by, is_deleted)
            VALUES
                (@WorkflowInstanceId, @WFormId, @FormEntryId, now(), @CreatedBy, false);
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
        cmd.Parameters.AddWithValue("@WFormId", (object?)wFormIdValue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@FormEntryId", formEntryItemId);
        cmd.Parameters.AddWithValue("@CreatedBy", userId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureProcessFormTableAsync(
        NpgsqlConnection connection,
        string workflowSuffix,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            CREATE SCHEMA IF NOT EXISTS workflow;
            CREATE TABLE IF NOT EXISTS workflow.process_form_{workflowSuffix} (
                id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                workflow_instance_id uuid NOT NULL,
                w_form_id varchar(64) NOT NULL,
                form_entry_id integer NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                created_by uuid NOT NULL,
                is_deleted boolean NOT NULL DEFAULT false
            );
            CREATE INDEX IF NOT EXISTS ix_process_form_{workflowSuffix}_workflow_instance_id_is_deleted ON workflow.process_form_{workflowSuffix} (workflow_instance_id, is_deleted);
            """;
        await using var cmd = new NpgsqlCommand(sql, connection) { CommandTimeout = 120 };
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? NormalizeFormIdForStorage(string? formId)
    {
        if (string.IsNullOrWhiteSpace(formId))
            return null;

        var trimmed = formId.Trim();
        return Guid.TryParse(trimmed, out var guid) ? guid.ToString("D") : trimmed;
    }

    private static async Task InsertWorkflowFormRowAsync(
        string connectionString,
        string workflowSuffix,
        Guid tenantId,
        Guid workflowInstanceId,
        Guid? stepInstanceId,
        int wFormId,
        int formEntryId,
        string formDataJson,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var table = $"workflow.workflow_forms_{workflowSuffix}";
        var sql = $"""
            INSERT INTO {table}
                (id, tenant_id, workflow_instance_id, step_instance_id, w_form_id, form_entry_id, form_data, has_form_pdf, created_at_utc, created_by, is_deleted)
            VALUES
                (gen_random_uuid(), @TenantId, @WorkflowInstanceId, @StepInstanceId, @WFormId, @FormEntryId, @FormData, false, now(), @CreatedBy, false);
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        cmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
        cmd.Parameters.AddWithValue("@StepInstanceId", (object?)stepInstanceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@WFormId", wFormId);
        cmd.Parameters.AddWithValue("@FormEntryId", formEntryId);
        cmd.Parameters.AddWithValue("@FormData", formDataJson);
        cmd.Parameters.AddWithValue("@CreatedBy", userId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<string?> SavePayloadToBlobAsync(
        Guid tenantId,
        string json,
        CancellationToken cancellationToken)
    {
        var connectionString = _configuration["EzofisBlobStorage:ConnectionString"]
            ?? _configuration["WorkflowJsonStorage:Blob:ConnectionString"]
            ?? _configuration["WorkflowJsonStorage:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogWarning(
                "Blob connection string is not configured (EzofisBlobStorage or WorkflowJsonStorage); start payload JSON was not saved to blob (Ap Agent Trial/*.json).");
            return null;
        }

        var containerPrefix = (_configuration["EzofisBlobStorage:ContainerPrefix"]
            ?? _configuration["WorkflowJsonStorage:Blob:ContainerPrefix"]
            ?? "ezts").ToLowerInvariant();
        var containerName = $"{containerPrefix}{tenantId:N}";
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        var blobPath = $"Ap Agent Trial/{timestamp}.json";

        var service = new BlobServiceClient(connectionString);
        var container = service.GetBlobContainerClient(containerName);
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        var client = container.GetBlobClient(blobPath);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await client.UploadAsync(stream, overwrite: true, cancellationToken);
        return blobPath;
    }

    private static int ResolveWFormIdInt(
        string? connectionString,
        string? formId)
    {
        _ = connectionString;
        if (string.IsNullOrWhiteSpace(formId))
            return 0;

        // workflow.workflow_forms_{suffix}.w_form_id is integer (see WorkflowTableCreator.cs).
        // v5 legacy tenants often store wForm.id as NVARCHAR(8) hex (or even other short ids),
        // while wFormControl.wFormId is INT, mapped from the trailing hex digits.
        // So we convert `formId` -> INT using the same hex extraction rule (last 8 hex digits).
        if (int.TryParse(formId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
            return numeric;

        var hex = new string(formId.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length == 0)
            return 0;

        if (hex.Length > 8)
            hex = hex[..8];

        var padded = hex.PadLeft(8, '0');
        if (uint.TryParse(padded, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var u))
            return unchecked((int)u);

        return 0;
    }

    private static async Task<Guid?> ResolveRepositoryGuidAsync(
        string connectionString,
        Guid tenantGuid,
        string? repositoryIdLink,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repositoryIdLink))
            return null;

        var trimmed = repositoryIdLink.Trim();
        if (Guid.TryParse(trimmed, out var parsed))
            return parsed;

        if (trimmed.Length == 32
            && Guid.TryParseExact(trimmed, "N", out parsed))
            return parsed;

        if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var legacyInt))
            return null;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string byTableSql = """
            SELECT "Id"
            FROM repository."Repositories"
            WHERE "TenantId" = @TenantId AND "IsDeleted" = false
              AND ("ItemsTableName" LIKE @LegacyPattern OR "StageTableName" LIKE @LegacyPattern)
            LIMIT 1;
            """;

        var legacyPattern = $"%_{legacyInt}_%";
        await using (var cmd = new NpgsqlCommand(byTableSql, connection))
        {
            cmd.Parameters.AddWithValue("@TenantId", tenantGuid);
            cmd.Parameters.AddWithValue("@LegacyPattern", legacyPattern);
            var o = await cmd.ExecuteScalarAsync(cancellationToken);
            if (o is Guid g)
                return g;
        }

        return null;
    }

    private static async Task<Guid?> ResolveTransactionGuidAsync(
        string connectionString,
        string workflowSuffix,
        int? transactionId,
        CancellationToken cancellationToken)
    {
        if (transactionId is not > 0)
            return null;

        var table = $"workflow.transaction_{workflowSuffix}";
        var sql = $"""
            SELECT transaction_guid
            FROM {table}
            WHERE id = @Id AND is_deleted = false;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", transactionId.Value);
        var o = await cmd.ExecuteScalarAsync(cancellationToken);
        if (o is Guid g)
            return g;
        if (o != null && o != DBNull.Value && Guid.TryParse(o.ToString(), out var parsed))
            return parsed;

        return null;
    }
}
