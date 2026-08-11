using Npgsql;
using NpgsqlTypes;
using Microsoft.Extensions.Logging;
using SaaSApp.Repository.Application.Contracts;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Application.Workflows;
using SaaSApp.Workflow.Domain.Entities;
using SaaSApp.Workflow.Domain.Enums;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>
/// move-next transaction rules:
/// - review empty/null: insert step row if missing; if exists return StepAlreadyThere
/// - review provided: update review on existing open row; if review already set return ReviewAlreadyUpdated;
///   on successful review update insert next step row (review null, actionStatus 0)
/// </summary>
public sealed class WorkflowLegacyTransactionSyncService : IWorkflowLegacyTransactionSyncService
{
    private const int ActionStatusOpen = 0;
    private const int ActionStatusCompleted = 1;
    private const string EndStageType = "END";
    private const int LegacyFlowStatusRunning = 0;
    private const int LegacyFlowStatusCompleted = 1;

    private readonly ITenantContext _tenantContext;
    private readonly IWorkflowTableCreator _tableCreator;
    private readonly WorkflowLegacyMailboxSyncService _mailboxSync;
    private readonly IRepositoryItemShareService _itemShares;
    private readonly ILogger<WorkflowLegacyTransactionSyncService> _logger;

    public WorkflowLegacyTransactionSyncService(
        ITenantContext tenantContext,
        IWorkflowTableCreator tableCreator,
        WorkflowLegacyMailboxSyncService mailboxSync,
        IRepositoryItemShareService itemShares,
        ILogger<WorkflowLegacyTransactionSyncService> logger)
    {
        _tenantContext = tenantContext;
        _tableCreator = tableCreator;
        _mailboxSync = mailboxSync;
        _itemShares = itemShares;
        _logger = logger;
    }

    public async Task<int?> GetLegacyProcessFlowStatusAsync(
        Guid workflowId,
        Guid workflowInstanceId,
        string? referenceNumber,
        CancellationToken cancellationToken = default)
    {
        var connectionString = _tenantContext.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;

        var suffix = workflowId.ToString("N")[..8];
        var instancesTable = $"workflow.workflow_instances_{suffix}";

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = $"SELECT status FROM {instancesTable} WHERE id = @WorkflowInstanceId LIMIT 1;";
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        if (result == null || result == DBNull.Value)
            return null;

        var status = Convert.ToInt32(result);
        return status == (int)WorkflowInstanceStatus.Completed
            ? LegacyFlowStatusCompleted
            : LegacyFlowStatusRunning;
    }

