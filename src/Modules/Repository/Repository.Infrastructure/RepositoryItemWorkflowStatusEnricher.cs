using Npgsql;
using SaaSApp.Repository.Application.Contracts;

namespace SaaSApp.Repository.Infrastructure;

/// <summary>
/// For repository items linked to a workflow ticket (WorkflowInstanceId),
/// replaces Status with the current workflow stage / ticket status for the UI Status column.
/// </summary>
internal static class RepositoryItemWorkflowStatusEnricher
{
    // Matches WorkflowInstanceStatus enum values without taking a Workflow project reference.
    private const int InstancePending = 0;
    private const int InstanceRunning = 1;
    private const int InstancePaused = 2;
    private const int InstanceCompleted = 3;
    private const int InstanceFailed = 4;
    private const int InstanceCancelled = 5;

    public static async Task EnrichListAsync(
        NpgsqlConnection connection,
        IList<RepositoryItemListDto> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
            return;

        var instanceIds = items
            .Where(i => i.WorkflowInstanceId is Guid id && id != Guid.Empty)
            .Select(i => i.WorkflowInstanceId!.Value)
            .Distinct()
            .ToList();

        if (instanceIds.Count == 0)
            return;

        var statusByInstance = await ResolveStatusesAsync(connection, instanceIds, cancellationToken);
        if (statusByInstance.Count == 0)
            return;

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.WorkflowInstanceId is not Guid wfId
                || !statusByInstance.TryGetValue(wfId, out var workflowStatus)
                || string.IsNullOrWhiteSpace(workflowStatus))
            {
                continue;
            }

