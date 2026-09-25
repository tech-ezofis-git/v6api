using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using Microsoft.Extensions.Logging;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>
/// Routes transaction rows to workflow.inbox_*, sent_*, or completed_*.
/// Deduplicates by workflowId + workflowInstanceId + activityId (delete all matches, then insert one row).
/// </summary>
public sealed class WorkflowLegacyMailboxSyncService : IWorkflowLegacyMailboxSyncService
{
    private const string EndStageType = "END";

    // m.workflow_instance_id/user_id are text -- Postgres has no TRY_CONVERT, so a safe cast
    // to uuid is this regex-guarded CASE (same pattern used across this migration).
    private const string UuidGuard = "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$";
    private static string TryCastUuid(string column) =>
        $"(CASE WHEN {column} ~ '{UuidGuard}' THEN {column}::uuid ELSE NULL END)";

    private readonly ITenantContext _tenantContext;
    private readonly IWorkflowTableCreator _tableCreator;
    private readonly ILogger<WorkflowLegacyMailboxSyncService> _logger;

    private sealed record MailboxExtraData(
        Guid? RepositoryId,
        Guid? ItemId,
        string? FormId,
        string? FormEntryId,
        string? FormData);

    public WorkflowLegacyMailboxSyncService(
        ITenantContext tenantContext,
        IWorkflowTableCreator tableCreator,
        ILogger<WorkflowLegacyMailboxSyncService> logger)
    {
        _tenantContext = tenantContext;
        _tableCreator = tableCreator;
        _logger = logger;
    }

    public async Task SyncTransactionRowAsync(
        Guid workflowId,
        int transactionRowId,
        CancellationToken cancellationToken = default,
        int? inboxAction = null,
        MailboxOwnerCopyKind ownerMailboxCopy = MailboxOwnerCopyKind.Inbox)
    {
        var connectionString = _tenantContext.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await SyncTransactionRowAsync(
            workflowId, transactionRowId, connection, formOverride: null, cancellationToken, inboxAction, ownerMailboxCopy);
    }

    public async Task SyncInstanceEndTransactionsAsync(
        Guid workflowId,
        Guid workflowInstanceId,
        CancellationToken cancellationToken = default)
    {
        var connectionString = _tenantContext.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await SyncInstanceEndTransactionsAsync(workflowId, workflowInstanceId, connection, formOverride: null, cancellationToken);
    }

    /// <summary>Sync using an existing open connection (same request/transaction).</summary>
    public async Task SyncTransactionRowAsync(
        Guid workflowId,
        int transactionRowId,
        NpgsqlConnection connection,
        MailboxFormSnapshot? formOverride = null,
        CancellationToken cancellationToken = default,
        int? inboxAction = null,
        MailboxOwnerCopyKind ownerMailboxCopy = MailboxOwnerCopyKind.Inbox)
    {
        await EnsureMailboxTablesOnOpenConnectionAsync(workflowId, connection, cancellationToken);
        await SyncTransactionRowCoreAsync(
            workflowId, transactionRowId, connection, formOverride, cancellationToken, inboxAction, ownerMailboxCopy);
    }

    public async Task SyncInstanceEndTransactionsAsync(
        Guid workflowId,
        Guid workflowInstanceId,
        NpgsqlConnection connection,
        MailboxFormSnapshot? formOverride = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureMailboxTablesOnOpenConnectionAsync(workflowId, connection, cancellationToken);

        var suffix = workflowId.ToString("N")[..8];
        var transactionTable = $"workflow.transaction_{suffix}";
        await SyncInstanceEndTransactionsCoreAsync(workflowId, workflowInstanceId, transactionTable, connection, formOverride, cancellationToken);
    }

    private async Task SyncInstanceEndTransactionsCoreAsync(
        Guid workflowId,
        Guid workflowInstanceId,
        string transactionTable,
        NpgsqlConnection connection,
        MailboxFormSnapshot? formOverride,
        CancellationToken cancellationToken)
    {
        var ids = new List<int>();
        var sql = $@"
SELECT id
FROM {transactionTable}
WHERE workflow_instance_id = @WorkflowInstanceId AND is_deleted = false AND UPPER(TRIM(stage_type)) = @EndStageType;";
        await using (var cmd = new NpgsqlCommand(sql, connection))
        {
            cmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
            cmd.Parameters.AddWithValue("@EndStageType", EndStageType);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                ids.Add(reader.GetInt32(0));
        }

        foreach (var id in ids)
            await SyncTransactionRowCoreAsync(workflowId, id, connection, formOverride, cancellationToken, inboxAction: null);
    }

