using Npgsql;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Infrastructure.Services;

public sealed class WorkflowLegacyMailboxQueryService : IWorkflowLegacyMailboxQueryService
{
    private const int MaxPageSize = 100;

    // m.workflow_instance_id is stored as varchar(255) (see WorkflowTableCreator.
    // GenerateLegacyMailboxTableScript), same as before -- Postgres has no TRY_CONVERT, so a
    // safe cast to uuid is this regex-guarded CASE (same pattern as FormService.Queries.cs).
    private const string InstanceIdGuard = "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$";
    private static string TryCastInstanceId(string column) =>
        $"(CASE WHEN {column} ~ '{InstanceIdGuard}' THEN {column}::uuid ELSE NULL END)";

    private readonly ITenantContext _tenantContext;
    private readonly IWorkflowEzfbFormDataLoader _formDataLoader;

    public WorkflowLegacyMailboxQueryService(
        ITenantContext tenantContext,
        IWorkflowEzfbFormDataLoader formDataLoader)
    {
        _tenantContext = tenantContext;
        _formDataLoader = formDataLoader;
    }

    public async Task<LegacyMailboxListResult> ListAsync(
        LegacyMailboxListRequest request,
        CancellationToken cancellationToken = default)
    {
        var connectionString = _tenantContext.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Tenant connection string not resolved.");

        var suffix = request.WorkflowId.ToString("N")[..8];
        var fullWorkflowKey = request.WorkflowId.ToString("N");
        var tablePrefix = request.Kind switch
        {
            LegacyMailboxTableKind.Inbox => "inbox",
            LegacyMailboxTableKind.Sent => "sent",
            LegacyMailboxTableKind.Completed => "completed",
            _ => throw new ArgumentOutOfRangeException(nameof(request.Kind))
        };
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var (tableName, tableFull, tableExists) = await ResolveMailboxTableAsync(
            connection, tablePrefix, suffix, fullWorkflowKey, cancellationToken);
        if (!tableExists)
        {
            return new LegacyMailboxListResult(
                Array.Empty<LegacyMailboxRowDto>(),
                0,
                request.PageNumber,
                request.PageSize,
                TableExists: false);
        }

        var page = request.PageNumber <= 0 ? 1 : request.PageNumber;
        var pageSize = request.PageSize <= 0 ? 20 : Math.Min(request.PageSize, MaxPageSize);
        var offset = (page - 1) * pageSize;

        var transactionTableName = $"transaction_{suffix}";
        var transactionTable = $"workflow.{transactionTableName}";
        var transactionTableExists = await TableExistsAsync(connection, transactionTableName, cancellationToken);
        var (whereSql, parameters) = BuildUserFilter(request, transactionTable, transactionTableExists);
        var latestOnlyPerInstance = ShouldReturnLatestOnlyPerInstance(request);

        var agentTable = $"agent_data_validation_{suffix}";
        var agentJoin = await BuildAgentValidationApplyAsync(connection, agentTable, cancellationToken);
        var dataSql = BuildListSql(tableFull, whereSql, agentJoin, latestOnlyPerInstance);

        if (request.SkipTotal)
        {
            var items = await ReadListPageAsync(connection, dataSql, parameters, offset, pageSize, cancellationToken);
            await EnrichFormDataAsync(items, cancellationToken);
            return new LegacyMailboxListResult(items, -1, page, pageSize, TableExists: true);
        }

        await using var countConnection = new NpgsqlConnection(connectionString);
        await countConnection.OpenAsync(cancellationToken);

        var countSql = BuildCountSql(tableFull, whereSql, latestOnlyPerInstance);
        var countTask = ExecuteCountAsync(countConnection, countSql, parameters, cancellationToken);
        var listTask = ReadListPageAsync(connection, dataSql, parameters, offset, pageSize, cancellationToken);

        await Task.WhenAll(countTask, listTask);

        var pageItems = await listTask;
        await EnrichFormDataAsync(pageItems, cancellationToken);

        return new LegacyMailboxListResult(pageItems, await countTask, page, pageSize, TableExists: true);
    }