    public async Task<WorkflowLegacyTransactionSyncResult> SyncTransactionByActivityIdAsync(
        Guid workflowId,
        Guid workflowInstanceId,
        string? referenceNumber,
        WorkflowStep targetStep,
        IReadOnlyList<WorkflowStep> orderedSteps,
        string activityId,
        Guid userId,
        Guid? activityUserId,
        string? review,
        MailboxFormSnapshot? mailboxForm = null,
        CancellationToken cancellationToken = default)
    {
        var connectionString = _tenantContext.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Tenant connection string not resolved.");

        var suffix = workflowId.ToString("N")[..8];
        var transactionTable = $"workflow.transaction_{suffix}";
        var instancesTable = $"workflow.workflow_instances_{suffix}";
        var txActivityId = ResolveTransactionActivityId(targetStep, activityId);
        var resolvedActivityUserId = activityUserId ?? targetStep.AssignedToUserId ?? userId;
        var hasReview = !string.IsNullOrWhiteSpace(review);

        await _tableCreator.EnsureLegacyTransactionTableAsync(workflowId, connectionString, cancellationToken);
        await _tableCreator.EnsureLegacyMailboxTablesAsync(workflowId, connectionString, cancellationToken);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var existingRow = await FindTransactionRowForStepAsync(
            connection, transactionTable, workflowInstanceId, targetStep, txActivityId, cancellationToken);

        if (hasReview)
        {
            if (existingRow == null)
            {
                _logger.LogInformation(
                    "No transaction row for activityId {ActivityId}; inserting open row before applying review",
                    txActivityId);
                await InsertOpenTransactionForDefinitionStepAsync(
                    connection,
                    transactionTable,
                    workflowInstanceId,
                    targetStep,
                    txActivityId,
                    resolvedActivityUserId,
                    userId,
                    ruleId: null,
                    cancellationToken);
                existingRow = await FindTransactionRowForStepAsync(
                    connection, transactionTable, workflowInstanceId, targetStep, txActivityId, cancellationToken)
                    ?? throw new InvalidOperationException(
                        $"Failed to create transaction row for activityId '{activityId}'.");
            }

            if (!string.IsNullOrWhiteSpace(existingRow.Review))
            {
                return new WorkflowLegacyTransactionSyncResult(
                    LegacyTransactionSyncStatus.ReviewAlreadyUpdated,
                    workflowInstanceId,
                    existingRow.Id,
                    null,
                    null,
                    WorkflowCompleted: false);
            }

            await UpdateReviewAsync(
                connection,
                transactionTable,
                existingRow.Id,
                targetStep,
                txActivityId,
                review!.Trim(),
                resolvedActivityUserId,
                userId,
                WorkflowStepActionsHelper.FindMatchingAction(targetStep, review)?.Id,
                cancellationToken);

            await _mailboxSync.SyncTransactionRowAsync(
                workflowId, existingRow.Id, connection, mailboxForm, cancellationToken);

            int? nextTransactionId = null;
            Guid? nextTransactionGuid = null;
            var workflowCompleted = false;

            if (IsEndReview(review))
            {
                await CompleteWorkflowInstanceAsync(connection, instancesTable, workflowInstanceId, userId, cancellationToken);
                await _mailboxSync.SyncInstanceEndTransactionsAsync(
                    workflowId, workflowInstanceId, connection, mailboxForm, cancellationToken);

                return new WorkflowLegacyTransactionSyncResult(
                    LegacyTransactionSyncStatus.ReviewUpdated,
                    workflowInstanceId,
                    existingRow.Id,
                    null,
                    null,
                    WorkflowCompleted: true);
            }

            WorkflowStep? nextStep;
            var matchedRule = WorkflowStepActionsHelper.FindMatchingAction(targetStep, review);
            if (matchedRule != null)
            {
                nextStep = WorkflowStepActionsHelper.ResolveNextStepByReview(targetStep, review, orderedSteps);
                if (nextStep == null)
                    throw new InvalidOperationException(
                        $"Rule '{matchedRule.Id}' targets block '{matchedRule.ToBlockId}' which is not defined in workflow steps.");
            }
            else if (WorkflowStepActionsHelper.ParseActions(targetStep.ActionsJson).Count > 0)
            {
                throw new InvalidOperationException(
                    $"Review '{review?.Trim()}' is not a valid ProceedAction for activity '{txActivityId}'.");
            }
            else
            {
                nextStep = orderedSteps
                    .Where(s => s.Order > targetStep.Order)
                    .OrderBy(s => s.Order)
                    .FirstOrDefault();
            }

            if (nextStep != null && matchedRule != null)
            {
                _logger.LogInformation(
                    "Routing review '{Review}' from activity {FromActivityId} via rule {RuleId} to block {ToBlockId} (step order {Order})",
                    review?.Trim(),
                    txActivityId,
                    matchedRule.Id,
                    matchedRule.ToBlockId,
                    nextStep.Order);
            }

            if (nextStep != null)
            {
                var nextTxActivityId = ResolveTransactionActivityId(nextStep, nextStep.ActivityId ?? nextStep.Id.ToString("D"));
                var nextExists = await FindTransactionRowForStepAsync(
                    connection, transactionTable, workflowInstanceId, nextStep, nextTxActivityId, cancellationToken);

                if (nextExists == null)
                {
                    var (nextActivityUserId, nextCreatedByUserId) = await ResolveNextStepAssigneeAsync(
                        workflowInstanceId,
                        nextStep,
                        resolvedActivityUserId,
                        userId,
                        cancellationToken);

                    (nextTransactionId, nextTransactionGuid) = await InsertOpenTransactionForDefinitionStepAsync(
                        connection,
                        transactionTable,
                        workflowInstanceId,
                        nextStep,
                        nextTxActivityId,
                        nextActivityUserId,
                        nextCreatedByUserId,
                        matchedRule?.Id,
                        cancellationToken);

                    await _mailboxSync.SyncTransactionRowAsync(
                        workflowId, nextTransactionId.Value, connection, mailboxForm, cancellationToken);

                    if (IsEndStage(nextStep))
                    {
                        await CompleteWorkflowInstanceAsync(connection, instancesTable, workflowInstanceId, userId, cancellationToken);
                        await _mailboxSync.SyncInstanceEndTransactionsAsync(
                            workflowId, workflowInstanceId, connection, mailboxForm, cancellationToken);
                        workflowCompleted = true;
                    }

                    _logger.LogInformation(
                        "Inserted next step transaction {TransactionId} order {Order} after review on {ActivityId} (assignee {AssigneeUserId})",
                        nextTransactionId, nextStep.Order, txActivityId, nextActivityUserId);

                    return new WorkflowLegacyTransactionSyncResult(
                        LegacyTransactionSyncStatus.ReviewUpdated,
                        workflowInstanceId,
                        existingRow.Id,
                        nextTransactionId,
                        nextTransactionGuid,
                        workflowCompleted,
                        nextActivityUserId,
                        nextCreatedByUserId);
                }
            }
            else
            {
                await CompleteWorkflowInstanceAsync(connection, instancesTable, workflowInstanceId, userId, cancellationToken);
                await _mailboxSync.SyncInstanceEndTransactionsAsync(
                    workflowId, workflowInstanceId, connection, mailboxForm, cancellationToken);
                workflowCompleted = true;
            }

            if (workflowCompleted)
            {
                nextTransactionId = null;
                nextTransactionGuid = null;
            }

            return new WorkflowLegacyTransactionSyncResult(
                LegacyTransactionSyncStatus.ReviewUpdated,
                workflowInstanceId,
                existingRow.Id,
                nextTransactionId,
                nextTransactionGuid,
                workflowCompleted);
        }

        // review empty or not given
        if (existingRow != null)
        {
            return new WorkflowLegacyTransactionSyncResult(
                LegacyTransactionSyncStatus.StepAlreadyThere,
                workflowInstanceId,
                existingRow.Id,
                null,
                null,
                WorkflowCompleted: false);
        }

        var (insertedId, insertedGuid) = await InsertOpenTransactionForDefinitionStepAsync(
            connection,
            transactionTable,
            workflowInstanceId,
            targetStep,
            txActivityId,
            resolvedActivityUserId,
            userId,
            ruleId: null,
            cancellationToken);

        await _mailboxSync.SyncTransactionRowAsync(
            workflowId, insertedId, connection, mailboxForm, cancellationToken);

        var insertedEndStage = IsEndStage(targetStep);
        if (insertedEndStage)
        {
            await CompleteWorkflowInstanceAsync(connection, instancesTable, workflowInstanceId, userId, cancellationToken);
            await _mailboxSync.SyncInstanceEndTransactionsAsync(
                workflowId, workflowInstanceId, connection, mailboxForm, cancellationToken);
            _logger.LogInformation(
                "END stage inserted for workflow instance {WorkflowInstanceId}; instance marked completed",
                workflowInstanceId);
        }

        _logger.LogInformation(
            "Inserted step transaction {TransactionId} order {Order} activityId {ActivityId}",
            insertedId, targetStep.Order, txActivityId);

        return new WorkflowLegacyTransactionSyncResult(
            LegacyTransactionSyncStatus.StepInserted,
            workflowInstanceId,
            insertedId,
            null,
            insertedGuid,
            insertedEndStage);
    }