    private async Task SyncTransactionRowCoreAsync(
        Guid workflowId,
        int transactionRowId,
        NpgsqlConnection connection,
        MailboxFormSnapshot? formOverride,
        CancellationToken cancellationToken,
        int? inboxAction = null,
        MailboxOwnerCopyKind ownerMailboxCopy = MailboxOwnerCopyKind.Inbox)
    {
        var suffix = workflowId.ToString("N")[..8];
        var workflowIdCompact = workflowId.ToString("N");
        var workflowIdValue = workflowId.ToString("D");

        var transactionTable = $"workflow.transaction_{suffix}";
        var instancesTable = $"workflow.workflow_instances_{suffix}";

        var stateSql = $@"
SELECT
    t.workflow_instance_id,
    t.activity_id,
    t.transaction_guid,
    t.stage_type,
    t.action_status,
    t.is_deleted,
    t.activity_user_id,
    t.modified_by,
    t.created_by
FROM {transactionTable} t
WHERE t.id = @TransactionRowId;";

        Guid workflowInstanceId;
        string? activityId;
        Guid? transactionGuid;
        string? stageType;
        int actionStatus;
        bool isDeleted;
        Guid? activityUserId;
        Guid? modifiedByUserId;
        Guid? createdByUserId;

        await using (var stateCmd = new NpgsqlCommand(stateSql, connection))
        {
            stateCmd.Parameters.AddWithValue("@TransactionRowId", transactionRowId);
            await using var reader = await stateCmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return;

            workflowInstanceId = reader.GetGuid(0);
            activityId = reader.IsDBNull(1) ? null : reader.GetString(1);
            transactionGuid = reader.IsDBNull(2) ? null : reader.GetGuid(2);
            stageType = reader.IsDBNull(3) ? null : reader.GetString(3);
            actionStatus = reader.GetInt32(4);
            isDeleted = reader.GetBoolean(5);
            activityUserId = reader.IsDBNull(6) ? null : reader.GetGuid(6);
            modifiedByUserId = reader.IsDBNull(7) ? null : reader.GetGuid(7);
            createdByUserId = reader.IsDBNull(8) ? null : reader.GetGuid(8);
        }

        var workflowInstanceIdStr = workflowInstanceId.ToString("D");
        var inboxTable = MailboxTable("inbox", suffix);
        var sentTable = MailboxTable("sent", suffix);
        var completedTable = MailboxTable("completed", suffix);

        // Forward re-sync deletes Sent by activity key — capture prior Sent watchers first
        // so earlier forwarders stay visible after a second Forward.
        List<string>? priorSentWatcherUserIds = null;
        if (ownerMailboxCopy == MailboxOwnerCopyKind.Sent)
        {
            priorSentWatcherUserIds = await ListSentMailboxUserIdsForInstanceAsync(
                connection,
                workflowIdValue,
                workflowIdCompact,
                workflowInstanceId,
                workflowInstanceIdStr,
                sentTable,
                cancellationToken);
        }

        // Snapshot before the activity-key delete. Completing a step removes the open
        // Inbox/Sent rows that still use this activity, so reading them afterwards misses
        // everyone who was involved.
        var isEnd = string.Equals(stageType?.Trim(), EndStageType, StringComparison.OrdinalIgnoreCase);
        List<string>? completedWatcherUserIds = null;
        if (isEnd && !isDeleted)
        {
            var mailboxWatchers = await ListMailboxUserIdsForInstanceAsync(
                connection,
                workflowIdValue,
                workflowIdCompact,
                workflowInstanceId,
                workflowInstanceIdStr,
                inboxTable,
                sentTable,
                cancellationToken);
            var participants = await ListTransactionParticipantUserIdsAsync(
                connection,
                transactionTable,
                workflowInstanceId,
                cancellationToken);
            completedWatcherUserIds = mailboxWatchers
                .Concat(participants)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        await DeleteFromAllMailboxTablesByKeyAsync(
            connection,
            workflowIdValue,
            workflowIdCompact,
            workflowInstanceId,
            workflowInstanceIdStr,
            activityId,
            inboxTable,
            sentTable,
            completedTable,
            cancellationToken);

        if (isDeleted)
            return;

        var targetTable = isEnd
            ? completedTable
            : actionStatus == 0
                ? inboxTable
                : sentTable;

        // Keep mailbox aligned with workflow state: no stale inbox after approve; no inbox/sent after complete.
        if (targetTable == sentTable)
            await DeleteMailboxRowsForInstanceAsync(connection, workflowIdValue, workflowIdCompact, workflowInstanceId, workflowInstanceIdStr, inboxTable, cancellationToken);
        else if (targetTable == completedTable)
        {
            await DeleteMailboxRowsForInstanceAsync(connection, workflowIdValue, workflowIdCompact, workflowInstanceId, workflowInstanceIdStr, inboxTable, cancellationToken);
            await DeleteMailboxRowsForInstanceAsync(connection, workflowIdValue, workflowIdCompact, workflowInstanceId, workflowInstanceIdStr, sentTable, cancellationToken);
        }
        else
            await DeleteMailboxRowsForInstanceAsync(connection, workflowIdValue, workflowIdCompact, workflowInstanceId, workflowInstanceIdStr, inboxTable, cancellationToken);

        if (targetTable == inboxTable && activityUserId is Guid assigneeId && assigneeId != Guid.Empty)
            await DeleteSentRowsForInstanceAndUserAsync(
                connection,
                workflowIdValue,
                workflowIdCompact,
                workflowInstanceId,
                workflowInstanceIdStr,
                sentTable,
                assigneeId,
                cancellationToken);

        // Share-file: clear owner's Sent when they stay on Inbox with the guest.
        // Forward (Sent copy): do not clear owner's Sent here — insert below recreates it.
        if (targetTable == inboxTable
            && modifiedByUserId is Guid shareOwnerId
            && shareOwnerId != Guid.Empty
            && activityUserId is Guid guestId
            && guestId != Guid.Empty
            && shareOwnerId != guestId
            && ownerMailboxCopy == MailboxOwnerCopyKind.Inbox)
        {
            await DeleteSentRowsForInstanceAndUserAsync(
                connection,
                workflowIdValue,
                workflowIdCompact,
                workflowInstanceId,
                workflowInstanceIdStr,
                sentTable,
                shareOwnerId,
                cancellationToken);
        }
        else if (targetTable == inboxTable
            && modifiedByUserId is Guid forwarderId
            && forwarderId != Guid.Empty
            && activityUserId is Guid assigneeOnly
            && assigneeOnly != Guid.Empty
            && forwarderId != assigneeOnly
            && ownerMailboxCopy == MailboxOwnerCopyKind.None)
        {
            // Explicit hide for forwarder (unused by Forward path; kept for share edge cases).
            await DeleteMailboxRowsForInstanceAndUserAsync(
                connection,
                workflowIdValue,
                workflowIdCompact,
                workflowInstanceId,
                workflowInstanceIdStr,
                inboxTable,
                forwarderId,
                cancellationToken);
            await DeleteMailboxRowsForInstanceAndUserAsync(
                connection,
                workflowIdValue,
                workflowIdCompact,
                workflowInstanceId,
                workflowInstanceIdStr,
                sentTable,
                forwarderId,
                cancellationToken);
        }

        var txIdStr = transactionGuid is { } g && g != Guid.Empty
            ? g.ToString("D")
            : transactionRowId.ToString();
        var extras = await ResolveMailboxExtraDataAsync(
            connection,
            suffix,
            workflowId,
            workflowInstanceId,
            formOverride,
            cancellationToken);

        var sourceSql = BuildMailboxSourceSelect(transactionTable, instancesTable);
        var resolvedAction = inboxAction == 0 ? 0 : 1;

        var insertSql = $@"
INSERT INTO {targetTable}
    (user_id, group_id, workflow_id, name, workflow_instance_id, reference_number, created_at_utc, started_at_utc, completed_at_utc, context,
     transaction_id, activity_id, rule_id, stage_type, stage, review,
     transaction_created_at, transaction_created_by, transaction_created_by_email,
     transaction_modified_at, transaction_modified_by, activity_user_email,
     repository_id, item_id, form_id, form_entry_id, form_data, ""action"")
{sourceSql};";

        await using (var insertCmd = new NpgsqlCommand(insertSql, connection))
        {
            insertCmd.Parameters.AddWithValue("@WorkflowGuid", workflowId);
            insertCmd.Parameters.AddWithValue("@WorkflowIdValue", workflowIdValue);
            insertCmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
            insertCmd.Parameters.AddWithValue("@WorkflowInstanceIdStr", workflowInstanceIdStr);
            insertCmd.Parameters.AddWithValue("@TransactionRowId", transactionRowId);
            insertCmd.Parameters.AddWithValue("@TxGuidStr", txIdStr);
            insertCmd.Parameters.AddWithValue("@OverrideUserId", DBNull.Value);
            insertCmd.Parameters.AddWithValue("@Action", resolvedAction);
            insertCmd.Parameters.AddWithValue("@RepositoryId", (object?)extras.RepositoryId?.ToString("D") ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("@ItemId", (object?)extras.ItemId?.ToString("D") ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("@FormId", (object?)extras.FormId ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("@FormEntryId", (object?)extras.FormEntryId ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("@FormData", (object?)extras.FormData ?? DBNull.Value);
            await insertCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        if (targetTable == inboxTable
            && modifiedByUserId is Guid ownerCcId
            && ownerCcId != Guid.Empty
            && activityUserId is Guid openAssigneeId
            && openAssigneeId != Guid.Empty
            && ownerCcId != openAssigneeId
            && ownerMailboxCopy != MailboxOwnerCopyKind.None)
        {
            var ownerTable = ownerMailboxCopy == MailboxOwnerCopyKind.Sent ? sentTable : inboxTable;
            var ownerAction = ownerMailboxCopy == MailboxOwnerCopyKind.Sent ? 0 : resolvedAction;

            if (ownerMailboxCopy == MailboxOwnerCopyKind.Sent)
            {
                await DeleteMailboxRowsForInstanceAndUserAsync(
                    connection,
                    workflowIdValue,
                    workflowIdCompact,
                    workflowInstanceId,
                    workflowInstanceIdStr,
                    inboxTable,
                    ownerCcId,
                    cancellationToken);
            }

            var ownerInsertSql = $@"
INSERT INTO {ownerTable}
    (user_id, group_id, workflow_id, name, workflow_instance_id, reference_number, created_at_utc, started_at_utc, completed_at_utc, context,
     transaction_id, activity_id, rule_id, stage_type, stage, review,
     transaction_created_at, transaction_created_by, transaction_created_by_email,
     transaction_modified_at, transaction_modified_by, activity_user_email,
     repository_id, item_id, form_id, form_entry_id, form_data, ""action"")
{sourceSql};";

            await using var ccCmd = new NpgsqlCommand(ownerInsertSql, connection);
            ccCmd.Parameters.AddWithValue("@WorkflowGuid", workflowId);
            ccCmd.Parameters.AddWithValue("@WorkflowIdValue", workflowIdValue);
            ccCmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
            ccCmd.Parameters.AddWithValue("@WorkflowInstanceIdStr", workflowInstanceIdStr);
            ccCmd.Parameters.AddWithValue("@TransactionRowId", transactionRowId);
            ccCmd.Parameters.AddWithValue("@TxGuidStr", txIdStr);
            ccCmd.Parameters.AddWithValue("@OverrideUserId", ownerCcId.ToString("D"));
            ccCmd.Parameters.AddWithValue("@Action", ownerAction);
            ccCmd.Parameters.AddWithValue("@RepositoryId", (object?)extras.RepositoryId?.ToString("D") ?? DBNull.Value);
            ccCmd.Parameters.AddWithValue("@ItemId", (object?)extras.ItemId?.ToString("D") ?? DBNull.Value);
            ccCmd.Parameters.AddWithValue("@FormId", (object?)extras.FormId ?? DBNull.Value);
            ccCmd.Parameters.AddWithValue("@FormEntryId", (object?)extras.FormEntryId ?? DBNull.Value);
            ccCmd.Parameters.AddWithValue("@FormData", (object?)extras.FormData ?? DBNull.Value);
            await ccCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Re-Forward: restore earlier forwarders' Sent copies (skip new assignee + current forwarder).
        if (targetTable == inboxTable
            && ownerMailboxCopy == MailboxOwnerCopyKind.Sent
            && priorSentWatcherUserIds is { Count: > 0 })
        {
            var assigneeUser = activityUserId?.ToString("D");
            var currentForwarder = modifiedByUserId?.ToString("D");
            foreach (var watcher in priorSentWatcherUserIds)
            {
                if (string.IsNullOrWhiteSpace(watcher))
                    continue;
                if (assigneeUser != null
                    && string.Equals(watcher, assigneeUser, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (currentForwarder != null
                    && string.Equals(watcher, currentForwarder, StringComparison.OrdinalIgnoreCase))
                    continue;

                await EnsureUserMailboxCopyAsync(
                    connection,
                    sentTable,
                    sourceSql,
                    workflowId,
                    workflowIdValue,
                    workflowInstanceId,
                    workflowInstanceIdStr,
                    transactionRowId,
                    txIdStr,
                    watcher,
                    action: 0,
                    extras,
                    cancellationToken);
            }
        }

        // Submitter/initiator is not the next assignee: remove from Inbox (Forward puts them on Sent above).
        if (targetTable == inboxTable
            && createdByUserId is Guid submitterId
            && submitterId != Guid.Empty
            && activityUserId is Guid openUserId
            && openUserId != Guid.Empty
            && submitterId != openUserId)
        {
            await DeleteMailboxRowsForInstanceAndUserAsync(
                connection,
                workflowIdValue,
                workflowIdCompact,
                workflowInstanceId,
                workflowInstanceIdStr,
                inboxTable,
                submitterId,
                cancellationToken);

            // If submitter is the forwarder, they already got Sent via ownerMailboxCopy.Sent.
            // If not, and they have no Sent copy yet, keep visibility on Sent.
            if (ownerMailboxCopy != MailboxOwnerCopyKind.Sent
                || modifiedByUserId != submitterId)
            {
                await EnsureUserMailboxCopyAsync(
                    connection,
                    sentTable,
                    sourceSql,
                    workflowId,
                    workflowIdValue,
                    workflowInstanceId,
                    workflowInstanceIdStr,
                    transactionRowId,
                    txIdStr,
                    submitterId.ToString("D"),
                    action: 0,
                    extras,
                    cancellationToken);
            }
        }

        // After Submit (open step → Sent): keep forwarder on Sent until Completed.
        if (targetTable == sentTable
            && modifiedByUserId is Guid priorActorId
            && priorActorId != Guid.Empty
            && activityUserId is Guid completerId
            && completerId != Guid.Empty
            && priorActorId != completerId)
        {
            await EnsureUserMailboxCopyAsync(
                connection,
                sentTable,
                sourceSql,
                workflowId,
                workflowIdValue,
                workflowInstanceId,
                workflowInstanceIdStr,
                transactionRowId,
                txIdStr,
                priorActorId.ToString("D"),
                action: 0,
                extras,
                cancellationToken);
        }

        // Completed: restore every prior Inbox/Sent watcher onto Completed.
        if (targetTable == completedTable && completedWatcherUserIds is { Count: > 0 })
        {
            var primaryUser = activityUserId?.ToString("D");
            foreach (var watcher in completedWatcherUserIds)
            {
                if (string.IsNullOrWhiteSpace(watcher))
                    continue;
                if (primaryUser != null
                    && string.Equals(watcher, primaryUser, StringComparison.OrdinalIgnoreCase))
                    continue;

                await EnsureUserMailboxCopyAsync(
                    connection,
                    completedTable,
                    sourceSql,
                    workflowId,
                    workflowIdValue,
                    workflowInstanceId,
                    workflowInstanceIdStr,
                    transactionRowId,
                    txIdStr,
                    watcher,
                    action: 0,
                    extras,
                    cancellationToken);
            }
        }
    }

    private static async Task EnsureUserMailboxCopyAsync(
        NpgsqlConnection connection,
        string tableFull,
        string sourceSql,
        Guid workflowId,
        string workflowIdValue,
        Guid workflowInstanceId,
        string workflowInstanceIdStr,
        int transactionRowId,
        string txIdStr,
        string overrideUserId,
        int action,
        MailboxExtraData extras,
        CancellationToken cancellationToken)
    {
        var insertSql = $@"
INSERT INTO {tableFull}
    (user_id, group_id, workflow_id, name, workflow_instance_id, reference_number, created_at_utc, started_at_utc, completed_at_utc, context,
     transaction_id, activity_id, rule_id, stage_type, stage, review,
     transaction_created_at, transaction_created_by, transaction_created_by_email,
     transaction_modified_at, transaction_modified_by, activity_user_email,
     repository_id, item_id, form_id, form_entry_id, form_data, ""action"")
{sourceSql};";

        await using var cmd = new NpgsqlCommand(insertSql, connection);
        cmd.Parameters.AddWithValue("@WorkflowGuid", workflowId);
        cmd.Parameters.AddWithValue("@WorkflowIdValue", workflowIdValue);
        cmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
        cmd.Parameters.AddWithValue("@WorkflowInstanceIdStr", workflowInstanceIdStr);
        cmd.Parameters.AddWithValue("@TransactionRowId", transactionRowId);
        cmd.Parameters.AddWithValue("@TxGuidStr", txIdStr);
        cmd.Parameters.AddWithValue("@OverrideUserId", overrideUserId);
        cmd.Parameters.AddWithValue("@Action", action);
        cmd.Parameters.AddWithValue("@RepositoryId", (object?)extras.RepositoryId?.ToString("D") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ItemId", (object?)extras.ItemId?.ToString("D") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@FormId", (object?)extras.FormId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@FormEntryId", (object?)extras.FormEntryId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@FormData", (object?)extras.FormData ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<string>> ListSentMailboxUserIdsForInstanceAsync(
        NpgsqlConnection connection,
        string workflowIdValue,
        string workflowTableKey,
        Guid workflowInstanceId,
        string workflowInstanceIdStr,
        string sentTable,
        CancellationToken cancellationToken)
    {
        var sql = $"""
SELECT DISTINCT user_id
FROM {sentTable}
WHERE (workflow_id = @WorkflowIdValue OR workflow_id = @WorkflowTableKey)
  AND (workflow_instance_id = @WorkflowInstanceIdStr OR {TryCastUuid("workflow_instance_id")} = @WorkflowInstanceId)
  AND user_id IS NOT NULL
  AND BTRIM(user_id) <> '';
""";
        var list = new List<string>();
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@WorkflowIdValue", workflowIdValue);
        cmd.Parameters.AddWithValue("@WorkflowTableKey", workflowTableKey);
        cmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
        cmd.Parameters.AddWithValue("@WorkflowInstanceIdStr", workflowInstanceIdStr);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(0))
                list.Add(reader.GetString(0));
        }

        return list;
    }

    private static async Task<List<string>> ListMailboxUserIdsForInstanceAsync(
        NpgsqlConnection connection,
        string workflowIdValue,
        string workflowTableKey,
        Guid workflowInstanceId,
        string workflowInstanceIdStr,
        string inboxTable,
        string sentTable,
        CancellationToken cancellationToken)
    {
        var sql = $"""
SELECT DISTINCT user_id
FROM (
    SELECT user_id FROM {inboxTable}
    WHERE (workflow_id = @WorkflowIdValue OR workflow_id = @WorkflowTableKey)
      AND (workflow_instance_id = @WorkflowInstanceIdStr OR {TryCastUuid("workflow_instance_id")} = @WorkflowInstanceId)
    UNION
    SELECT user_id FROM {sentTable}
    WHERE (workflow_id = @WorkflowIdValue OR workflow_id = @WorkflowTableKey)
      AND (workflow_instance_id = @WorkflowInstanceIdStr OR {TryCastUuid("workflow_instance_id")} = @WorkflowInstanceId)
) u
WHERE user_id IS NOT NULL AND BTRIM(user_id) <> '';
""";
        var users = new List<string>();
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@WorkflowIdValue", workflowIdValue);
        cmd.Parameters.AddWithValue("@WorkflowTableKey", workflowTableKey);
        cmd.Parameters.AddWithValue("@WorkflowInstanceIdStr", workflowInstanceIdStr);
        cmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(0))
                users.Add(reader.GetString(0));
        }

        return users;
    }

    /// <summary>Everyone who created, updated, or was assigned on any transaction for this ticket.</summary>
    private static async Task<List<string>> ListTransactionParticipantUserIdsAsync(
        NpgsqlConnection connection,
        string transactionTable,
        Guid workflowInstanceId,
        CancellationToken cancellationToken)
    {
        var sql = $"""
SELECT DISTINCT uid
FROM (
    SELECT created_by::text AS uid
    FROM {transactionTable}
    WHERE workflow_instance_id = @WorkflowInstanceId AND is_deleted = false
    UNION
    SELECT modified_by::text
    FROM {transactionTable}
    WHERE workflow_instance_id = @WorkflowInstanceId AND is_deleted = false
    UNION
    SELECT activity_user_id::text
    FROM {transactionTable}
    WHERE workflow_instance_id = @WorkflowInstanceId AND is_deleted = false
) s
WHERE uid IS NOT NULL AND BTRIM(uid) <> '' AND uid <> '00000000-0000-0000-0000-000000000000';
""";
        var users = new List<string>();
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(0))
                users.Add(reader.GetString(0));
        }

        return users;
    }

    private static string BuildMailboxSourceSelect(string transactionTable, string instancesTable)
    {
        return $@"
    SELECT
        COALESCE(@OverrideUserId, t.activity_user_id::text) AS user_id,
        t.activity_group_id AS group_id,
        @WorkflowIdValue AS workflow_id,
        w.""Name"" AS name,
        @WorkflowInstanceIdStr AS workflow_instance_id,
        wi.reference_number AS reference_number,
        wi.created_at_utc AS created_at_utc,
        wi.started_at_utc AS started_at_utc,
        wi.completed_at_utc AS completed_at_utc,
        wi.context AS context,
        @TxGuidStr AS transaction_id,
        t.activity_id AS activity_id,
        t.rule_id AS rule_id,
        t.stage_type AS stage_type,
        t.stage_name AS stage,
        t.review AS review,
        t.created_at AS transaction_created_at,
        t.created_by::text AS transaction_created_by,
        cu.""Email"" AS transaction_created_by_email,
        t.modified_at AS transaction_modified_at,
        t.modified_by::text AS transaction_modified_by,
        au.""Email"" AS activity_user_email,
        @RepositoryId AS repository_id,
        @ItemId AS item_id,
        @FormId AS form_id,
        @FormEntryId AS form_entry_id,
        @FormData AS form_data,
        @Action AS ""action""
    FROM {transactionTable} t
    INNER JOIN {instancesTable} wi ON wi.id = t.workflow_instance_id
    LEFT JOIN workflow.""Workflows"" w ON w.""Id"" = @WorkflowGuid AND w.""IsDeleted"" = false
    LEFT JOIN users.""Users"" cu ON cu.""Id"" = t.created_by AND cu.""IsDeleted"" = false
    LEFT JOIN users.""Users"" au ON au.""Id"" = t.activity_user_id AND au.""IsDeleted"" = false
    WHERE t.id = @TransactionRowId AND t.is_deleted = false";
    }

    /// <summary>
    /// Removes any existing mailbox row for the same workflow + instance + activity (all three tables).
    /// </summary>
    private static async Task DeleteFromAllMailboxTablesByKeyAsync(
        NpgsqlConnection connection,
        string workflowIdValue,
        string workflowTableKey,
        Guid workflowInstanceId,
        string workflowInstanceIdStr,
        string? activityId,
        string inboxTable,
        string sentTable,
        string completedTable,
        CancellationToken cancellationToken)
    {
        var keyPredicate = $"""
            (workflow_id = @WorkflowIdValue OR workflow_id = @WorkflowTableKey)
            AND (
                workflow_instance_id = @WorkflowInstanceIdStr
                OR {TryCastUuid("workflow_instance_id")} = @WorkflowInstanceId
            )
            AND (
                (@ActivityId IS NULL AND (activity_id IS NULL OR TRIM(activity_id) = ''))
                OR TRIM(activity_id) = TRIM(@ActivityId)
            )
            """;

        var sql = $@"
DELETE FROM {inboxTable} WHERE {keyPredicate};
DELETE FROM {sentTable} WHERE {keyPredicate};
DELETE FROM {completedTable} WHERE {keyPredicate};";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@WorkflowIdValue", workflowIdValue);
        cmd.Parameters.AddWithValue("@WorkflowTableKey", workflowTableKey);
        cmd.Parameters.AddWithValue("@WorkflowInstanceIdStr", workflowInstanceIdStr);
        cmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
        // Explicit NpgsqlDbType.Varchar (not AddWithValue): @ActivityId is also used in a bare
        // "@ActivityId IS NULL" test with no column context at that node, and Postgres can
        // fail to infer the parameter's type from the other, typed usage (TRIM(@ActivityId))
        // alone -- confirmed empirically elsewhere in this migration (42P08 "could not
        // determine data type of parameter"), so this is typed defensively up front.
        cmd.Parameters.Add(new NpgsqlParameter("ActivityId", NpgsqlDbType.Varchar)
        {
            Value = (object?)activityId ?? DBNull.Value
        });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Removes all mailbox rows for a workflow instance in one table (any activity).</summary>
    private static async Task DeleteMailboxRowsForInstanceAsync(
        NpgsqlConnection connection,
        string workflowIdValue,
        string workflowTableKey,
        Guid workflowInstanceId,
        string workflowInstanceIdStr,
        string tableFull,
        CancellationToken cancellationToken)
    {
        var instancePredicate = $"""
            (workflow_id = @WorkflowIdValue OR workflow_id = @WorkflowTableKey)
            AND (
                workflow_instance_id = @WorkflowInstanceIdStr
                OR {TryCastUuid("workflow_instance_id")} = @WorkflowInstanceId
            )
            """;

        var sql = $"DELETE FROM {tableFull} WHERE {instancePredicate};";
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@WorkflowIdValue", workflowIdValue);
        cmd.Parameters.AddWithValue("@WorkflowTableKey", workflowTableKey);
        cmd.Parameters.AddWithValue("@WorkflowInstanceIdStr", workflowInstanceIdStr);
        cmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Self-assign: remove sent row when the same user receives the instance back in inbox.</summary>
    private static async Task DeleteSentRowsForInstanceAndUserAsync(
        NpgsqlConnection connection,
        string workflowIdValue,
        string workflowTableKey,
        Guid workflowInstanceId,
        string workflowInstanceIdStr,
        string sentTable,
        Guid assigneeUserId,
        CancellationToken cancellationToken)
    {
        await DeleteMailboxRowsForInstanceAndUserAsync(
            connection,
            workflowIdValue,
            workflowTableKey,
            workflowInstanceId,
            workflowInstanceIdStr,
            sentTable,
            assigneeUserId,
            cancellationToken);
    }

    private static async Task DeleteMailboxRowsForInstanceAndUserAsync(
        NpgsqlConnection connection,
        string workflowIdValue,
        string workflowTableKey,
        Guid workflowInstanceId,
        string workflowInstanceIdStr,
        string tableFull,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var predicate = $"""
            (workflow_id = @WorkflowIdValue OR workflow_id = @WorkflowTableKey)
            AND (
                workflow_instance_id = @WorkflowInstanceIdStr
                OR {TryCastUuid("workflow_instance_id")} = @WorkflowInstanceId
            )
            AND (
                user_id = @AssigneeUserId
                OR {TryCastUuid("user_id")} = @AssigneeUserGuid
            )
            """;

        var sql = $"DELETE FROM {tableFull} WHERE {predicate};";
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@WorkflowIdValue", workflowIdValue);
        cmd.Parameters.AddWithValue("@WorkflowTableKey", workflowTableKey);
        cmd.Parameters.AddWithValue("@WorkflowInstanceIdStr", workflowInstanceIdStr);
        cmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
        cmd.Parameters.AddWithValue("@AssigneeUserId", userId.ToString("D"));
        cmd.Parameters.AddWithValue("@AssigneeUserGuid", userId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string MailboxTable(string prefix, string tableSuffix) =>
        $"workflow.{prefix}_{tableSuffix}";

    private static async Task<MailboxExtraData> ResolveMailboxExtraDataAsync(
        NpgsqlConnection connection,
        string suffix,
        Guid workflowId,
        Guid workflowInstanceId,
        MailboxFormSnapshot? formOverride,
        CancellationToken cancellationToken)
    {
        var attachmentTable = $"workflow.workflow_attachments_{suffix}";
        var processFormTable = $"workflow.process_form_{suffix}";

        Guid? repositoryId = null;
        Guid? itemId = null;
        string? formId = null;
        string? formEntryId = null;
        string? formData = null;

        var attachmentSql = $@"
SELECT
    repository_id,
    item_id,
    form_json_id
FROM {attachmentTable}
WHERE workflow_instance_id = @WorkflowInstanceId
  AND is_deleted = false
ORDER BY COALESCE(modified_at_utc, created_at_utc) DESC, created_at_utc DESC
LIMIT 1;";

        await using (var attachmentCmd = new NpgsqlCommand(attachmentSql, connection))
        {
            attachmentCmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
            await using var reader = await attachmentCmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                repositoryId = ReadGuidOrNull(reader, 0);
                itemId = ReadGuidOrNull(reader, 1);
                formId = reader.IsDBNull(2) ? null : reader.GetString(2);
            }
        }

        var processFormSql = $@"
SELECT
    w_form_id,
    form_entry_id
FROM {processFormTable}
WHERE workflow_instance_id = @WorkflowInstanceId
  AND is_deleted = false
ORDER BY id DESC
LIMIT 1;";

        await using (var processCmd = new NpgsqlCommand(processFormSql, connection))
        {
            processCmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
            await using var reader = await processCmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                // process_form.w_form_id is the authoritative form identifier for FormEntryId rows.
                formId = reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0));
                formEntryId = reader.IsDBNull(1) ? null : Convert.ToString(reader.GetValue(1));
            }
        }

        if (string.IsNullOrWhiteSpace(formId))
            formId = await ResolveWorkflowFormIdAsync(connection, workflowId, cancellationToken);

        if (formOverride != null)
        {
            if (!string.IsNullOrWhiteSpace(formOverride.FormId))
                formId = formOverride.FormId.Trim();
            if (formOverride.FormEntryId is { } overrideEntryId && overrideEntryId != Guid.Empty)
                formEntryId = overrideEntryId.ToString("D");
            if (!string.IsNullOrWhiteSpace(formOverride.FormDataJson))
                formData = formOverride.FormDataJson;
        }

        if (string.IsNullOrWhiteSpace(formData)
            && !string.IsNullOrWhiteSpace(formId)
            && !string.IsNullOrWhiteSpace(formEntryId)
            && Guid.TryParse(formEntryId, out var entryId)
            && entryId != Guid.Empty)
        {
            formData = await WorkflowEzfbFormDataLoader.LoadFormDataJsonAsync(
                connection, formId!, entryId, cancellationToken);
        }

        return new MailboxExtraData(repositoryId, itemId, formId, formEntryId, formData);
    }

    // LoadFormDataJsonAsync moved to WorkflowEzfbFormDataLoader (correct wFormId + ezfb column resolution).

    private static Guid? ReadGuidOrNull(NpgsqlDataReader reader, int index)
    {
        if (reader.IsDBNull(index))
            return null;

        var value = reader.GetValue(index);
        return value switch
        {
            Guid guid => guid,
            string text when Guid.TryParse(text, out var parsed) => parsed,
            _ => null
        };
    }

    private static async Task<string?> ResolveWorkflowFormIdAsync(
        NpgsqlConnection connection,
        Guid workflowId,
        CancellationToken cancellationToken)
    {
        const string sql = """
SELECT "FormId"
FROM workflow."Workflows"
WHERE "Id" = @WorkflowId AND "IsDeleted" = false;
""";
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@WorkflowId", workflowId);
        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return value == null || value == DBNull.Value ? null : Convert.ToString(value)?.Trim();
    }

    private async Task EnsureMailboxTablesOnOpenConnectionAsync(
        Guid workflowId,
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        if (_tableCreator is WorkflowTableCreator creator)
        {
            await creator.EnsureLegacyMailboxTablesAsync(workflowId, connection, cancellationToken);
            return;
        }

        var connectionString = _tenantContext.ConnectionString;
        if (!string.IsNullOrWhiteSpace(connectionString))
            await _tableCreator.EnsureLegacyMailboxTablesAsync(workflowId, connectionString, cancellationToken);
    }

    public async Task PropagateInstanceFormDataAsync(
        Guid workflowId,
        Guid workflowInstanceId,
        MailboxFormSnapshot formData,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(formData.FormDataJson)
            && (string.IsNullOrWhiteSpace(formData.FormId)
                || formData.FormEntryId is not { } missingEntry
                || missingEntry == Guid.Empty))
            return;

        var connectionString = _tenantContext.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        await _tableCreator.EnsureLegacyMailboxTablesAsync(workflowId, connectionString, cancellationToken);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var suffix = workflowId.ToString("N")[..8];
        var workflowIdValue = workflowId.ToString("D");
        var workflowIdCompact = workflowId.ToString("N");
        var instanceStr = workflowInstanceId.ToString("D");

        var formId = formData.FormId?.Trim();
        var formEntryId = formData.FormEntryId is { } entry && entry != Guid.Empty
            ? entry.ToString("D")
            : null;
        var formDataJson = formData.FormDataJson;

        if (string.IsNullOrWhiteSpace(formId) || string.IsNullOrWhiteSpace(formEntryId))
        {
            var process = await ReadProcessFormIdentityAsync(connection, suffix, workflowInstanceId, cancellationToken);
            if (process != null)
            {
                formId = string.IsNullOrWhiteSpace(formId) ? process.Value.FormId : formId;
                formEntryId ??= process.Value.FormEntryId?.ToString("D");
            }
        }

        if (string.IsNullOrWhiteSpace(formId))
            return;

        if (string.IsNullOrWhiteSpace(formDataJson)
            && !string.IsNullOrWhiteSpace(formEntryId)
            && Guid.TryParse(formEntryId, out var entryGuid)
            && entryGuid != Guid.Empty)
        {
            formDataJson = await WorkflowEzfbFormDataLoader.LoadFormDataJsonAsync(
                connection, formId, entryGuid, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(formDataJson))
            return;

        foreach (var prefix in new[] { "inbox", "sent", "completed" })
        {
            var table = MailboxTable(prefix, suffix);
            var sql = string.IsNullOrWhiteSpace(formEntryId)
                ? $@"
UPDATE {table}
SET form_id = @FormId,
    form_data = @FormData
WHERE (workflow_id = @WorkflowIdValue OR workflow_id = @WorkflowTableKey)
  AND (
      workflow_instance_id = @WorkflowInstanceIdStr
      OR {TryCastUuid("workflow_instance_id")} = @WorkflowInstanceId
  );"
                : $@"
UPDATE {table}
SET form_id = @FormId,
    form_entry_id = @FormEntryId,
    form_data = @FormData
WHERE (workflow_id = @WorkflowIdValue OR workflow_id = @WorkflowTableKey)
  AND (
      workflow_instance_id = @WorkflowInstanceIdStr
      OR {TryCastUuid("workflow_instance_id")} = @WorkflowInstanceId
  );";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@FormId", formId);
            if (!string.IsNullOrWhiteSpace(formEntryId))
                cmd.Parameters.AddWithValue("@FormEntryId", formEntryId);
            cmd.Parameters.AddWithValue("@FormData", formDataJson);
            cmd.Parameters.AddWithValue("@WorkflowIdValue", workflowIdValue);
            cmd.Parameters.AddWithValue("@WorkflowTableKey", workflowIdCompact);
            cmd.Parameters.AddWithValue("@WorkflowInstanceIdStr", instanceStr);
            cmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);

            var rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
            if (rows > 0)
            {
                _logger.LogDebug(
                    "Propagated formData to {Count} row(s) in {Table} for instance {InstanceId}.",
                    rows,
                    table,
                    workflowInstanceId);
            }
        }
    }

    public async Task<(string? FormId, Guid? FormEntryId)?> TryGetProcessFormIdentityAsync(
        Guid workflowId,
        Guid workflowInstanceId,
        CancellationToken cancellationToken = default)
    {
        var connectionString = _tenantContext.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var suffix = workflowId.ToString("N")[..8];
        return await ReadProcessFormIdentityAsync(connection, suffix, workflowInstanceId, cancellationToken);
    }

    private static async Task<(string? FormId, Guid? FormEntryId)?> ReadProcessFormIdentityAsync(
        NpgsqlConnection connection,
        string suffix,
        Guid workflowInstanceId,
        CancellationToken cancellationToken)
    {
        var processFormTable = $"workflow.process_form_{suffix}";
        var sql = $@"
SELECT
    w_form_id,
    form_entry_id
FROM {processFormTable}
WHERE workflow_instance_id = @WorkflowInstanceId
  AND is_deleted = false
ORDER BY id DESC
LIMIT 1;";

        try
        {
            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            var formId = reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0));
            Guid? formEntryId = null;
            if (!reader.IsDBNull(1))
            {
                var raw = Convert.ToString(reader.GetValue(1));
                if (Guid.TryParse(raw, out var parsed) && parsed != Guid.Empty)
                    formEntryId = parsed;
            }

            if (string.IsNullOrWhiteSpace(formId) && formEntryId is null)
                return null;

            return (string.IsNullOrWhiteSpace(formId) ? null : formId.Trim(), formEntryId);
        }
        catch (PostgresException)
        {
            return null;
        }
    }
}