            items[i] = item with { Status = workflowStatus };
        }
    }

    public static async Task<string?> ResolveStatusAsync(
        NpgsqlConnection connection,
        Guid workflowInstanceId,
        CancellationToken cancellationToken)
    {
        var map = await ResolveStatusesAsync(connection, [workflowInstanceId], cancellationToken);
        return map.TryGetValue(workflowInstanceId, out var status) ? status : null;
    }

    /// <summary>
    /// Instance IDs whose <em>display</em> status (same as list enrichment) matches any of
    /// <paramref name="displayStatuses"/> (case-insensitive).
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> FindInstanceIdsWithDisplayStatusAsync(
        NpgsqlConnection connection,
        IReadOnlyList<Guid> candidateInstanceIds,
        IReadOnlyList<string> displayStatuses,
        CancellationToken cancellationToken)
    {
        var wanted = new HashSet<string>(
            displayStatuses
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim()),
            StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0 || candidateInstanceIds.Count == 0)
            return Array.Empty<Guid>();

        var statusByInstance = await ResolveStatusesAsync(connection, candidateInstanceIds, cancellationToken);
        if (statusByInstance.Count == 0)
            return Array.Empty<Guid>();

        return statusByInstance
            .Where(kv => wanted.Contains(kv.Value))
            .Select(kv => kv.Key)
            .Distinct()
            .ToList();
    }

    /// <summary>Display Status for each instance (same values as list enrichment).</summary>
    public static Task<Dictionary<Guid, string>> GetDisplayStatusMapAsync(
        NpgsqlConnection connection,
        IReadOnlyList<Guid> candidateInstanceIds,
        CancellationToken cancellationToken) =>
        ResolveStatusesAsync(connection, candidateInstanceIds, cancellationToken);

    private static async Task<Dictionary<Guid, string>> ResolveStatusesAsync(
        NpgsqlConnection connection,
        IReadOnlyList<Guid> instanceIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, string>();
        if (instanceIds.Count == 0)
            return result;

        if (!await TableExistsAsync(connection, "WorkflowInstanceLookup", cancellationToken))
            return result;

        var lookups = await LoadLookupsAsync(connection, instanceIds, cancellationToken);
        if (lookups.Count == 0)
            return result;

        foreach (var group in lookups.GroupBy(x => x.WorkflowId))
        {
            var suffix = group.Key.ToString("N")[..8];
            // transaction_{suffix} is created by the dynamic DDL engine (WorkflowTableCreator.cs,
            // ported earlier in Phase 4) with snake_case unquoted columns.
            var transactionTable = $"transaction_{suffix}";
            if (!await TableExistsAsync(connection, transactionTable, cancellationToken, schema: "workflow"))
            {
                foreach (var row in group)
                    result[row.InstanceId] = MapInstanceStatus(row.Status);
                continue;
            }

            var groupIds = group.Select(x => x.InstanceId).ToList();
            var stageByInstance = await LoadCurrentStagesAsync(
                connection,
                transactionTable,
                groupIds,
                cancellationToken);

            foreach (var row in group)
            {
                if (stageByInstance.TryGetValue(row.InstanceId, out var stage)
                    && !string.IsNullOrWhiteSpace(stage))
                {
                    result[row.InstanceId] = stage.Trim();
                }
                else
                {
                    result[row.InstanceId] = MapInstanceStatus(row.Status);
                }
            }
        }

        return result;
    }

    private static async Task<List<(Guid InstanceId, Guid WorkflowId, int Status)>> LoadLookupsAsync(
        NpgsqlConnection connection,
        IReadOnlyList<Guid> instanceIds,
        CancellationToken cancellationToken)
    {
        var list = new List<(Guid, Guid, int)>();
        const string sql = """
            SELECT "InstanceId", "WorkflowId", "Status"
            FROM workflow."WorkflowInstanceLookup"
            WHERE "InstanceId" = ANY(@Ids);
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Ids", instanceIds.ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            list.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2)));

        return list;
    }

    private static async Task<Dictionary<Guid, string>> LoadCurrentStagesAsync(
        NpgsqlConnection connection,
        string transactionTableName,
        IReadOnlyList<Guid> instanceIds,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<Guid, string>();
        var table = $"workflow.{transactionTableName}";

        var sql = $"""
            WITH ranked AS (
                SELECT
                    workflow_instance_id,
                    stage_name,
                    review,
                    action_status,
                    ROW_NUMBER() OVER (
                        PARTITION BY workflow_instance_id
                        ORDER BY
                            CASE WHEN action_status = 0 AND UPPER(TRIM(COALESCE(stage_type, ''))) <> 'END' THEN 0 ELSE 1 END,
                            id DESC
                    ) AS rn
                FROM {table}
                WHERE is_deleted = false
                  AND workflow_instance_id = ANY(@Ids)
            )
            SELECT
                workflow_instance_id,
                CASE
                    WHEN action_status = 0 AND NULLIF(TRIM(stage_name), '') IS NOT NULL THEN stage_name
                    WHEN NULLIF(TRIM(review), '') IS NOT NULL
                         AND UPPER(TRIM(review)) <> 'END' THEN review
                    WHEN NULLIF(TRIM(stage_name), '') IS NOT NULL THEN stage_name
                    ELSE NULL
                END AS display_status
            FROM ranked
            WHERE rn = 1;
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Ids", instanceIds.ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(1))
                continue;
            var status = reader.GetString(1);
            if (!string.IsNullOrWhiteSpace(status))
                map[reader.GetGuid(0)] = status.Trim();
        }

        return map;
    }

    private static string MapInstanceStatus(int status) =>
        status switch
        {
            InstancePending => "Pending",
            InstanceRunning => "Pending Approval",
            InstancePaused => "Paused",
            InstanceCompleted => "Approved",
            InstanceFailed => "Failed",
            InstanceCancelled => "Cancelled",
            _ => "Active"
        };

    private static async Task<bool> TableExistsAsync(
        NpgsqlConnection connection,
        string tableName,
        CancellationToken cancellationToken,
        string schema = "workflow")
    {
        const string sql = """
            SELECT 1 FROM information_schema.tables
            WHERE table_name = @Name AND table_schema = @Schema;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Name", tableName);
        cmd.Parameters.AddWithValue("@Schema", schema);
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }
}