    internal static string ResolveTransactionActivityId(WorkflowStep step, string requestActivityId)
    {
        if (!string.IsNullOrWhiteSpace(step.ActivityId))
            return step.ActivityId.Trim();

        return requestActivityId.Trim();
    }

    internal static string? ResolveTransactionStageType(WorkflowStep step) =>
        !string.IsNullOrWhiteSpace(step.StageType)
            ? step.StageType.Trim()
            : step.StepType.ToString();

    internal static bool IsEndStage(WorkflowStep step) =>
        string.Equals(ResolveTransactionStageType(step), EndStageType, StringComparison.OrdinalIgnoreCase);

    internal static bool IsEndReview(string? review) =>
        string.Equals(review?.Trim(), EndStageType, StringComparison.OrdinalIgnoreCase);

    private sealed record TransactionRow(int Id, string? Review, int ActionStatus);

    private static async Task<TransactionRow?> FindTransactionRowForStepAsync(
        NpgsqlConnection connection,
        string transactionTable,
        Guid workflowInstanceId,
        WorkflowStep step,
        string txActivityId,
        CancellationToken cancellationToken)
    {
        var sql = $@"
SELECT id, review, action_status
FROM {transactionTable}
WHERE workflow_instance_id = @WorkflowInstanceId
  AND is_deleted = false
  AND (
        activity_id = @TxActivityId
     OR activity_id = @RequestActivityId
     OR activity_id = @StepIdD
     OR activity_id = @StepIdN
     OR (@StepActivityId IS NOT NULL AND activity_id = @StepActivityId)
  )
ORDER BY id DESC
LIMIT 1;";
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
        cmd.Parameters.AddWithValue("@TxActivityId", txActivityId);
        cmd.Parameters.AddWithValue("@RequestActivityId", txActivityId);
        cmd.Parameters.AddWithValue("@StepIdD", step.Id.ToString("D"));
        cmd.Parameters.AddWithValue("@StepIdN", step.Id.ToString("N"));
        // Explicit NpgsqlDbType.Varchar (not AddWithValue) -- when the CLR value is
        // DBNull.Value, AddWithValue leaves the parameter type as Unknown, and Postgres
        // can't infer it from a bare "@StepActivityId IS NOT NULL" comparison, causing
        // 42P08 "could not determine data type of parameter" (confirmed empirically).
        cmd.Parameters.Add(new NpgsqlParameter("StepActivityId", NpgsqlDbType.Varchar)
        {
            Value = (object?)step.ActivityId ?? DBNull.Value
        });

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new TransactionRow(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt32(2));
    }

