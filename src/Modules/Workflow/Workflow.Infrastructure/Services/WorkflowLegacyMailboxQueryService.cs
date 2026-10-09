using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SaaSApp.Repository.Application.Contracts;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Application.Workflows;

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
    private readonly IRepositoryItemQueryService _repositoryItems;

    public WorkflowLegacyMailboxQueryService(
        ITenantContext tenantContext,
        IWorkflowEzfbFormDataLoader formDataLoader,
        IRepositoryItemQueryService repositoryItems)
    {
        _tenantContext = tenantContext;
        _formDataLoader = formDataLoader;
        _repositoryItems = repositoryItems;
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
        var keepStageRows = KeepMjbStageRows(request.WorkflowId);
        var (whereSql, parameters) = BuildUserFilter(request, transactionTable, transactionTableExists, keepStageRows);
        var latestOnlyPerInstance = !keepStageRows && ShouldReturnLatestOnlyPerInstance(request);

        var agentTable = $"agent_data_validation_{suffix}";
        var agentJoin = await BuildAgentValidationApplyAsync(connection, agentTable, cancellationToken);
        var currentStageJoin = BuildCurrentStageJoin(transactionTable, transactionTableExists);
        var dataSql = BuildListSql(tableFull, whereSql, agentJoin, currentStageJoin, latestOnlyPerInstance, keepStageRows);

        if (request.SkipTotal)
        {
            var items = await ReadListPageAsync(connection, dataSql, parameters, offset, pageSize, cancellationToken);
            await EnrichFormDataAsync(connection, items, cancellationToken);
            await EnrichRepositoryItemAsync(items, cancellationToken);
            return new LegacyMailboxListResult(items, -1, page, pageSize, TableExists: true);
        }

        var countSql = BuildCountSql(tableFull, whereSql, latestOnlyPerInstance);
        var totalCount = await ExecuteCountAsync(connection, countSql, parameters, cancellationToken);
        var pageItems = await ReadListPageAsync(connection, dataSql, parameters, offset, pageSize, cancellationToken);
        await EnrichFormDataAsync(connection, pageItems, cancellationToken);
        await EnrichRepositoryItemAsync(pageItems, cancellationToken);

        return new LegacyMailboxListResult(pageItems, totalCount, page, pageSize, TableExists: true);
    }

    public async Task<LegacyMailboxListResult> ListByActivityIdsAsync(
        LegacyMailboxByActivityRequest request,
        CancellationToken cancellationToken = default)
    {
        var activityIds = request.ActivityIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (activityIds.Length == 0)
        {
            return new LegacyMailboxListResult(Array.Empty<LegacyMailboxRowDto>(), 0, 1, 0, TableExists: false);
        }

        var connectionString = _tenantContext.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Tenant connection string not resolved.");

        var suffix = request.WorkflowId.ToString("N")[..8];
        var fullWorkflowKey = request.WorkflowId.ToString("N");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var inboxRows = await ReadMailboxByActivityIdsAsync(
            connection, LegacyMailboxTableKind.Inbox, "inbox", suffix, fullWorkflowKey, request, activityIds, cancellationToken);
        var sentRows = await ReadMailboxByActivityIdsAsync(
            connection, LegacyMailboxTableKind.Sent, "sent", suffix, fullWorkflowKey, request, activityIds, cancellationToken);
        var completedRows = await ReadMailboxByActivityIdsAsync(
            connection, LegacyMailboxTableKind.Completed, "completed", suffix, fullWorkflowKey, request, activityIds, cancellationToken);

        var items = KeepCurrentStageRows(inboxRows, sentRows, completedRows);

        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < activityIds.Length; i++)
            order[activityIds[i]] = i;
        items.Sort((a, b) =>
            order.GetValueOrDefault(a.ActivityId ?? string.Empty, int.MaxValue)
                .CompareTo(order.GetValueOrDefault(b.ActivityId ?? string.Empty, int.MaxValue)));

        await EnrichFormDataAsync(connection, items, cancellationToken);
        await EnrichRepositoryItemAsync(items, cancellationToken);

        var tableExists = inboxRows.Count > 0 || sentRows.Count > 0 || completedRows.Count > 0
            || await MailboxTableExistsAsync(connection, suffix, fullWorkflowKey, cancellationToken);
        return new LegacyMailboxListResult(items, items.Count, 1, activityIds.Length, tableExists);
    }

    public async Task<LegacyMailboxActivityCountResult> CountByActivityIdsAsync(
        LegacyMailboxByActivityRequest request,
        CancellationToken cancellationToken = default)
    {
        var listed = await ListByActivityIdsAsync(request, cancellationToken);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in request.ActivityIds.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
            counts[id] = 0;

        foreach (var row in listed.Items)
        {
            var activityId = row.ActivityId?.Trim();
            if (string.IsNullOrWhiteSpace(activityId))
                continue;

            var key = counts.Keys.FirstOrDefault(id => ActivityKeysMatch(id, activityId)) ?? activityId;
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        var items = counts.Select(pair => new LegacyMailboxActivityCountItem(pair.Key, pair.Value)).ToList();
        return new LegacyMailboxActivityCountResult(
            request.WorkflowId,
            items.Sum(item => item.Count),
            items);
    }

    private async Task<bool> MailboxTableExistsAsync(
        NpgsqlConnection connection,
        string suffix,
        string fullWorkflowKey,
        CancellationToken cancellationToken)
    {
        var inbox = await ResolveMailboxTableAsync(connection, "inbox", suffix, fullWorkflowKey, cancellationToken);
        if (inbox.Exists)
            return true;
        var sent = await ResolveMailboxTableAsync(connection, "sent", suffix, fullWorkflowKey, cancellationToken);
        if (sent.Exists)
            return true;
        var completed = await ResolveMailboxTableAsync(connection, "completed", suffix, fullWorkflowKey, cancellationToken);
        return completed.Exists;
    }

    /// <summary>One row per ticket: the mailbox row for the stage the ticket is on now.</summary>
    private static List<LegacyMailboxRowDto> KeepCurrentStageRows(
        IReadOnlyList<LegacyMailboxRowDto> inboxRows,
        IReadOnlyList<LegacyMailboxRowDto> sentRows,
        IReadOnlyList<LegacyMailboxRowDto> completedRows)
    {
        var byInstance = new Dictionary<string, LegacyMailboxRowDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in inboxRows.Concat(sentRows).Concat(completedRows))
        {
            var key = string.IsNullOrWhiteSpace(row.WorkflowInstanceId)
                ? $"row:{row.Id}"
                : row.WorkflowInstanceId.Trim();
            if (!byInstance.TryGetValue(key, out var existing) || MailboxRank(row.Mailbox) < MailboxRank(existing.Mailbox))
                byInstance[key] = row;
        }

        return byInstance.Values.ToList();
    }

    private static int MailboxRank(string? mailbox) =>
        mailbox switch
        {
            "inbox" => 0,
            "sent" => 1,
            "completed" => 2,
            _ => 3
        };

    private static bool ActivityKeysMatch(string left, string right)
    {
        var a = left.Trim();
        var b = right.Trim();
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return true;
        return Guid.TryParse(a, out var aId)
            && Guid.TryParse(b, out var bId)
            && aId == bId;
    }

    private async Task<List<LegacyMailboxRowDto>> ReadMailboxByActivityIdsAsync(
        NpgsqlConnection connection,
        LegacyMailboxTableKind kind,
        string tablePrefix,
        string suffix,
        string fullWorkflowKey,
        LegacyMailboxByActivityRequest request,
        string[] activityIds,
        CancellationToken cancellationToken)
    {
        var (_, tableFull, tableExists) = await ResolveMailboxTableAsync(
            connection, tablePrefix, suffix, fullWorkflowKey, cancellationToken);
        if (!tableExists)
            return [];

        var transactionTableName = $"transaction_{suffix}";
        var transactionTable = $"workflow.{transactionTableName}";
        var transactionTableExists = await TableExistsAsync(connection, transactionTableName, cancellationToken);
        var pageSize = 5000;
        var listRequest = new LegacyMailboxListRequest(
            kind,
            request.WorkflowId,
            InstanceId: null,
            TransactionId: null,
            request.CurrentUserId,
            PageNumber: 1,
            PageSize: pageSize);
        var (whereSql, parameters) = BuildUserFilter(listRequest, transactionTable, transactionTableExists);
        whereSql += " AND LOWER(BTRIM(m.activity_id)) = ANY(@ActivityIds)";
        parameters.Add(new NpgsqlParameter("@ActivityIds", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = activityIds.Select(x => x.ToLowerInvariant()).ToArray()
        });
        if (transactionTableExists)
            whereSql += " AND " + BuildCurrentActivityMatchSql(transactionTable);

        var agentTable = $"agent_data_validation_{suffix}";
        var agentJoin = await BuildAgentValidationApplyAsync(connection, agentTable, cancellationToken);
        var currentStageJoin = BuildCurrentStageJoin(transactionTable, transactionTableExists);
        var dataSql = BuildListSql(tableFull, whereSql, agentJoin, currentStageJoin, latestOnlyPerInstance: true);
        var mailbox = kind switch
        {
            LegacyMailboxTableKind.Inbox => "inbox",
            LegacyMailboxTableKind.Sent => "sent",
            _ => "completed"
        };
        var rows = await ReadListPageAsync(connection, dataSql, parameters, 0, pageSize, cancellationToken);
        return rows.Select(row => row with { Mailbox = mailbox }).ToList();
    }

    /// <summary>
    /// Keeps a mailbox row only when its activity is the ticket's current stage.
    /// Open step wins. After workflow success, the END stage (Workflow Success) wins,
    /// so an older sent row is not returned on a previous stage.
    /// </summary>
    private static string BuildCurrentActivityMatchSql(string transactionTable)
    {
        var instanceJoin = $"{TryCastInstanceId("m.workflow_instance_id")} = cur.workflow_instance_id";
        return $"""
EXISTS (
    SELECT 1
    FROM (
        SELECT DISTINCT ON (tx.workflow_instance_id)
            tx.workflow_instance_id,
            tx.activity_id
        FROM {transactionTable} tx
        WHERE tx.is_deleted = false
        ORDER BY tx.workflow_instance_id,
            CASE
                WHEN tx.action_status = 0 AND UPPER(TRIM(COALESCE(tx.stage_type, ''))) <> 'END' THEN 0
                WHEN UPPER(TRIM(COALESCE(tx.stage_type, ''))) = 'END' THEN 1
                ELSE 2
            END,
            tx.id DESC
    ) cur
    WHERE {instanceJoin}
      AND REPLACE(LOWER(BTRIM(COALESCE(cur.activity_id, ''))), '-', '')
        = REPLACE(LOWER(BTRIM(COALESCE(m.activity_id, ''))), '-', '')
)
""";
    }

    private async Task EnrichFormDataAsync(
        NpgsqlConnection connection,
        IList<LegacyMailboxRowDto> items,
        CancellationToken cancellationToken)
    {
        var controlsByForm = new Dictionary<string, List<FormControlSlot>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < items.Count; i++)
        {
            var row = items[i];
            var formData = row.FormData;
            if (string.IsNullOrWhiteSpace(formData)
                && !string.IsNullOrWhiteSpace(row.FormId)
                && Guid.TryParse(row.FormEntryId, out var entryId)
                && entryId != Guid.Empty)
            {
                formData = await _formDataLoader.LoadFormDataJsonAsync(row.FormId, entryId, cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(row.FormId))
            {
                if (!string.Equals(formData, row.FormData, StringComparison.Ordinal))
                    items[i] = row with { FormData = formData };
                continue;
            }

            if (!controlsByForm.TryGetValue(row.FormId, out var controls))
            {
                controls = await LoadRootFormControlsAsync(connection, row.FormId, cancellationToken);
                controlsByForm[row.FormId] = controls;
            }

            formData = IncludeEveryControl(formData, controls);
            if (!string.Equals(formData, row.FormData, StringComparison.Ordinal))
                items[i] = row with { FormData = formData };
        }
    }

    private static async Task<List<FormControlSlot>> LoadRootFormControlsAsync(
        NpgsqlConnection connection,
        string formId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT "jsonId", type
            FROM dbo."wFormControl"
            WHERE "wFormId" = @FormId
              AND "isDeleted" = false
              AND COALESCE("parentId", 0) = 0
              AND "jsonId" IS NOT NULL
              AND BTRIM("jsonId") <> ''
            ORDER BY id
            """;
        var controls = new List<FormControlSlot>();
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@FormId", formId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var type = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            controls.Add(new FormControlSlot(
                reader.GetString(0).Trim(),
                type.Contains("TABLE", StringComparison.OrdinalIgnoreCase)));
        }

        return controls;
    }

    /// <summary>Every root control jsonId is returned. Missing values are "" and missing tables are [].</summary>
    private static string? IncludeEveryControl(string? formData, IReadOnlyList<FormControlSlot> controls)
    {
        if (controls.Count == 0)
            return formData;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(formData))
            {
                try
                {
                    using var doc = JsonDocument.Parse(formData);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (!written.Add(prop.Name))
                                continue;
                            writer.WritePropertyName(prop.Name);
                            prop.Value.WriteTo(writer);
                        }
                    }
                }
                catch (JsonException)
                {
                }
            }

            foreach (var control in controls)
            {
                if (!written.Add(control.JsonId))
                    continue;
                writer.WritePropertyName(control.JsonId);
                if (control.IsTable)
                {
                    writer.WriteStartArray();
                    writer.WriteEndArray();
                }
                else
                {
                    writer.WriteStringValue(string.Empty);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed record FormControlSlot(string JsonId, bool IsTable);

    private async Task EnrichRepositoryItemAsync(IList<LegacyMailboxRowDto> items, CancellationToken cancellationToken)
    {
        if (_tenantContext.TenantId is not Guid tenantId || tenantId == Guid.Empty)
            return;

        var cache = new Dictionary<(Guid RepoId, Guid ItemId), LegacyMailboxRepositoryItemDto?>();

        for (var i = 0; i < items.Count; i++)
        {
            var row = items[i];
            if (string.IsNullOrWhiteSpace(row.RepositoryId) || string.IsNullOrWhiteSpace(row.ItemId))
                continue;
            if (!Guid.TryParse(row.RepositoryId, out var repoId) || repoId == Guid.Empty)
                continue;
            if (!Guid.TryParse(row.ItemId, out var itemId) || itemId == Guid.Empty)
                continue;

            var key = (repoId, itemId);
            if (!cache.TryGetValue(key, out var snapshot))
            {
                snapshot = await LoadRepositoryItemSnapshotAsync(tenantId, repoId, itemId, cancellationToken);
                cache[key] = snapshot;
            }

            if (snapshot is null)
                continue;

            items[i] = row with { RepositoryItem = snapshot };
        }
    }

    private async Task<LegacyMailboxRepositoryItemDto?> LoadRepositoryItemSnapshotAsync(
        Guid tenantId,
        Guid repositoryId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        try
        {
            var detail = await _repositoryItems.GetItemAsync(repositoryId, tenantId, itemId, cancellationToken);
            if (detail is null)
                return null;

            return new LegacyMailboxRepositoryItemDto(
                detail.FileName,
                detail.FilePath,
                detail.FileType,
                detail.FileSize,
                detail.Fields);
        }
        catch
        {
            // Missing repo / deleted item must not break mailbox list.
            return null;
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

        var keepStageRows = KeepMjbStageRows(request.WorkflowId);
        var (whereSql, parameters) = BuildUserFilter(request, kind, transactionTable, transactionTableExists, keepStageRows);
        var countSql = BuildCountSql(tableFull, whereSql, latestOnlyPerInstance: !keepStageRows);
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
        bool transactionTableExists,
        bool keepStageRows = false) =>
        BuildUserFilterCore(
            request.CurrentUserId, kind, transactionTable, transactionTableExists,
            instanceId: null, transactionId: null, keepStageRows);

    private static (string WhereSql, List<NpgsqlParameter> Parameters) BuildUserFilter(
        LegacyMailboxListRequest request,
        string transactionTable,
        bool transactionTableExists,
        bool keepStageRows = false) =>
        BuildUserFilterCore(
            request.CurrentUserId,
            request.Kind,
            transactionTable,
            transactionTableExists,
            request.InstanceId,
            request.TransactionId,
            keepStageRows);

    private static bool KeepMjbStageRows(Guid workflowId) =>
        workflowId == MjbUsAgent.WorkflowId;

    private static (string WhereSql, List<NpgsqlParameter> Parameters) BuildUserFilterCore(
        Guid currentUserId,
        LegacyMailboxTableKind kind,
        string transactionTable,
        bool transactionTableExists,
        Guid? instanceId,
        string? transactionId,
        bool keepStageRows = false)
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
                // Inbox is only the current assignee (not the starter/CreatedBy of the next step).
                whereParts.Add(BuildInboxMailboxUserMatchSql("m"));
                whereParts.AddRange(BuildInboxOpenTransactionFilter(transactionTable));
            }
            else
                whereParts.Add(BuildInboxMailboxUserMatchSql("m"));
        }
        else
        {
            whereParts.Add(BuildMailboxUserMatchSql("m"));
            if (transactionTableExists)
                whereParts.AddRange(BuildTransactionStateFilter(kind, transactionTable, keepStageRows));
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

    /// <summary>Inbox: current assignee (or group), not transaction CreatedBy.</summary>
    private static string BuildInboxMailboxUserMatchSql(string alias) => $"""
(
    {alias}.user_id = @CurrentUserId
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

    /// <summary>Sent/Completed: assignee, creator, or group member.</summary>
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

    private static string BuildInboxAssigneeMatchSql(string alias) => $"""
(
    {alias}.activity_user_id = @CurrentUserGuid
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
        var assigneeMatch = BuildInboxAssigneeMatchSql("tx");
        yield return $"""
EXISTS (
    SELECT 1
    FROM {transactionTable} tx
    WHERE tx.is_deleted = false
      AND tx.action_status = 0
      AND UPPER(TRIM(COALESCE(tx.stage_type, ''))) <> 'END'
      AND {instanceJoin}
      AND {assigneeMatch}
)
""";
    }

    /// <summary>Hide sent rows when the current user still has the same instance in inbox (self-assign).</summary>
    private static string BuildSentExcludeOpenInboxForCurrentUserFilter(string transactionTable)
    {
        var instanceJoin = $"{TryCastInstanceId("m.workflow_instance_id")} = tx_open.workflow_instance_id";
        var assigneeMatch = BuildInboxAssigneeMatchSql("tx_open");
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

    /// <summary>
    /// Sent: submitted steps (action_status 1), or open Forward watches (still open but current user is not assignee).
    /// Completed: END stage.
    /// </summary>
    private static IEnumerable<string> BuildTransactionStateFilter(
        LegacyMailboxTableKind kind,
        string transactionTable,
        bool keepStageRows = false)
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
                // Include open Forward watches: step stays action_status=0 after Forward,
                // but forwarder must still see the ticket in Sent until Completed.
                yield return $"""
EXISTS (
    SELECT 1
    FROM {transactionTable} tx
    WHERE tx.is_deleted = false
      AND UPPER(TRIM(COALESCE(tx.stage_type, ''))) <> 'END'
      AND {instanceJoin}
      AND {participantMatch}
      AND (
            tx.action_status = 1
         OR (
              tx.action_status = 0
              AND (tx.activity_user_id IS NULL OR tx.activity_user_id IS DISTINCT FROM @CurrentUserGuid)
            )
          )
)
""";
                // Same user still has an open inbox task — show inbox only, not sent.
                // MJB keeps the finished stage in sent while the next agent step is in inbox.
                if (!keepStageRows)
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

    /// <summary>Ticket current stage: open step first, else END, else latest transaction.</summary>
    private static string BuildCurrentStageJoin(string transactionTable, bool transactionTableExists)
    {
        if (!transactionTableExists)
        {
            return """
LEFT JOIN LATERAL (
    SELECT
        NULL::text AS current_stage_type,
        NULL::text AS current_stage_name,
        NULL::text AS current_activity_user_email
) cs ON true
""";
        }

        return $@"
LEFT JOIN LATERAL (
    SELECT
        tx.stage_type AS current_stage_type,
        tx.stage_name AS current_stage_name,
        au.""Email"" AS current_activity_user_email
    FROM {transactionTable} tx
    LEFT JOIN users.""Users"" au ON au.""Id"" = tx.activity_user_id AND au.""IsDeleted"" = false
    WHERE tx.is_deleted = false
      AND tx.workflow_instance_id = {TryCastInstanceId("m.workflow_instance_id")}
    ORDER BY
        CASE
            WHEN tx.action_status = 0 AND UPPER(TRIM(COALESCE(tx.stage_type, ''))) <> 'END' THEN 0
            WHEN UPPER(TRIM(COALESCE(tx.stage_type, ''))) = 'END' THEN 1
            ELSE 2
        END,
        tx.id DESC
    LIMIT 1
) cs ON true";
    }

    private static string BuildListSql(
        string tableFull,
        string whereSql,
        string agentJoin,
        string currentStageJoin,
        bool latestOnlyPerInstance,
        bool useRowStage = false)
    {
        // Other workflows show the ticket's current stage. MJB shows the stage stored on that inbox/sent/completed row.
        var stageTypeSql = useRowStage
            ? "m.stage_type AS stage_type"
            : "COALESCE(cs.current_stage_type, m.stage_type) AS stage_type";
        var stageSql = useRowStage
            ? "m.stage AS stage"
            : "COALESCE(cs.current_stage_name, m.stage) AS stage";
        var selectColumns = $"""
    m.id, m.user_id, m.group_id, m.workflow_id, m.name, m.workflow_instance_id, m.reference_number, m.created_at_utc, m.started_at_utc, m.completed_at_utc, m.context,
    m.transaction_id, m.activity_id, m.rule_id,
    {stageTypeSql},
    {stageSql},
    m.review,
    m.transaction_created_at, m.transaction_created_by, m.transaction_created_by_email,
    m.transaction_modified_at, m.transaction_modified_by,
    m.repository_id, m.item_id, m.form_id, m.form_entry_id, m.form_data,
    m.ml_prediction, m.ml_condition, m.user_type, m.created_by_name,
    COALESCE(m.last_action_stage_type, m.stage_type) AS last_action_stage_type,
    COALESCE(m.last_action_stage_name, m.stage) AS last_action_stage_name,
    m.last_action,
    m.comments_count, m.attachment_count,
    COALESCE(cs.current_activity_user_email, m.activity_user_email) AS activity_user_email,
    m.activity_group_name,
    av.agent_validation_workflow_id,
    COALESCE(av.agent_response, '') AS agent_response,
    COALESCE(av.agent_html_response, '') AS agent_html,
    av.qualify_agent_response,
    av.quote_agent_response,
    av.classification_agent_response,
    av.ocr_agent_response,
    av.ftp_agent_response,
    COALESCE(m."action", 1) AS action
""";

        if (!latestOnlyPerInstance)
        {
            return $@"
SELECT
{selectColumns}
FROM {tableFull} m
{currentStageJoin}
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
{currentStageJoin}
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
            QualifyAgentResponse: reader.FieldCount > 41 && !reader.IsDBNull(41) ? reader.GetString(41) : null,
            QuoteAgentResponse: reader.FieldCount > 42 && !reader.IsDBNull(42) ? reader.GetString(42) : null,
            ClassificationAgentResponse: reader.FieldCount > 43 && !reader.IsDBNull(43) ? reader.GetString(43) : null,
            OcrAgentResponse: reader.FieldCount > 44 && !reader.IsDBNull(44) ? reader.GetString(44) : null,
            FtpAgentResponse: reader.FieldCount > 45 && !reader.IsDBNull(45) ? reader.GetString(45) : null,
            Action: reader.FieldCount > 46 && !reader.IsDBNull(46) ? reader.GetInt32(46) : 1);

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
        NULL::text AS agent_html_response,
        NULL::text AS qualify_agent_response,
        NULL::text AS quote_agent_response,
        NULL::text AS classification_agent_response,
        NULL::text AS ocr_agent_response,
        NULL::text AS ftp_agent_response
) av ON true
""";
        }

        return $@"
LEFT JOIN LATERAL (
    SELECT
        a.workflow_id::text AS agent_validation_workflow_id,
        a.agent_response,
        a.agent_html_response,
        {AgentResponseByTypeSql(agentTableName, "'QUALIFY_AGENT'")} AS qualify_agent_response,
        {AgentResponseByTypeSql(agentTableName, "'QUOTE_AGENT'")} AS quote_agent_response,
        {AgentResponseByTypeSql(agentTableName, "'CLASSIFICATION_AGENT', 'CLASSIFICATION'")} AS classification_agent_response,
        {AgentResponseByTypeSql(agentTableName, "'OCR', 'OCR_AGENT'")} AS ocr_agent_response,
        {AgentResponseByTypeSql(agentTableName, "'FTP_AGENT', 'FTP'")} AS ftp_agent_response
    FROM workflow.{agentTableName} a
    WHERE a.is_deleted = false
      AND a.process_id = {TryCastInstanceId("m.workflow_instance_id")}
    ORDER BY a.created_at DESC, a.id DESC
    LIMIT 1
) av ON true";
    }

    private static string AgentResponseByTypeSql(string agentTableName, string typeList) => $"""
        (
            SELECT q.agent_response
            FROM workflow.{agentTableName} q
            WHERE q.is_deleted = false
              AND q.process_id = {TryCastInstanceId("m.workflow_instance_id")}
              AND UPPER(TRIM(COALESCE(q.type, ''))) IN ({typeList})
            ORDER BY q.created_at DESC, q.id DESC
            LIMIT 1
        )
        """;
}