    private async Task EnrichFormDataAsync(IList<LegacyMailboxRowDto> items, CancellationToken cancellationToken)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var row = items[i];
            if (!string.IsNullOrWhiteSpace(row.FormData))
                continue;
            if (string.IsNullOrWhiteSpace(row.FormId) || string.IsNullOrWhiteSpace(row.FormEntryId))
                continue;
            if (!Guid.TryParse(row.FormEntryId, out var entryId) || entryId == Guid.Empty)
                continue;

            var loaded = await _formDataLoader.LoadFormDataJsonAsync(row.FormId, entryId, cancellationToken);
            if (string.IsNullOrWhiteSpace(loaded))
                continue;

            items[i] = row with { FormData = loaded };
        }
    }

    public async Task<LegacyMailboxInstanceCountResult> GetInstanceCountsAsync(
        LegacyMailboxInstanceCountRequest request,
        CancellationToken cancellationToken = default)
    {
        var connectionString = _tenantContext.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Tenant connection string not resolved.");

        var suffix = request.WorkflowId.ToString("N")[..8];
        var fullWorkflowKey = request.WorkflowId.ToString("N");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var transactionTableName = $"transaction_{suffix}";
        var transactionTable = $"workflow.{transactionTableName}";
        var transactionTableExists = await TableExistsAsync(connection, transactionTableName, cancellationToken);
        var (inboxCount, inboxExists) = await CountMailboxTableAsync(
            connection, "inbox", suffix, fullWorkflowKey, request, LegacyMailboxTableKind.Inbox, transactionTable, transactionTableExists, cancellationToken);
        var (sentCount, sentExists) = await CountMailboxTableAsync(
            connection, "sent", suffix, fullWorkflowKey, request, LegacyMailboxTableKind.Sent, transactionTable, transactionTableExists, cancellationToken);
        var (completedCount, completedExists) = await CountMailboxTableAsync(
            connection, "completed", suffix, fullWorkflowKey, request, LegacyMailboxTableKind.Completed, transactionTable, transactionTableExists, cancellationToken);

        return new LegacyMailboxInstanceCountResult(
            request.WorkflowId,
            inboxCount,
            sentCount,
            completedCount,
            inboxExists,
            sentExists,
            completedExists);
    }

    private static async Task<(int Count, bool TableExists)> CountMailboxTableAsync(
        NpgsqlConnection connection,
        string tablePrefix,
        string suffix,
        string fullWorkflowKey,
        LegacyMailboxInstanceCountRequest request,
        LegacyMailboxTableKind kind,
        string transactionTable,
        bool transactionTableExists,
        CancellationToken cancellationToken)
    {
        var (tableName, tableFull, exists) = await ResolveMailboxTableAsync(
            connection, tablePrefix, suffix, fullWorkflowKey, cancellationToken);
        if (!exists)
            return (0, false);

        var (whereSql, parameters) = BuildUserFilter(request, kind, transactionTable, transactionTableExists);
        var countSql = BuildCountSql(tableFull, whereSql, latestOnlyPerInstance: true);
        await using var cmd = new NpgsqlCommand(countSql, connection);
        foreach (var p in parameters)
            cmd.Parameters.Add(CloneParameter(p));

        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
        return (count, true);
    }

    private static (string WhereSql, List<NpgsqlParameter> Parameters) BuildUserFilter(
        LegacyMailboxInstanceCountRequest request,
        LegacyMailboxTableKind kind,
        string transactionTable,
        bool transactionTableExists) =>
        BuildUserFilterCore(
            request.CurrentUserId, kind, transactionTable, transactionTableExists,
            instanceId: null, transactionId: null);

    private static (string WhereSql, List<NpgsqlParameter> Parameters) BuildUserFilter(
        LegacyMailboxListRequest request,
        string transactionTable,
        bool transactionTableExists) =>
        BuildUserFilterCore(
            request.CurrentUserId,
            request.Kind,
            transactionTable,
            transactionTableExists,
            request.InstanceId,
            request.TransactionId);

    private static (string WhereSql, List<NpgsqlParameter> Parameters) BuildUserFilterCore(
        Guid currentUserId,
        LegacyMailboxTableKind kind,
        string transactionTable,
        bool transactionTableExists,
        Guid? instanceId,
        string? transactionId)
    {
        var userId = currentUserId.ToString("D");
        var whereParts = new List<string>();
        var parameters = new List<NpgsqlParameter>
        {
            new("@CurrentUserId", userId),
            new("@CurrentUserGuid", currentUserId)
        };

        if (kind == LegacyMailboxTableKind.Inbox)
        {
            if (transactionTableExists)
            {
                // Narrow mailbox rows first (index on user_id), then open transaction exists.
                whereParts.Add(BuildMailboxUserMatchSql("m"));
                whereParts.AddRange(BuildInboxOpenTransactionFilter(transactionTable));
            }
            else
                whereParts.Add(BuildMailboxUserMatchSql("m"));
        }
        else
        {
            whereParts.Add(BuildMailboxUserMatchSql("m"));
            if (transactionTableExists)
                whereParts.AddRange(BuildTransactionStateFilter(kind, transactionTable));
        }

        if (instanceId is Guid instanceGuid && instanceGuid != Guid.Empty)
        {
            whereParts.Add("m.workflow_instance_id = @InstanceId");
            parameters.Add(new NpgsqlParameter("@InstanceId", instanceGuid.ToString("D")));
        }

        if (!string.IsNullOrWhiteSpace(transactionId))
        {
            whereParts.Add("m.transaction_id = @TransactionId");
            parameters.Add(new NpgsqlParameter("@TransactionId", transactionId.Trim()));
        }

        return (string.Join(" AND ", whereParts), parameters);
    }

    /// <summary>Assignee, creator, or group member — matches legacy inboxList visibility.</summary>
    private static string BuildMailboxUserMatchSql(string alias) => $"""
(
    {alias}.user_id = @CurrentUserId
    OR {alias}.transaction_created_by = @CurrentUserId
    OR (
        {alias}.group_id IS NOT NULL
        AND EXISTS (
            SELECT 1
            FROM workflow."groupUser" gu
            WHERE gu."GroupId" = {alias}.group_id
              AND gu."UserId" = @CurrentUserGuid
              AND gu."IsDeleted" = false
        )
    )
)
""";

    private static string BuildTransactionParticipantMatchSql(string alias) => $"""
(
    {alias}.activity_user_id = @CurrentUserGuid
    OR {alias}.created_by = @CurrentUserGuid
    OR {alias}.modified_by = @CurrentUserGuid
    OR (
        {alias}.activity_group_id IS NOT NULL
        AND EXISTS (
            SELECT 1
            FROM workflow."groupUser" gu
            WHERE gu."GroupId" = {alias}.activity_group_id
              AND gu."UserId" = @CurrentUserGuid
              AND gu."IsDeleted" = false
        )
    )
)
""";

    /// <summary>Inbox = open transaction (action_status 0) for this instance and user.</summary>
    private static IEnumerable<string> BuildInboxOpenTransactionFilter(string transactionTable)
    {
        var instanceJoin = $"{TryCastInstanceId("m.workflow_instance_id")} = tx.workflow_instance_id";
        var participantMatch = BuildTransactionParticipantMatchSql("tx");
        yield return $"""
EXISTS (
    SELECT 1
    FROM {transactionTable} tx
    WHERE tx.is_deleted = false
      AND tx.action_status = 0
      AND UPPER(TRIM(COALESCE(tx.stage_type, ''))) <> 'END'
      AND {instanceJoin}
      AND {participantMatch}
)
""";
    }

    /// <summary>Hide sent rows when the current user still has the same instance in inbox (self-assign).</summary>
    private static string BuildSentExcludeOpenInboxForCurrentUserFilter(string transactionTable)
    {
        var instanceJoin = $"{TryCastInstanceId("m.workflow_instance_id")} = tx_open.workflow_instance_id";
        var assigneeMatch = BuildTransactionParticipantMatchSql("tx_open");
        return $"""
NOT EXISTS (
    SELECT 1
    FROM {transactionTable} tx_open
    WHERE tx_open.is_deleted = false
      AND tx_open.action_status = 0
      AND UPPER(TRIM(COALESCE(tx_open.stage_type, ''))) <> 'END'
      AND {instanceJoin}
      AND {assigneeMatch}
)
""";
    }

    /// <summary>Sent: action_status 1. Completed: END stage.</summary>
    private static IEnumerable<string> BuildTransactionStateFilter(
        LegacyMailboxTableKind kind,
        string transactionTable)
    {
        var instanceJoin = $"{TryCastInstanceId("m.workflow_instance_id")} = tx.workflow_instance_id";
        var participantMatch = BuildTransactionParticipantMatchSql("tx");
        var workflowCompleted = $"""
NOT EXISTS (
    SELECT 1
    FROM {transactionTable} tx_end
    WHERE tx_end.is_deleted = false
      AND tx_end.workflow_instance_id = {TryCastInstanceId("m.workflow_instance_id")}
      AND UPPER(TRIM(COALESCE(tx_end.stage_type, ''))) = 'END'
)
""";

        switch (kind)
        {
            case LegacyMailboxTableKind.Sent:
                yield return workflowCompleted;
                yield return $"""
EXISTS (
    SELECT 1
    FROM {transactionTable} tx
    WHERE tx.is_deleted = false
      AND tx.action_status = 1
      AND UPPER(TRIM(COALESCE(tx.stage_type, ''))) <> 'END'
      AND {instanceJoin}
      AND {participantMatch}
)
""";
                // Same user still has an open inbox task — show inbox only, not sent.
                yield return BuildSentExcludeOpenInboxForCurrentUserFilter(transactionTable);
                break;
            case LegacyMailboxTableKind.Completed:
                yield return $"""
EXISTS (
    SELECT 1
    FROM {transactionTable} tx
    WHERE tx.is_deleted = false
      AND UPPER(TRIM(COALESCE(tx.stage_type, ''))) = 'END'
      AND {instanceJoin}
)
""";
                break;
        }
    }

    /// <summary>One current row per instance (latest transaction). Skip when a specific transaction is requested.</summary>
    private static bool ShouldReturnLatestOnlyPerInstance(LegacyMailboxListRequest request) =>
        string.IsNullOrWhiteSpace(request.TransactionId);

    private static string BuildCountSql(string tableFull, string whereSql, bool latestOnlyPerInstance)
    {
        if (!latestOnlyPerInstance)
            return $"SELECT COUNT(*) FROM {tableFull} m WHERE {whereSql};";

        return $@"
SELECT COUNT(*)
FROM (
    SELECT
        ROW_NUMBER() OVER (
            PARTITION BY m.workflow_instance_id
            ORDER BY m.transaction_created_at DESC, m.id DESC) AS mailbox_rn
    FROM {tableFull} m
    WHERE {whereSql}
) ranked
WHERE ranked.mailbox_rn = 1;";
    }

    private static string BuildListSql(string tableFull, string whereSql, string agentJoin, bool latestOnlyPerInstance)
    {
        const string selectColumns = """
    m.id, m.user_id, m.group_id, m.workflow_id, m.name, m.workflow_instance_id, m.reference_number, m.created_at_utc, m.started_at_utc, m.completed_at_utc, m.context,
    m.transaction_id, m.activity_id, m.rule_id, m.stage_type, m.stage, m.review,
    m.transaction_created_at, m.transaction_created_by, m.transaction_created_by_email,
    m.transaction_modified_at, m.transaction_modified_by,
    m.repository_id, m.item_id, m.form_id, m.form_entry_id, m.form_data,
    m.ml_prediction, m.ml_condition, m.user_type, m.created_by_name,
    m.last_action_stage_type, m.last_action_stage_name, m.last_action,
    m.comments_count, m.attachment_count, m.activity_user_email, m.activity_group_name,
    av.agent_validation_workflow_id,
    COALESCE(av.agent_response, '') AS agent_response,
    COALESCE(av.agent_html_response, '') AS agent_html,
    COALESCE(m."action", 1) AS action
""";

        if (!latestOnlyPerInstance)
        {
            return $@"
SELECT
{selectColumns}
FROM {tableFull} m
{agentJoin}
WHERE {whereSql}
ORDER BY m.transaction_created_at DESC, m.id DESC
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;";
        }

        return $@"
WITH mailbox_ranked AS (
    SELECT
        m.*,
        ROW_NUMBER() OVER (
            PARTITION BY m.workflow_instance_id
            ORDER BY m.transaction_created_at DESC, m.id DESC) AS mailbox_rn
    FROM {tableFull} m
    WHERE {whereSql}
),
page_rows AS (
    SELECT m.*
    FROM mailbox_ranked m
    WHERE m.mailbox_rn = 1
    ORDER BY m.transaction_created_at DESC, m.id DESC
    OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
)
SELECT
{selectColumns}
FROM page_rows m
{agentJoin}
ORDER BY m.transaction_created_at DESC, m.id DESC;";
    }

    private static async Task<int> ExecuteCountAsync(
        NpgsqlConnection connection,
        string countSql,
        List<NpgsqlParameter> parameters,
        CancellationToken cancellationToken)
    {
        await using var countCmd = new NpgsqlCommand(countSql, connection) { CommandTimeout = 120 };
        foreach (var p in parameters)
            countCmd.Parameters.Add(CloneParameter(p));
        return Convert.ToInt32(await countCmd.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<List<LegacyMailboxRowDto>> ReadListPageAsync(
        NpgsqlConnection connection,
        string dataSql,
        List<NpgsqlParameter> parameters,
        int offset,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var items = new List<LegacyMailboxRowDto>();
        await using var cmd = new NpgsqlCommand(dataSql, connection) { CommandTimeout = 120 };
        foreach (var p in parameters)
            cmd.Parameters.Add(CloneParameter(p));
        cmd.Parameters.AddWithValue("@Offset", offset);
        cmd.Parameters.AddWithValue("@PageSize", pageSize);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(MapRow(reader));
        return items;
    }

    private static async Task<(string TableName, string TableFull, bool Exists)> ResolveMailboxTableAsync(
        NpgsqlConnection connection,
        string tablePrefix,
        string suffix,
        string fullWorkflowKey,
        CancellationToken cancellationToken)
    {
        var tableName = $"{tablePrefix}_{suffix}";
        if (await TableExistsAsync(connection, tableName, cancellationToken))
            return (tableName, $"workflow.{tableName}", true);

        var legacyTableName = $"{tablePrefix}_{fullWorkflowKey}";
        if (!string.Equals(legacyTableName, tableName, StringComparison.Ordinal)
            && await TableExistsAsync(connection, legacyTableName, cancellationToken))
            return (legacyTableName, $"workflow.{legacyTableName}", true);

        return (tableName, $"workflow.{tableName}", false);
    }

    private static NpgsqlParameter CloneParameter(NpgsqlParameter p) =>
        new(p.ParameterName, p.Value ?? DBNull.Value) { NpgsqlDbType = p.NpgsqlDbType };

    private static async Task<bool> TableExistsAsync(
        NpgsqlConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(*)
            FROM information_schema.tables
            WHERE table_schema = 'workflow' AND table_name = @TableName
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TableName", tableName);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static LegacyMailboxRowDto MapRow(NpgsqlDataReader reader) =>
        new(
            Id: reader.GetInt32(0),
            UserId: reader.IsDBNull(1) ? null : reader.GetString(1),
            GroupId: reader.IsDBNull(2) ? null : reader.GetInt32(2),
            WorkflowId: reader.IsDBNull(3) ? null : reader.GetString(3),
            Name: reader.IsDBNull(4) ? null : reader.GetString(4),
            WorkflowInstanceId: reader.IsDBNull(5) ? null : reader.GetString(5),
            ReferenceNumber: reader.IsDBNull(6) ? null : reader.GetString(6),
            CreatedAtUtc: reader.IsDBNull(7) ? null : reader.GetDateTime(7),
            StartedAtUtc: reader.IsDBNull(8) ? null : reader.GetDateTime(8),
            CompletedAtUtc: reader.IsDBNull(9) ? null : reader.GetDateTime(9),
            Context: reader.IsDBNull(10) ? null : reader.GetString(10),
            TransactionId: reader.IsDBNull(11) ? null : reader.GetString(11),
            ActivityId: reader.IsDBNull(12) ? null : reader.GetString(12),
            RuleId: reader.IsDBNull(13) ? null : reader.GetString(13),
            StageType: reader.IsDBNull(14) ? null : reader.GetString(14),
            Stage: reader.IsDBNull(15) ? null : reader.GetString(15),
            Review: reader.IsDBNull(16) ? null : reader.GetString(16),
            TransactionCreatedAt: reader.IsDBNull(17) ? null : reader.GetDateTime(17),
            TransactionCreatedBy: reader.IsDBNull(18) ? null : reader.GetString(18),
            TransactionCreatedByEmail: reader.IsDBNull(19) ? null : reader.GetString(19),
            TransactionModifiedAt: reader.IsDBNull(20) ? null : reader.GetDateTime(20),
            TransactionModifiedBy: reader.IsDBNull(21) ? null : reader.GetString(21),
            RepositoryId: reader.IsDBNull(22) ? null : reader.GetString(22),
            ItemId: reader.IsDBNull(23) ? null : reader.GetString(23),
            FormId: reader.IsDBNull(24) ? null : reader.GetString(24),
            FormEntryId: reader.IsDBNull(25) ? null : reader.GetString(25),
            FormData: reader.IsDBNull(26) ? null : reader.GetString(26),
            MlPrediction: reader.IsDBNull(27) ? null : reader.GetString(27),
            MlCondition: reader.IsDBNull(28) ? null : reader.GetString(28),
            UserType: reader.IsDBNull(29) ? null : reader.GetString(29),
            CreatedByName: reader.IsDBNull(30) ? null : reader.GetString(30),
            LastActionStageType: reader.IsDBNull(31) ? null : reader.GetString(31),
            LastActionStageName: reader.IsDBNull(32) ? null : reader.GetString(32),
            LastAction: reader.IsDBNull(33) ? null : reader.GetString(33),
            CommentsCount: reader.IsDBNull(34) ? null : reader.GetInt32(34),
            AttachmentCount: reader.IsDBNull(35) ? null : reader.GetInt32(35),
            ActivityUserEmail: reader.IsDBNull(36) ? null : reader.GetString(36),
            ActivityGroupName: reader.IsDBNull(37) ? null : reader.GetString(37),
            AgentValidationWorkflowId: reader.IsDBNull(38) ? null : reader.GetString(38),
            AgentResponse: reader.IsDBNull(39) ? null : reader.GetString(39),
            AgentHtml: reader.IsDBNull(40) ? null : reader.GetString(40),
            Action: reader.FieldCount > 41 && !reader.IsDBNull(41) ? reader.GetInt32(41) : 1);

    private static async Task<string> BuildAgentValidationApplyAsync(
        NpgsqlConnection connection,
        string agentTableName,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, agentTableName, cancellationToken))
        {
            return """
LEFT JOIN LATERAL (
    SELECT
        NULL::text AS agent_validation_workflow_id,
        NULL::text AS agent_response,
        NULL::text AS agent_html_response
) av ON true
""";
        }

        return $@"
LEFT JOIN LATERAL (
    SELECT
        a.workflow_id::text AS agent_validation_workflow_id,
        a.agent_response,
        a.agent_html_response
    FROM workflow.{agentTableName} a
    WHERE a.is_deleted = false
      AND a.process_id = {TryCastInstanceId("m.workflow_instance_id")}
    ORDER BY a.created_at DESC, a.id DESC
    LIMIT 1
) av ON true";
    }
}
