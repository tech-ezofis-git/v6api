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
        int? inboxAction = null)
    {
        var connectionString = _tenantContext.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await SyncTransactionRowAsync(workflowId, transactionRowId, connection, formOverride: null, cancellationToken, inboxAction);
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
        int? inboxAction = null)
    {
        var connectionString = _tenantContext.ConnectionString;
        if (!string.IsNullOrWhiteSpace(connectionString))
            await _tableCreator.EnsureLegacyMailboxTablesAsync(workflowId, connectionString, cancellationToken);

        await SyncTransactionRowCoreAsync(workflowId, transactionRowId, connection, formOverride, cancellationToken, inboxAction);
    }

    public async Task SyncInstanceEndTransactionsAsync(
        Guid workflowId,
        Guid workflowInstanceId,
        NpgsqlConnection connection,
        MailboxFormSnapshot? formOverride = null,
        CancellationToken cancellationToken = default)
    {
        var connectionString = _tenantContext.ConnectionString;
        if (!string.IsNullOrWhiteSpace(connectionString))
            await _tableCreator.EnsureLegacyMailboxTablesAsync(workflowId, connectionString, cancellationToken);

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
        int? inboxAction = null)
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
    t.modified_by
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
        }

        var workflowInstanceIdStr = workflowInstanceId.ToString("D");
        var inboxTable = MailboxTable("inbox", suffix);
        var sentTable = MailboxTable("sent", suffix);
        var completedTable = MailboxTable("completed", suffix);

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

        var isEnd = string.Equals(stageType?.Trim(), EndStageType, StringComparison.OrdinalIgnoreCase);

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

        // Share-file: keep sharer (ModifiedBy) on inbox while guest (ActivityUserId) holds the open task.
        if (targetTable == inboxTable
            && modifiedByUserId is Guid shareOwnerId
            && shareOwnerId != Guid.Empty
            && activityUserId is Guid guestId
            && guestId != Guid.Empty
            && shareOwnerId != guestId)
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
            && ownerCcId != openAssigneeId)
        {
            await using var ccCmd = new NpgsqlCommand(insertSql, connection);
            ccCmd.Parameters.AddWithValue("@WorkflowGuid", workflowId);
            ccCmd.Parameters.AddWithValue("@WorkflowIdValue", workflowIdValue);
            ccCmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
            ccCmd.Parameters.AddWithValue("@WorkflowInstanceIdStr", workflowInstanceIdStr);
            ccCmd.Parameters.AddWithValue("@TransactionRowId", transactionRowId);
            ccCmd.Parameters.AddWithValue("@TxGuidStr", txIdStr);
            ccCmd.Parameters.AddWithValue("@OverrideUserId", ownerCcId.ToString("D"));
            ccCmd.Parameters.AddWithValue("@Action", resolvedAction);
            ccCmd.Parameters.AddWithValue("@RepositoryId", (object?)extras.RepositoryId?.ToString("D") ?? DBNull.Value);
            ccCmd.Parameters.AddWithValue("@ItemId", (object?)extras.ItemId?.ToString("D") ?? DBNull.Value);
            ccCmd.Parameters.AddWithValue("@FormId", (object?)extras.FormId ?? DBNull.Value);
            ccCmd.Parameters.AddWithValue("@FormEntryId", (object?)extras.FormEntryId ?? DBNull.Value);
            ccCmd.Parameters.AddWithValue("@FormData", (object?)extras.FormData ?? DBNull.Value);
            await ccCmd.ExecuteNonQueryAsync(cancellationToken);
        }
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

        var sql = $"DELETE FROM {sentTable} WHERE {predicate};";
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@WorkflowIdValue", workflowIdValue);
        cmd.Parameters.AddWithValue("@WorkflowTableKey", workflowTableKey);
        cmd.Parameters.AddWithValue("@WorkflowInstanceIdStr", workflowInstanceIdStr);
        cmd.Parameters.AddWithValue("@WorkflowInstanceId", workflowInstanceId);
        cmd.Parameters.AddWithValue("@AssigneeUserId", assigneeUserId.ToString("D"));
        cmd.Parameters.AddWithValue("@AssigneeUserGuid", assigneeUserId);
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

    public async Task PropagateInstanceFormDataAsync(
        Guid workflowId,
        Guid workflowInstanceId,
        MailboxFormSnapshot formData,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(formData.FormId) || formData.FormEntryId is not { } entryGuid || entryGuid == Guid.Empty)
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

        var formId = formData.FormId.Trim();
        var formEntryId = formData.FormEntryId.Value.ToString("D");
        var formDataJson = formData.FormDataJson;

        if (string.IsNullOrWhiteSpace(formDataJson))
        {
            formDataJson = await WorkflowEzfbFormDataLoader.LoadFormDataJsonAsync(
                connection, formId, formData.FormEntryId.Value, cancellationToken);
        }

        foreach (var prefix in new[] { "inbox", "sent", "completed" })
        {
            var table = MailboxTable(prefix, suffix);
            var sql = $@"
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
            cmd.Parameters.AddWithValue("@FormEntryId", formEntryId);
            cmd.Parameters.AddWithValue("@FormData", (object?)formDataJson ?? DBNull.Value);
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
}
