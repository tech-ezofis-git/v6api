using System.Globalization;
using System.Text;
using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.Data.SqlClient;
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
    private readonly IWorkflowApAgentMoveNextService _apAgentMoveNext;
    private readonly IWorkflowEzfbFormDataLoader _ezfbFormDataLoader;
    private readonly IWorkflowAttachmentArchiveService? _attachmentArchive;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WorkflowStartBootstrapService> _logger;

    public WorkflowStartBootstrapService(
        ITenantContext tenantContext,
        IWorkflowLegacyTransactionSyncService legacyTransactionSync,
        IWorkflowLegacyMailboxSyncService legacyMailboxSync,
        IWorkflowRepository repository,
        IUnitOfWork unitOfWork,
        IWorkflowApAgentMoveNextService apAgentMoveNext,
        IWorkflowEzfbFormDataLoader ezfbFormDataLoader,
        IConfiguration configuration,
        ILogger<WorkflowStartBootstrapService> logger,
        IWorkflowStartAttachmentUploader? attachmentUploader = null,
        IWorkflowAttachmentArchiveService? attachmentArchive = null)
    {
        _tenantContext = tenantContext;
        _legacyTransactionSync = legacyTransactionSync;
        _legacyMailboxSync = legacyMailboxSync;
        _repository = repository;
        _unitOfWork = unitOfWork;
        _apAgentMoveNext = apAgentMoveNext;
        _ezfbFormDataLoader = ezfbFormDataLoader;
        _configuration = configuration;
        _logger = logger;
        _attachmentUploader = attachmentUploader;
        _attachmentArchive = attachmentArchive;
    }

    public async Task<WorkflowStartBootstrapResult> RunAsync(
        WorkflowStartBootstrapRequest request,
        CancellationToken cancellationToken = default)
    {
        var orderedSteps = request.Workflow.Steps.OrderBy(s => s.Order).ToList();
        var startStep = orderedSteps.FirstOrDefault()
            ?? throw new InvalidOperationException("Workflow has no steps.");

        var dedicatedApAgent = WorkflowStepTransitionHelper.TryResolveDedicatedApAgentStep(orderedSteps);
        if (dedicatedApAgent == null)
        {
            return await RunNormalBootstrapAsync(request, orderedSteps, startStep, cancellationToken);
        }

        return await RunApAgentBootstrapAsync(request, orderedSteps, startStep, dedicatedApAgent, cancellationToken);
    }

    private async Task<WorkflowStartBootstrapResult> RunApAgentBootstrapAsync(
        WorkflowStartBootstrapRequest request,
        IReadOnlyList<WorkflowStep> orderedSteps,
        WorkflowStep startStep,
        WorkflowStep apAgentStep,
        CancellationToken cancellationToken)
    {
        var workflow = request.Workflow;
        var instance = request.Instance;
        var userId = request.UserId;

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

    private async Task<WorkflowStartBootstrapResult> RunNormalBootstrapAsync(
        WorkflowStartBootstrapRequest request,
        IReadOnlyList<WorkflowStep> orderedSteps,
        WorkflowStep startStep,
        CancellationToken cancellationToken)
    {
        var workflow = request.Workflow;
        var instance = request.Instance;
        var userId = request.UserId;

        var connectionString = _tenantContext.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        var workflowSuffix = workflow.Id.ToString("N")[..8];
        var repositoryGuid = await ResolveRepositoryGuidAsync(
            connectionString,
            instance.TenantId,
            workflow.RepositoryId,
            cancellationToken);

        var formEntryItemId = await InsertFormEntryAsync(
            connectionString,
            workflow.FormId,
            userId,
            cancellationToken);

        if (request.FormDataFields is { Count: > 0 } || !string.IsNullOrWhiteSpace(request.FormLineItemsJson))
        {
            await _apAgentMoveNext.ApplyFormDataToEzfbAsync(
                workflow.FormId!,
                formEntryItemId,
                request.FormDataFields ?? new Dictionary<string, string>(),
                request.FormLineItemsJson,
                cancellationToken);
        }

        var mailboxForm = await BuildMailboxFormSnapshotAsync(workflow.FormId, formEntryItemId, cancellationToken);

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
            mailboxForm,
            cancellationToken);

        if (reviewSync.WorkflowInstanceId != instance.Id)
        {
            throw new InvalidOperationException(
                $"Transaction row was not linked to workflow instance {instance.Id:D}.");
        }

        var nextDefinitionStep = WorkflowStepActionsHelper.ResolveNextStepByReview(
                startStep,
                WorkflowStepTransitionHelper.StartProceedReview,
                orderedSteps)
            ?? orderedSteps.FirstOrDefault(s => s.Order > startStep.Order);

        if (reviewSync.Status is LegacyTransactionSyncStatus.ReviewUpdated
            or LegacyTransactionSyncStatus.ReviewAlreadyUpdated
            or LegacyTransactionSyncStatus.StepInserted
            or LegacyTransactionSyncStatus.StepAlreadyThere)
        {
            WorkflowStepTransitionHelper.CompleteStepInstance(instance, startStep.Id, userId);
            if (nextDefinitionStep != null && !reviewSync.WorkflowCompleted)
                WorkflowStepTransitionHelper.StartStepInstance(instance, nextDefinitionStep.Id);
            await _repository.UpdateInstanceAsync(instance, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var currentTransactionId = reviewSync.NextTransactionId
            ?? reviewSync.CurrentTransactionId
            ?? request.StartTransactionId;

        Guid? repositoryItemId = null;
        string? blobPath = null;

        if (_attachmentArchive != null && request.StagedFiles is { Count: > 0 })
        {
            foreach (var staged in request.StagedFiles.Where(s => s.RepositoryId != Guid.Empty && s.FileId != Guid.Empty))
            {
                var archived = await _attachmentArchive.PromoteFromStageAsync(
                    instance.TenantId,
                    workflow.Id,
                    instance.Id,
                    staged.RepositoryId,
                    staged.FileId,
                    currentTransactionId,
                    userId,
                    cancellationToken);
                if (archived != null)
                {
                    repositoryItemId ??= archived.ItemId;
                    blobPath ??= archived.FilePath;
                }
            }
        }

        if (request.AttachmentStream != null
            && !string.IsNullOrWhiteSpace(request.AttachmentFileName)
            && _attachmentArchive != null
            && repositoryGuid is Guid repoId)
        {
            var archived = await _attachmentArchive.UploadAsync(
                instance.TenantId,
                workflow.Id,
                instance.Id,
                repoId,
                request.AttachmentStream,
                request.AttachmentFileName,
                request.AttachmentContentType,
                request.AttachmentStream.CanSeek ? request.AttachmentStream.Length : null,
                metadataJson: null,
                currentTransactionId,
                userId,
                cancellationToken);
            repositoryItemId ??= archived.ItemId;
            blobPath ??= archived.FilePath;
        }

        await InsertProcessFormRowAsync(
            connectionString,
            workflowSuffix,
            instance.Id,
            workflow.FormId,
            formEntryItemId,
            userId,
            cancellationToken);

        var transactionGuid = reviewSync.NextTransactionGuid
            ?? await ResolveTransactionGuidAsync(
                connectionString,
                workflowSuffix,
                currentTransactionId,
                cancellationToken);

        var nextStepInstance = nextDefinitionStep != null
            ? WorkflowStepTransitionHelper.FindStepInstance(instance, nextDefinitionStep.Id)
            : null;

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

        var formDataJson = JsonSerializer.Serialize(payload, PayloadJsonOptions);
        var wFormId = ResolveWFormIdInt(connectionString, workflow.FormId);

        await InsertWorkflowFormRowAsync(
            connectionString,
            workflowSuffix,
            instance.TenantId,
            instance.Id,
            nextStepInstance?.Id,
            wFormId,
            formEntryItemId,
            formDataJson,
            userId,
            cancellationToken);

        var blobRelativePath = await SavePayloadToBlobAsync(
            instance.TenantId,
            formDataJson,
            cancellationToken);

        if (reviewSync.CurrentTransactionId is > 0)
        {
            await _legacyMailboxSync.SyncTransactionRowAsync(
                workflow.Id,
                reviewSync.CurrentTransactionId.Value,
                cancellationToken);
        }

        if (reviewSync.NextTransactionId is > 0)
        {
            await _legacyMailboxSync.SyncTransactionRowAsync(
                workflow.Id,
                reviewSync.NextTransactionId.Value,
                cancellationToken);
        }

        _logger.LogInformation(
            "Normal start bootstrap completed for instance {InstanceId}: transaction {TransactionId}, form entry {FormEntryId}, next step {NextStep}",
            instance.Id,
            currentTransactionId,
            formEntryItemId,
            nextDefinitionStep?.Name);

        return new WorkflowStartBootstrapResult(
            reviewSync.CurrentTransactionId,
            currentTransactionId,
            formEntryItemId,
            nextStepInstance?.Id,
            formDataJson,
            blobRelativePath,
            payload.ToDictionary(kv => kv.Key, kv => (object?)kv.Value));
    }

    private async Task<MailboxFormSnapshot?> BuildMailboxFormSnapshotAsync(
        string? formId,
        int formEntryItemId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(formId) || formEntryItemId <= 0)
            return null;

        var formDataJson = await _ezfbFormDataLoader.LoadFormDataJsonAsync(
            formId,
            formEntryItemId,
            cancellationToken);

        return string.IsNullOrWhiteSpace(formDataJson)
            ? null
            : new MailboxFormSnapshot(formId, formEntryItemId, formDataJson);
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
            ["envType"] = envType,
            ["tenantId"] = tenantGuid.ToString("D"),
            ["workflowId"] = workflowGuid.ToString("D"),
            ["repositoryId"] = repositoryGuid?.ToString("D") ?? string.Empty,
            ["itemId"] = repositoryItemId?.ToString("D") ?? string.Empty,
            ["repositoryItemId"] = repositoryItemId?.ToString("D") ?? string.Empty,
            ["instanceId"] = instanceGuid.ToString("D"),
            ["transactionId"] = transactionGuid?.ToString("D") ?? string.Empty,
            ["formentryId"] = formEntryItemId,
            ["formId"] = formTemplateId ?? string.Empty
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
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        if (!await EzfbTableExistsAsync(connection, tableSuffix, cancellationToken))
            await EnsureMinimalEzfbTableAsync(connection, tableSuffix, cancellationToken);

        return await InsertEzfbItemRowAsync(connection, tableSuffix, userId, cancellationToken);
    }

    private static async Task EnsureMinimalEzfbTableAsync(
        SqlConnection connection,
        string tableSuffix,
        CancellationToken cancellationToken)
    {
        var table = $"dbo.[ezfb_{tableSuffix}_items]";
        var sql = $@"
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'ezfb_{tableSuffix}_items' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE {table} (
        itemId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        createdAt NVARCHAR(50) NULL,
        modifiedAt NVARCHAR(50) NULL,
        createdBy NVARCHAR(50) NOT NULL CONSTRAINT DF_ezfb_{tableSuffix}_createdBy DEFAULT('0'),
        modifiedBy NVARCHAR(50) NOT NULL CONSTRAINT DF_ezfb_{tableSuffix}_modifiedBy DEFAULT('0'),
        isDeleted BIT NOT NULL CONSTRAINT DF_ezfb_{tableSuffix}_isDeleted DEFAULT(0)
    );
END";
        await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> EzfbTableExistsAsync(
        SqlConnection connection,
        string tableSuffix,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(1)
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = @TableName
            """;
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TableName", $"ezfb_{tableSuffix}_items");
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task<int> InsertEzfbItemRowAsync(
        SqlConnection connection,
        string tableSuffix,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var table = $"dbo.[ezfb_{tableSuffix}_items]";
        var createdBy = userId.ToString("D");
        var sql = $@"
INSERT INTO {table} (createdAt, createdBy, isDeleted)
OUTPUT INSERTED.itemId
VALUES (CONVERT(NVARCHAR(50), SYSUTCDATETIME(), 127), @CreatedBy, 0);";
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@CreatedBy", createdBy);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
    }

    /// <summary>
    /// Legacy workflow.processForm_{suffix}: FormEntryId = ezfb itemId; WorkflowInstanceId = instance (no ProcessId).
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
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await EnsureProcessFormTableAsync(connection, workflowSuffix, cancellationToken);

        const string schema = "workflow";
        var tableName = $"processForm_{workflowSuffix}";
        var table = $"workflow.[{tableName}]";

        var wFormIdSqlType = await GetColumnSqlTypeAsync(connection, schema, tableName, "WFormId", cancellationToken);
        var wFormIdValue = CoerceWFormIdValue(formId, wFormIdSqlType);

        var createdAtCol = await ColumnExistsAsync(connection, schema, tableName, "CreatedAt", cancellationToken)
            ? "CreatedAt"
            : "CreatedAtUtc";

        var createdBySqlType = await GetColumnSqlTypeAsync(connection, schema, tableName, "CreatedBy", cancellationToken);
        var createdByValue = createdBySqlType == "uniqueidentifier"
            ? (object)userId
            : userId.ToString("D");

        var hasWorkflowInstanceId = await ColumnExistsAsync(connection, schema, tableName, "WorkflowInstanceId", cancellationToken);
        var hasProcessId = await ColumnExistsAsync(connection, schema, tableName, "ProcessId", cancellationToken);

        var cols = new List<string>();
        var vals = new List<string>();
        if (hasWorkflowInstanceId)
        {
            cols.Add("WorkflowInstanceId");
            vals.Add("@WorkflowInstanceId");
        }
        else if (hasProcessId)
        {
            cols.Add("ProcessId");
            vals.Add("@ProcessId");
        }

        cols.AddRange(["WFormId", "FormEntryId", createdAtCol, "CreatedBy", "IsDeleted"]);
        vals.AddRange(["@WFormId", "@FormEntryId", "SYSUTCDATETIME()", "@CreatedBy", "0"]);

        var sql = $@"
INSERT INTO {table}
    ({string.Join(", ", cols)})
VALUES
    ({string.Join(", ", vals)});";

        await using var cmd = new SqlCommand(sql, connection);
        if (hasWorkflowInstanceId)
            cmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
        else if (hasProcessId)
            cmd.Parameters.AddWithValue("@ProcessId", 0);
        cmd.Parameters.AddWithValue("@WFormId", wFormIdValue);
        cmd.Parameters.AddWithValue("@FormEntryId", formEntryItemId);
        cmd.Parameters.AddWithValue("@CreatedBy", createdByValue);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureProcessFormTableAsync(
        SqlConnection connection,
        string workflowSuffix,
        CancellationToken cancellationToken)
    {
        var tableName = $"processForm_{workflowSuffix}";
        if (await TableExistsAsync(connection, "workflow", tableName, cancellationToken))
        {
            const string schema = "workflow";
            var hasWorkflowInstanceId = await ColumnExistsAsync(connection, schema, tableName, "WorkflowInstanceId", cancellationToken);
            var hasProcessId = await ColumnExistsAsync(connection, schema, tableName, "ProcessId", cancellationToken);
            if (hasWorkflowInstanceId && !hasProcessId)
                return;

            await MigrateProcessFormDropProcessIdAsync(connection, workflowSuffix, cancellationToken);
            return;
        }

        var idx = workflowSuffix.Replace("-", "_");
        var sql = $@"
CREATE TABLE workflow.[{tableName}] (
    Id INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    WorkflowInstanceId UNIQUEIDENTIFIER NOT NULL,
    WFormId NVARCHAR(64) NOT NULL,
    FormEntryId INT NOT NULL,
    CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_processForm_{idx}_CreatedAt DEFAULT (SYSUTCDATETIME()),
    CreatedBy UNIQUEIDENTIFIER NOT NULL,
    IsDeleted BIT NOT NULL CONSTRAINT DF_processForm_{idx}_IsDeleted DEFAULT (0)
);
CREATE INDEX IX_processForm_{idx}_WorkflowInstanceId_IsDeleted
    ON workflow.[{tableName}](WorkflowInstanceId, IsDeleted);";
        await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MigrateProcessFormDropProcessIdAsync(
        SqlConnection connection,
        string workflowSuffix,
        CancellationToken cancellationToken)
    {
        const string schema = "workflow";
        var tableName = $"processForm_{workflowSuffix}";
        var tableFull = $"{schema}.{tableName}";
        var idx = workflowSuffix.Replace("-", "_");

        var ensureInstanceCol = $@"
IF COL_LENGTH('{tableFull}', 'WorkflowInstanceId') IS NULL
    ALTER TABLE workflow.[{tableName}] ADD WorkflowInstanceId UNIQUEIDENTIFIER NULL;";
        await using (var cmd = new SqlCommand(ensureInstanceCol, connection) { CommandTimeout = 120 })
            await cmd.ExecuteNonQueryAsync(cancellationToken);

        if (await ColumnExistsAsync(connection, schema, tableName, "ProcessId", cancellationToken))
        {
            await DropIndexesOnColumnAsync(connection, schema, tableName, "ProcessId", cancellationToken);

            var dropProcessId = $@"
IF COL_LENGTH('{tableFull}', 'ProcessId') IS NOT NULL
    ALTER TABLE workflow.[{tableName}] DROP COLUMN ProcessId;";
            await using var drop = new SqlCommand(dropProcessId, connection) { CommandTimeout = 120 };
            await drop.ExecuteNonQueryAsync(cancellationToken);
        }

        var ensureIndexSql = $@"
IF COL_LENGTH('{tableFull}', 'WorkflowInstanceId') IS NOT NULL
  AND NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    INNER JOIN sys.tables t ON i.object_id = t.object_id
    INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = N'workflow' AND t.name = N'{tableName}'
      AND i.name = N'IX_processForm_{idx}_WorkflowInstanceId_IsDeleted')
BEGIN
    CREATE INDEX IX_processForm_{idx}_WorkflowInstanceId_IsDeleted
        ON workflow.[{tableName}](WorkflowInstanceId, IsDeleted);
END";
        await using var indexCmd = new SqlCommand(ensureIndexSql, connection) { CommandTimeout = 120 };
        await indexCmd.ExecuteNonQueryAsync(cancellationToken);

        await MigrateProcessFormWFormIdToNvarcharAsync(connection, workflowSuffix, cancellationToken);
    }

    /// <summary>Legacy processForm had WFormId INT (hex-truncated). Align with dbo.wForm.id (dashed GUID string).</summary>
    private static async Task MigrateProcessFormWFormIdToNvarcharAsync(
        SqlConnection connection,
        string workflowSuffix,
        CancellationToken cancellationToken)
    {
        const string schema = "workflow";
        var tableName = $"processForm_{workflowSuffix}";

        if (!await ColumnExistsAsync(connection, schema, tableName, "WFormId", cancellationToken))
            return;

        var wFormIdType = await GetColumnSqlTypeAsync(connection, schema, tableName, "WFormId", cancellationToken);
        if (!IsNumericSqlType(wFormIdType))
            return;

        await DropIndexesOnColumnAsync(connection, schema, tableName, "WFormId", cancellationToken);

        var alterSql = $"ALTER TABLE workflow.[{tableName}] ALTER COLUMN WFormId NVARCHAR(64) NOT NULL;";
        await using var alterCmd = new SqlCommand(alterSql, connection) { CommandTimeout = 120 };
        await alterCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DropIndexesOnColumnAsync(
        SqlConnection connection,
        string schema,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        const string getIndexesSql = """
            SELECT DISTINCT i.name
            FROM sys.indexes i
            INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
            INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            INNER JOIN sys.tables t ON i.object_id = t.object_id
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = @Schema AND t.name = @TableName AND c.name = @ColumnName
              AND i.name IS NOT NULL AND i.type > 0;
            """;

        var indexNames = new List<string>();
        await using (var getCmd = new SqlCommand(getIndexesSql, connection))
        {
            getCmd.Parameters.AddWithValue("@Schema", schema);
            getCmd.Parameters.AddWithValue("@TableName", tableName);
            getCmd.Parameters.AddWithValue("@ColumnName", columnName);
            await using var reader = await getCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                indexNames.Add(reader.GetString(0));
        }

        foreach (var indexName in indexNames)
        {
            var escapedIndex = indexName.Replace("]", "]]");
            var dropSql = $"DROP INDEX [{escapedIndex}] ON [{schema}].[{tableName}];";
            await using var dropCmd = new SqlCommand(dropSql, connection) { CommandTimeout = 120 };
            await dropCmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<bool> TableExistsAsync(
        SqlConnection connection,
        string schema,
        string tableName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(1)
            FROM sys.tables t
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = @Schema AND t.name = @TableName
            """;
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Schema", schema);
        cmd.Parameters.AddWithValue("@TableName", tableName);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task<bool> ColumnExistsAsync(
        SqlConnection connection,
        string schema,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(1)
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @Schema AND TABLE_NAME = @Table AND COLUMN_NAME = @Column
            """;
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Schema", schema);
        cmd.Parameters.AddWithValue("@Table", tableName);
        cmd.Parameters.AddWithValue("@Column", columnName);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task<string?> GetColumnSqlTypeAsync(
        SqlConnection connection,
        string schema,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT DATA_TYPE
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @Schema AND TABLE_NAME = @Table AND COLUMN_NAME = @Column
            """;
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Schema", schema);
        cmd.Parameters.AddWithValue("@Table", tableName);
        cmd.Parameters.AddWithValue("@Column", columnName);
        return (await cmd.ExecuteScalarAsync(cancellationToken))?.ToString()?.ToLowerInvariant();
    }

    private static object CoerceWFormIdValue(string? formId, string? sqlType)
    {
        if (string.IsNullOrWhiteSpace(formId))
            return DBNull.Value;

        // processForm.WFormId should be NVARCHAR (dbo.wForm.id). Legacy INT coercion is wrong for GUID form ids.
        if (sqlType is "int" or "bigint" or "smallint" or "tinyint")
            throw new InvalidOperationException(
                "workflow.processForm WFormId is still numeric; run schema migration (restart workflow start) before inserting.");

        return NormalizeFormIdForStorage(formId);
    }

    private static string NormalizeFormIdForStorage(string formId)
    {
        var trimmed = formId.Trim();
        return Guid.TryParse(trimmed, out var guid) ? guid.ToString("D") : trimmed;
    }

    private static bool IsNumericSqlType(string? dataType) =>
        dataType is "int" or "bigint" or "smallint" or "tinyint";

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
        var table = $"workflow.WorkflowForms_{workflowSuffix}";
        var sql = $@"
INSERT INTO {table}
    (Id, TenantId, WorkflowInstanceId, StepInstanceId, WFormId, FormEntryId, FormData, HasFormPdf, CreatedAtUtc, CreatedBy, IsDeleted)
VALUES
    (NEWID(), @TenantId, @WorkflowInstanceId, @StepInstanceId, @WFormId, @FormEntryId, @FormData, 0, SYSUTCDATETIME(), @CreatedBy, 0);";

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new SqlCommand(sql, connection);
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
        var connectionString = _configuration["WorkflowJsonStorage:Blob:ConnectionString"]
            ?? _configuration["WorkflowJsonStorage:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogWarning(
                "WorkflowJsonStorage blob connection string is not configured; start payload JSON was not saved to blob (Ap Agent Trial/*.json).");
            return null;
        }

        var containerPrefix = (_configuration["WorkflowJsonStorage:Blob:ContainerPrefix"] ?? "ezts").ToLowerInvariant();
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

        // Workflow.WorkflowForms_{suffix}.WFormId is INT.
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

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string byTableSql = """
            SELECT TOP 1 Id
            FROM repository.Repositories
            WHERE TenantId = @TenantId AND IsDeleted = 0
              AND (ItemsTableName LIKE @LegacyPattern OR StageTableName LIKE @LegacyPattern);
            """;

        var legacyPattern = $"%_{legacyInt}_%";
        await using (var cmd = new SqlCommand(byTableSql, connection))
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

        var table = $"workflow.[transaction_{workflowSuffix}]";
        var sql = $"""
            SELECT TransactionGuid
            FROM {table}
            WHERE Id = @Id AND IsDeleted = 0;
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", transactionId.Value);
        var o = await cmd.ExecuteScalarAsync(cancellationToken);
        if (o is Guid g)
            return g;
        if (o != null && o != DBNull.Value && Guid.TryParse(o.ToString(), out var parsed))
            return parsed;

        return null;
    }
}