    private static async Task UpdateReviewAsync(
        NpgsqlConnection connection,
        string transactionTable,
        int transactionId,
        WorkflowStep step,
        string txActivityId,
        string review,
        Guid activityUserId,
        Guid modifiedByUserId,
        string? ruleId,
        CancellationToken cancellationToken)
    {
        var sql = $@"
UPDATE {transactionTable}
SET activity_id = @ActivityId,
    rule_id = @RuleId,
    stage_type = @StageType,
    stage_name = @StageName,
    review = @Review,
    action_status = @ActionStatus,
    activity_user_id = @ActivityUserId,
    modified_at = now(),
    modified_by = @ModifiedBy
WHERE id = @Id AND is_deleted = false;";
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@ActivityId", txActivityId);
        cmd.Parameters.AddWithValue("@RuleId", (object?)ruleId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@StageType", (object?)ResolveTransactionStageType(step) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@StageName", step.Name);
        cmd.Parameters.AddWithValue("@Review", review);
        cmd.Parameters.AddWithValue("@ActionStatus", ActionStatusCompleted);
        cmd.Parameters.AddWithValue("@ActivityUserId", activityUserId);
        cmd.Parameters.AddWithValue("@ModifiedBy", modifiedByUserId);
        cmd.Parameters.AddWithValue("@Id", transactionId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CompleteWorkflowInstanceAsync(
        NpgsqlConnection connection,
        string instancesTable,
        Guid workflowInstanceId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var sql = $@"
UPDATE {instancesTable}
SET status = @Status,
    completed_at_utc = now(),
    last_activity_at_utc = now()
WHERE id = @WorkflowInstanceId;";
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Status", (int)WorkflowInstanceStatus.Completed);
        cmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// After a workflow inbox share, return the next open step to the sharer (not the guest).
    /// Uses SharedByUserId as both assignee and CreatedBy so the guest does not keep inbox via CreatedBy.
    /// </summary>
    private async Task<(Guid ActivityUserId, Guid CreatedByUserId)> ResolveNextStepAssigneeAsync(
        Guid workflowInstanceId,
        WorkflowStep nextStep,
        Guid resolvedActivityUserId,
        Guid currentUserId,
        CancellationToken cancellationToken)
    {
        if (nextStep.AssignedToUserId is Guid stepAssignee && stepAssignee != Guid.Empty)
            return (stepAssignee, currentUserId);

        try
        {
            var shareOwnerId = await _itemShares.GetActiveWorkflowShareOwnerUserIdAsync(
                workflowInstanceId,
                cancellationToken);
            if (shareOwnerId is Guid ownerId && ownerId != Guid.Empty)
            {
                _logger.LogInformation(
                    "Returning workflow instance {InstanceId} next step to share owner {OwnerUserId} after guest move-next",
                    workflowInstanceId,
                    ownerId);
                return (ownerId, ownerId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to resolve workflow share owner for instance {InstanceId}; using default next assignee",
                workflowInstanceId);
        }

        return (resolvedActivityUserId, currentUserId);
    }

    private static async Task<(int Id, Guid Guid)> InsertOpenTransactionForDefinitionStepAsync(
        NpgsqlConnection connection,
        string transactionTable,
        Guid workflowInstanceId,
        WorkflowStep step,
        string txActivityId,
        Guid activityUserId,
        Guid createdByUserId,
        string? ruleId,
        CancellationToken cancellationToken)
    {
        var sql = $@"
INSERT INTO {transactionTable}
    (workflow_instance_id, activity_id, rule_id, stage_type, stage_name, review, action_status,
     activity_user_id, created_at, created_by, is_deleted, transaction_guid)
VALUES
    (@WorkflowInstanceId, @ActivityId, @RuleId, @StageType, @StageName, NULL, @ActionStatus,
     @ActivityUserId, now(), @CreatedBy, false, gen_random_uuid())
RETURNING id;";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
        cmd.Parameters.AddWithValue("@ActivityId", txActivityId);
        cmd.Parameters.AddWithValue("@RuleId", (object?)ruleId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@StageType", (object?)ResolveTransactionStageType(step) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@StageName", step.Name);
        cmd.Parameters.AddWithValue("@ActionStatus", ActionStatusOpen);
        cmd.Parameters.AddWithValue("@ActivityUserId", activityUserId);
        cmd.Parameters.AddWithValue("@CreatedBy", createdByUserId);
        var id = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));

        var guidSql = $"SELECT transaction_guid FROM {transactionTable} WHERE id = @Id AND is_deleted = false";
        await using var guidCmd = new NpgsqlCommand(guidSql, connection);
        guidCmd.Parameters.AddWithValue("@Id", id);
        var guidResult = await guidCmd.ExecuteScalarAsync(cancellationToken);
        var guid = guidResult == null || guidResult == DBNull.Value ? Guid.Empty : (Guid)guidResult;
        return (id, guid);
    }
}
