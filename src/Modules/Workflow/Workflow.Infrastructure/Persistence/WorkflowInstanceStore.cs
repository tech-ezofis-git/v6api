using System.Reflection;
using Npgsql;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Domain.Entities;
using SaaSApp.Workflow.Domain.Enums;

namespace SaaSApp.Workflow.Infrastructure.Persistence;

/// <summary>
/// Persists workflow instances to per-workflow tables (workflow_instances_{suffix}, workflow_step_instances_{suffix}).
/// Uses workflow."WorkflowInstanceLookup" for cross-workflow queries.
/// </summary>
public sealed class WorkflowInstanceStore : IWorkflowInstanceStore
{
    private readonly ITenantContext _tenantContext;

    public WorkflowInstanceStore(ITenantContext tenantContext)
    {
        _tenantContext = tenantContext;
    }

    private static string GetSuffix(Guid workflowId) => workflowId.ToString("N")[..8];
    private static string InstancesTable(Guid workflowId) => $"workflow.workflow_instances_{GetSuffix(workflowId)}";
    private static string StepInstancesTable(Guid workflowId) => $"workflow.workflow_step_instances_{GetSuffix(workflowId)}";
    private static string InstanceSlasTable(Guid workflowId) => $"workflow.workflow_instance_slas_{GetSuffix(workflowId)}";

    public async Task AddAsync(WorkflowInstance instance, CancellationToken cancellationToken = default)
    {
        var connStr = _tenantContext.ConnectionString ?? throw new InvalidOperationException("Connection string required.");
        var suffix = GetSuffix(instance.WorkflowId);
        var instancesTable = InstancesTable(instance.WorkflowId);
        var stepInstancesTable = StepInstancesTable(instance.WorkflowId);

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(cancellationToken);

        await EnsureStepInstanceColumnsAsync(conn, suffix, cancellationToken);

        // Insert instance
        var instanceSql = $@"
            INSERT INTO {instancesTable} (
                id, tenant_id, workflow_id, workflow_name, workflow_version, status, current_step_instance_id,
                created_at_utc, started_at_utc, completed_at_utc, started_by, context, error_message,
                reference_number, customer_name, customer_email, customer_phone, department, category, priority,
                tags, custom_fields_json, assigned_to_user_id, assigned_to_group_id, last_activity_at_utc,
                view_count, is_archived, archived_at_utc, source_type, source_id, last_viewed_at_utc, last_viewed_by)
            VALUES (
                @Id, @TenantId, @WorkflowId, @WorkflowName, @WorkflowVersion, @Status, @CurrentStepInstanceId,
                @CreatedAtUtc, @StartedAtUtc, @CompletedAtUtc, @StartedBy, @Context, @ErrorMessage,
                @ReferenceNumber, @CustomerName, @CustomerEmail, @CustomerPhone, @Department, @Category, @Priority,
                @Tags, @CustomFieldsJson, @AssignedToUserId, @AssignedToGroupId, @LastActivityAtUtc,
                @ViewCount, @IsArchived, @ArchivedAtUtc, @SourceType, @SourceId, @LastViewedAtUtc, @LastViewedBy)";

        await using (var cmd = new NpgsqlCommand(instanceSql, conn))
        {
            AddInstanceParams(cmd, instance);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Insert step instances
        foreach (var step in instance.StepInstances)
        {
            var stepSql = $@"
                INSERT INTO {stepInstancesTable} (
                    id, workflow_instance_id, workflow_step_id, step_name, step_type, ""order"", status,
                    assigned_to_user_id, assigned_to_role, created_at_utc, started_at_utc, completed_at_utc,
                    completed_by, result, error_message, activity_id, stage_type)
                VALUES (
                    @Id, @WorkflowInstanceId, @WorkflowStepId, @StepName, @StepType, @Order, @Status,
                    @AssignedToUserId, @AssignedToRole, @CreatedAtUtc, @StartedAtUtc, @CompletedAtUtc,
                    @CompletedBy, @Result, @ErrorMessage, @ActivityId, @StageType)";
            await using var stepCmd = new NpgsqlCommand(stepSql, conn);
            AddStepInstanceParams(stepCmd, step, instance.Id);
            await stepCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Insert SLA if present
        if (instance.Sla != null)
        {
            var slaTable = InstanceSlasTable(instance.WorkflowId);
            var slaSql = $@"
                INSERT INTO {slaTable} (
                    id, workflow_instance_id, priority, response_deadline, resolution_deadline, escalation_deadline,
                    response_achieved_at, resolution_achieved_at, response_status, resolution_status, is_escalated, escalated_at, created_at_utc)
                VALUES (
                    @Id, @WorkflowInstanceId, @Priority, @ResponseDeadline, @ResolutionDeadline, @EscalationDeadline,
                    @ResponseAchievedAt, @ResolutionAchievedAt, @ResponseStatus, @ResolutionStatus, @IsEscalated, @EscalatedAt, @CreatedAtUtc)";
            await using var slaCmd = new NpgsqlCommand(slaSql, conn);
            AddSlaParams(slaCmd, instance.Sla);
            await slaCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Insert lookup row
        const string lookupSql = """
            INSERT INTO workflow."WorkflowInstanceLookup" (
                "InstanceId", "WorkflowId", "TenantId", "WorkflowName", "Status", "AssignedToUserId", "StartedBy",
                "CreatedAtUtc", "LastActivityAtUtc", "CompletedAtUtc", "IsArchived", "Priority", "CurrentStepInstanceId",
                "SlaPriority", "ResponseStatus", "ResolutionStatus", "ResponseDeadline", "ResolutionDeadline", "IsEscalated")
            VALUES (
                @InstanceId, @WorkflowId, @TenantId, @WorkflowName, @Status, @AssignedToUserId, @StartedBy,
                @CreatedAtUtc, @LastActivityAtUtc, @CompletedAtUtc, @IsArchived, @Priority, @CurrentStepInstanceId,
                @SlaPriority, @ResponseStatus, @ResolutionStatus, @ResponseDeadline, @ResolutionDeadline, @IsEscalated)
            """;
        await using var lookupCmd = new NpgsqlCommand(lookupSql, conn);
        AddLookupParams(lookupCmd, instance);
        await lookupCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<WorkflowInstance?> GetByIdAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var connStr = _tenantContext.ConnectionString ?? throw new InvalidOperationException("Connection string required.");

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(cancellationToken);

        // Lookup workflowId
        const string lookupSql = """SELECT "WorkflowId" FROM workflow."WorkflowInstanceLookup" WHERE "InstanceId" = @Id""";
        await using var lookupCmd = new NpgsqlCommand(lookupSql, conn);
        lookupCmd.Parameters.AddWithValue("@Id", instanceId);
        var workflowIdObj = await lookupCmd.ExecuteScalarAsync(cancellationToken);
        if (workflowIdObj == null || workflowIdObj == DBNull.Value)
            return null;

        var workflowId = (Guid)workflowIdObj;
        var instancesTable = InstancesTable(workflowId);
        var stepInstancesTable = StepInstancesTable(workflowId);
        var slaTable = InstanceSlasTable(workflowId);

        // Check if per-workflow tables exist (workflow may have been published)
        if (!await TableExistsAsync(conn, $"workflow_instances_{GetSuffix(workflowId)}", cancellationToken))
            return null;

        // Load instance
        var instanceSql = $"SELECT * FROM {instancesTable} WHERE id = @Id";
        await using var instanceCmd = new NpgsqlCommand(instanceSql, conn);
        instanceCmd.Parameters.AddWithValue("@Id", instanceId);

        WorkflowInstance? instance = null;
        await using (var reader = await instanceCmd.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
                instance = ReadWorkflowInstance(reader, workflowId);
        }

        if (instance == null)
            return null;

        // Load step instances
        var stepSql = $"""SELECT * FROM {stepInstancesTable} WHERE workflow_instance_id = @InstanceId ORDER BY "order" """;
        await using var stepCmd = new NpgsqlCommand(stepSql, conn);
        stepCmd.Parameters.AddWithValue("@InstanceId", instanceId);
        await using (var stepReader = await stepCmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await stepReader.ReadAsync(cancellationToken))
            {
                var step = ReadWorkflowStepInstance(stepReader, instanceId);
                instance.AddStepInstance(step);
            }
        }

        // Load SLA (table may not exist for workflows without SLA)
        try
        {
            var slaSql = $"SELECT * FROM {slaTable} WHERE workflow_instance_id = @InstanceId";
            await using var slaCmd = new NpgsqlCommand(slaSql, conn);
            slaCmd.Parameters.AddWithValue("@InstanceId", instanceId);
            await using var slaReader = await slaCmd.ExecuteReaderAsync(cancellationToken);
            if (await slaReader.ReadAsync(cancellationToken))
            {
                var sla = ReadWorkflowInstanceSla(slaReader, instanceId);
                instance.SetSla(sla);
            }
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable) { /* Table may not exist */ }

        return instance;
    }

    public async Task<IReadOnlyList<WorkflowInstance>> ListByWorkflowIdAsync(Guid workflowId, CancellationToken cancellationToken = default)
    {
        var connStr = _tenantContext.ConnectionString ?? throw new InvalidOperationException("Connection string required.");
        var instancesTable = InstancesTable(workflowId);
        var stepInstancesTable = StepInstancesTable(workflowId);

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(cancellationToken);

        if (!await TableExistsAsync(conn, $"workflow_instances_{GetSuffix(workflowId)}", cancellationToken))
            return Array.Empty<WorkflowInstance>();

        var sql = $"SELECT * FROM {instancesTable} WHERE workflow_id = @WorkflowId ORDER BY created_at_utc DESC";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@WorkflowId", workflowId);

        var list = new List<WorkflowInstance>();
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var instance = ReadWorkflowInstance(reader, workflowId);
                list.Add(instance);
            }
        }

        foreach (var instance in list)
        {
            var stepSql = $"""SELECT * FROM {stepInstancesTable} WHERE workflow_instance_id = @InstanceId ORDER BY "order" """;
            await using var stepCmd = new NpgsqlCommand(stepSql, conn);
            stepCmd.Parameters.AddWithValue("@InstanceId", instance.Id);
            await using var stepReader = await stepCmd.ExecuteReaderAsync(cancellationToken);
            while (await stepReader.ReadAsync(cancellationToken))
            {
                var step = ReadWorkflowStepInstance(stepReader, instance.Id);
                instance.AddStepInstance(step);
            }
        }

        return list;
    }

    public async Task UpdateAsync(WorkflowInstance instance, CancellationToken cancellationToken = default)
    {
        var connStr = _tenantContext.ConnectionString ?? throw new InvalidOperationException("Connection string required.");
        var instancesTable = InstancesTable(instance.WorkflowId);

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(cancellationToken);

        var sql = $@"
            UPDATE {instancesTable} SET
                status = @Status, current_step_instance_id = @CurrentStepInstanceId,
                started_at_utc = @StartedAtUtc, completed_at_utc = @CompletedAtUtc,
                error_message = @ErrorMessage, assigned_to_user_id = @AssignedToUserId,
                last_activity_at_utc = @LastActivityAtUtc, view_count = @ViewCount,
                is_archived = @IsArchived, archived_at_utc = @ArchivedAtUtc
            WHERE id = @Id";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Id", instance.Id);
        cmd.Parameters.AddWithValue("@Status", (int)instance.Status);
        cmd.Parameters.AddWithValue("@CurrentStepInstanceId", (object?)instance.CurrentStepInstanceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@StartedAtUtc", (object?)instance.StartedAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CompletedAtUtc", (object?)instance.CompletedAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ErrorMessage", (object?)instance.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@AssignedToUserId", (object?)instance.AssignedToUserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@LastActivityAtUtc", (object?)instance.LastActivityAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ViewCount", instance.ViewCount);
        cmd.Parameters.AddWithValue("@IsArchived", instance.IsArchived);
        cmd.Parameters.AddWithValue("@ArchivedAtUtc", (object?)instance.ArchivedAtUtc ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        // Update step instances
        var stepInstancesTable = StepInstancesTable(instance.WorkflowId);
        foreach (var step in instance.StepInstances)
        {
            var stepSql = $@"
                UPDATE {stepInstancesTable} SET
                    status = @Status, started_at_utc = @StartedAtUtc, completed_at_utc = @CompletedAtUtc,
                    completed_by = @CompletedBy, result = @Result, error_message = @ErrorMessage
                WHERE id = @Id";
            await using var stepCmd = new NpgsqlCommand(stepSql, conn);
            stepCmd.Parameters.AddWithValue("@Id", step.Id);
            stepCmd.Parameters.AddWithValue("@Status", (int)step.Status);
            stepCmd.Parameters.AddWithValue("@StartedAtUtc", (object?)step.StartedAtUtc ?? DBNull.Value);
            stepCmd.Parameters.AddWithValue("@CompletedAtUtc", (object?)step.CompletedAtUtc ?? DBNull.Value);
            stepCmd.Parameters.AddWithValue("@CompletedBy", (object?)step.CompletedBy ?? DBNull.Value);
            stepCmd.Parameters.AddWithValue("@Result", (object?)step.Result ?? DBNull.Value);
            stepCmd.Parameters.AddWithValue("@ErrorMessage", (object?)step.ErrorMessage ?? DBNull.Value);
            await stepCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Update SLA if present
        if (instance.Sla != null)
        {
            try
            {
                var slaTable = InstanceSlasTable(instance.WorkflowId);
                var slaSql = $@"
                    UPDATE {slaTable} SET
                        response_achieved_at = @ResponseAchievedAt, resolution_achieved_at = @ResolutionAchievedAt,
                        response_status = @ResponseStatus, resolution_status = @ResolutionStatus,
                        is_escalated = @IsEscalated, escalated_at = @EscalatedAt
                    WHERE workflow_instance_id = @WorkflowInstanceId";
                await using var slaCmd = new NpgsqlCommand(slaSql, conn);
                slaCmd.Parameters.AddWithValue("@WorkflowInstanceId", instance.Id);
                slaCmd.Parameters.AddWithValue("@ResponseAchievedAt", (object?)instance.Sla.ResponseAchievedAt ?? DBNull.Value);
                slaCmd.Parameters.AddWithValue("@ResolutionAchievedAt", (object?)instance.Sla.ResolutionAchievedAt ?? DBNull.Value);
                slaCmd.Parameters.AddWithValue("@ResponseStatus", (int)instance.Sla.ResponseStatus);
                slaCmd.Parameters.AddWithValue("@ResolutionStatus", (int)instance.Sla.ResolutionStatus);
                slaCmd.Parameters.AddWithValue("@IsEscalated", instance.Sla.IsEscalated);
                slaCmd.Parameters.AddWithValue("@EscalatedAt", (object?)instance.Sla.EscalatedAt ?? DBNull.Value);
                await slaCmd.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable) { /* Table may not exist */ }
        }

        // Update lookup
        const string lookupSql = """
            UPDATE workflow."WorkflowInstanceLookup" SET
                "Status" = @Status, "AssignedToUserId" = @AssignedToUserId, "LastActivityAtUtc" = @LastActivityAtUtc,
                "CompletedAtUtc" = @CompletedAtUtc, "IsArchived" = @IsArchived, "CurrentStepInstanceId" = @CurrentStepInstanceId,
                "SlaPriority" = @SlaPriority, "ResponseStatus" = @ResponseStatus, "ResolutionStatus" = @ResolutionStatus,
                "ResponseDeadline" = @ResponseDeadline, "ResolutionDeadline" = @ResolutionDeadline, "IsEscalated" = @IsEscalated
            WHERE "InstanceId" = @InstanceId
            """;
        await using var lookupCmd = new NpgsqlCommand(lookupSql, conn);
        lookupCmd.Parameters.AddWithValue("@InstanceId", instance.Id);
        lookupCmd.Parameters.AddWithValue("@Status", (int)instance.Status);
        lookupCmd.Parameters.AddWithValue("@AssignedToUserId", (object?)instance.AssignedToUserId ?? DBNull.Value);
        lookupCmd.Parameters.AddWithValue("@LastActivityAtUtc", (object?)instance.LastActivityAtUtc ?? DBNull.Value);
        lookupCmd.Parameters.AddWithValue("@CompletedAtUtc", (object?)instance.CompletedAtUtc ?? DBNull.Value);
        lookupCmd.Parameters.AddWithValue("@IsArchived", instance.IsArchived);
        lookupCmd.Parameters.AddWithValue("@CurrentStepInstanceId", (object?)instance.CurrentStepInstanceId ?? DBNull.Value);
        AddLookupSlaParams(lookupCmd, instance);
        await lookupCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<(List<WorkflowInstance> Items, int TotalCount)> GetMyInboxAsync(Guid userId, int pageNumber, int pageSize, Guid? workflowId = null, CancellationToken cancellationToken = default)
    {
        var whereClause = "\"AssignedToUserId\" = @UserId AND \"Status\" IN (0, 1) AND \"IsArchived\" = false";
        var parameters = new List<NpgsqlParameter> { new NpgsqlParameter("@UserId", userId) };
        if (workflowId.HasValue)
        {
            whereClause += " AND \"WorkflowId\" = @WorkflowId";
            parameters.Add(new NpgsqlParameter("@WorkflowId", workflowId.Value));
        }
        return await GetInstancesFromLookupAsync(
            whereClause,
            parameters.ToArray(),
            "\"Priority\" DESC, COALESCE(\"LastActivityAtUtc\", \"CreatedAtUtc\") DESC",
            pageNumber, pageSize, cancellationToken);
    }

    public async Task<IReadOnlyList<WorkflowInboxCount>> GetWorkflowWiseInboxCountsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var connStr = _tenantContext.ConnectionString ?? throw new InvalidOperationException("Connection string required.");
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(cancellationToken);

        const string sql = """
            SELECT "WorkflowId", "WorkflowName", COUNT(*) AS "InboxCount"
            FROM workflow."WorkflowInstanceLookup"
            WHERE "AssignedToUserId" = @UserId AND "Status" IN (0, 1) AND "IsArchived" = false
            GROUP BY "WorkflowId", "WorkflowName"
            ORDER BY "InboxCount" DESC, "WorkflowName"
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@UserId", userId);
        var list = new List<WorkflowInboxCount>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new WorkflowInboxCount(
                reader.GetGuid(0),
                reader.GetString(1),
                Convert.ToInt32(reader.GetInt64(2))));
        }
        return list;
    }

    public async Task<(List<WorkflowInstance> Items, int TotalCount)> GetMySentAsync(Guid userId, int pageNumber, int pageSize, CancellationToken cancellationToken = default)
    {
        return await GetInstancesFromLookupAsync(
            "\"StartedBy\" = @UserId AND \"IsArchived\" = false",
            new[] { new NpgsqlParameter("@UserId", userId) },
            "\"CreatedAtUtc\" DESC",
            pageNumber, pageSize, cancellationToken);
    }

    public async Task<(List<WorkflowInstance> Items, int TotalCount)> GetMyCompletedAsync(Guid userId, int pageNumber, int pageSize, CancellationToken cancellationToken = default)
    {
        return await GetInstancesFromLookupAsync(
            "(\"StartedBy\" = @UserId OR \"AssignedToUserId\" = @UserId) AND \"Status\" = 3",
            new[] { new NpgsqlParameter("@UserId", userId) },
            "\"LastActivityAtUtc\" DESC",
            pageNumber, pageSize, cancellationToken);
    }

    public async Task<WorkflowCounts> GetWorkflowCountsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var connStr = _tenantContext.ConnectionString ?? throw new InvalidOperationException("Connection string required.");
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(cancellationToken);

        const string sql = """
            SELECT
                (SELECT COUNT(*) FROM workflow."WorkflowInstanceLookup" WHERE "AssignedToUserId" = @UserId AND "Status" IN (0, 1) AND "IsArchived" = false),
                (SELECT COUNT(*) FROM workflow."WorkflowInstanceLookup" WHERE "StartedBy" = @UserId AND "IsArchived" = false),
                (SELECT COUNT(*) FROM workflow."WorkflowInstanceLookup" WHERE ("StartedBy" = @UserId OR "AssignedToUserId" = @UserId) AND "Status" = 3),
                (SELECT COUNT(*) FROM workflow."WorkflowInstanceLookup" WHERE "Status" NOT IN (3, 5) AND "IsArchived" = false)
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@UserId", userId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new WorkflowCounts(
            Convert.ToInt32(reader.GetInt64(0)),
            Convert.ToInt32(reader.GetInt64(1)),
            Convert.ToInt32(reader.GetInt64(2)),
            Convert.ToInt32(reader.GetInt64(3)));
    }

    public async Task<IReadOnlyList<SlaBreachInfo>> ListSlaBreachesAsync(CancellationToken cancellationToken = default)
    {
        var connStr = _tenantContext.ConnectionString ?? throw new InvalidOperationException("Connection string required.");
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(cancellationToken);

        const string sql = """
            SELECT "InstanceId", "WorkflowId", "WorkflowName", "Status", "SlaPriority", "ResponseStatus", "ResolutionStatus",
                   "ResponseDeadline", "ResolutionDeadline", "IsEscalated", "CreatedAtUtc"
            FROM workflow."WorkflowInstanceLookup"
            WHERE ("ResponseStatus" IN (1, 2) OR "ResolutionStatus" IN (1, 2))
              AND "SlaPriority" IS NOT NULL
            ORDER BY "SlaPriority" DESC, "ResolutionDeadline"
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        var list = new List<SlaBreachInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new SlaBreachInfo(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
                (WorkflowInstanceStatus)reader.GetInt32(3), (SlaPriority)reader.GetInt32(4),
                (SlaStatus)reader.GetInt32(5), (SlaStatus)reader.GetInt32(6),
                reader.GetDateTime(7), reader.GetDateTime(8), reader.GetBoolean(9), reader.GetDateTime(10)));
        }
        return list;
    }

    private async Task<(List<WorkflowInstance> Items, int TotalCount)> GetInstancesFromLookupAsync(
        string whereClause, NpgsqlParameter[] parameters, string orderBy, int pageNumber, int pageSize, CancellationToken cancellationToken)
    {
        var connStr = _tenantContext.ConnectionString ?? throw new InvalidOperationException("Connection string required.");
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(cancellationToken);

        var countSql = $"""SELECT COUNT(*) FROM workflow."WorkflowInstanceLookup" WHERE {whereClause}""";
        await using var countCmd = new NpgsqlCommand(countSql, conn);
        foreach (var p in parameters)
            countCmd.Parameters.Add(new NpgsqlParameter(p.ParameterName, p.Value));
        var totalCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync(cancellationToken) ?? 0);

        var offset = (pageNumber - 1) * pageSize;
        var dataSql = $"""
            SELECT "InstanceId", "WorkflowId" FROM workflow."WorkflowInstanceLookup"
            WHERE {whereClause}
            ORDER BY {orderBy}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
            """;
        await using var dataCmd = new NpgsqlCommand(dataSql, conn);
        foreach (var p in parameters)
            dataCmd.Parameters.Add(new NpgsqlParameter(p.ParameterName, p.Value));
        dataCmd.Parameters.AddWithValue("@Offset", offset);
        dataCmd.Parameters.AddWithValue("@PageSize", pageSize);

        var items = new List<WorkflowInstance>();
        var pairs = new List<(Guid InstanceId, Guid WorkflowId)>();
        await using (var reader = await dataCmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                pairs.Add((reader.GetGuid(0), reader.GetGuid(1)));
        }

        foreach (var group in pairs.GroupBy(p => p.WorkflowId))
        {
            var loaded = await LoadInstancesBatchAsync(
                conn,
                group.Key,
                group.Select(p => p.InstanceId).ToList(),
                cancellationToken);
            items.AddRange(loaded);
        }

        var order = pairs.Select((p, i) => (p.InstanceId, i)).ToDictionary(x => x.InstanceId, x => x.i);
        items.Sort((a, b) => order.GetValueOrDefault(a.Id, int.MaxValue).CompareTo(order.GetValueOrDefault(b.Id, int.MaxValue)));
        return (items, totalCount);
    }

    private async Task<List<WorkflowInstance>> LoadInstancesBatchAsync(
        NpgsqlConnection conn,
        Guid workflowId,
        IReadOnlyList<Guid> instanceIds,
        CancellationToken cancellationToken)
    {
        if (instanceIds.Count == 0)
            return [];

        var instancesTable = InstancesTable(workflowId);
        var stepInstancesTable = StepInstancesTable(workflowId);
        var slaTable = InstanceSlasTable(workflowId);

        if (!await TableExistsAsync(conn, $"workflow_instances_{GetSuffix(workflowId)}", cancellationToken))
            return [];

        var idParams = new List<string>(instanceIds.Count);
        var instanceSql = new System.Text.StringBuilder($"SELECT * FROM {instancesTable} WHERE id IN (");
        for (var i = 0; i < instanceIds.Count; i++)
        {
            var param = $"@Id{i}";
            idParams.Add(param);
            if (i > 0)
                instanceSql.Append(", ");
            instanceSql.Append(param);
        }
        instanceSql.Append(')');

        var instances = new Dictionary<Guid, WorkflowInstance>(instanceIds.Count);
        await using (var instanceCmd = new NpgsqlCommand(instanceSql.ToString(), conn))
        {
            for (var i = 0; i < instanceIds.Count; i++)
                instanceCmd.Parameters.AddWithValue($"@Id{i}", instanceIds[i]);

            await using var reader = await instanceCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var instance = ReadWorkflowInstance(reader, workflowId);
                instances[instance.Id] = instance;
            }
        }

        if (instances.Count == 0)
            return [];

        var stepSql = new System.Text.StringBuilder($"SELECT * FROM {stepInstancesTable} WHERE workflow_instance_id IN (");
        for (var i = 0; i < instanceIds.Count; i++)
        {
            if (i > 0)
                stepSql.Append(", ");
            stepSql.Append(idParams[i]);
        }
        stepSql.Append(") ORDER BY \"order\"");

        await using (var stepCmd = new NpgsqlCommand(stepSql.ToString(), conn))
        {
            for (var i = 0; i < instanceIds.Count; i++)
                stepCmd.Parameters.AddWithValue($"@Id{i}", instanceIds[i]);

            await using var stepReader = await stepCmd.ExecuteReaderAsync(cancellationToken);
            while (await stepReader.ReadAsync(cancellationToken))
            {
                var instanceId = stepReader.GetGuid(stepReader.GetOrdinal("workflow_instance_id"));
                if (instances.TryGetValue(instanceId, out var instance))
                {
                    var step = ReadWorkflowStepInstance(stepReader, instanceId);
                    instance.AddStepInstance(step);
                }
            }
        }

        try
        {
            var slaSql = new System.Text.StringBuilder($"SELECT * FROM {slaTable} WHERE workflow_instance_id IN (");
            for (var i = 0; i < instanceIds.Count; i++)
            {
                if (i > 0)
                    slaSql.Append(", ");
                slaSql.Append(idParams[i]);
            }
            slaSql.Append(')');

            await using var slaCmd = new NpgsqlCommand(slaSql.ToString(), conn);
            for (var i = 0; i < instanceIds.Count; i++)
                slaCmd.Parameters.AddWithValue($"@Id{i}", instanceIds[i]);

            await using var slaReader = await slaCmd.ExecuteReaderAsync(cancellationToken);
            while (await slaReader.ReadAsync(cancellationToken))
            {
                var instanceId = slaReader.GetGuid(slaReader.GetOrdinal("workflow_instance_id"));
                if (instances.TryGetValue(instanceId, out var instance))
                {
                    var sla = ReadWorkflowInstanceSla(slaReader, instanceId);
                    instance.SetSla(sla);
                }
            }
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable) { /* SLA table may not exist */ }

        return instanceIds
            .Where(instances.ContainsKey)
            .Select(id => instances[id])
            .ToList();
    }

    private static void AddInstanceParams(NpgsqlCommand cmd, WorkflowInstance i)
    {
        cmd.Parameters.AddWithValue("@Id", i.Id);
        cmd.Parameters.AddWithValue("@TenantId", i.TenantId);
        cmd.Parameters.AddWithValue("@WorkflowId", i.WorkflowId);
        cmd.Parameters.AddWithValue("@WorkflowName", i.WorkflowName);
        cmd.Parameters.AddWithValue("@WorkflowVersion", i.WorkflowVersion);
        cmd.Parameters.AddWithValue("@Status", (int)i.Status);
        cmd.Parameters.AddWithValue("@CurrentStepInstanceId", (object?)i.CurrentStepInstanceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedAtUtc", i.CreatedAtUtc);
        cmd.Parameters.AddWithValue("@StartedAtUtc", (object?)i.StartedAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CompletedAtUtc", (object?)i.CompletedAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@StartedBy", i.StartedBy);
        cmd.Parameters.AddWithValue("@Context", (object?)i.Context ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ErrorMessage", (object?)i.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ReferenceNumber", (object?)i.ReferenceNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CustomerName", (object?)i.CustomerName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CustomerEmail", (object?)i.CustomerEmail ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CustomerPhone", (object?)i.CustomerPhone ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Department", (object?)i.Department ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Category", (object?)i.Category ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Priority", i.Priority);
        cmd.Parameters.AddWithValue("@Tags", (object?)i.Tags ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CustomFieldsJson", (object?)i.CustomFieldsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@AssignedToUserId", (object?)i.AssignedToUserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@AssignedToGroupId", (object?)i.AssignedToGroupId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@LastActivityAtUtc", (object?)i.LastActivityAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ViewCount", i.ViewCount);
        cmd.Parameters.AddWithValue("@IsArchived", i.IsArchived);
        cmd.Parameters.AddWithValue("@ArchivedAtUtc", (object?)i.ArchivedAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@SourceType", (object?)i.SourceType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@SourceId", (object?)i.SourceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@LastViewedAtUtc", DBNull.Value);
        cmd.Parameters.AddWithValue("@LastViewedBy", DBNull.Value);
    }

    private static void AddStepInstanceParams(NpgsqlCommand cmd, WorkflowStepInstance s, Guid instanceId)
    {
        cmd.Parameters.AddWithValue("@Id", s.Id);
        cmd.Parameters.AddWithValue("@WorkflowInstanceId", instanceId);
        cmd.Parameters.AddWithValue("@WorkflowStepId", s.WorkflowStepId);
        cmd.Parameters.AddWithValue("@StepName", s.StepName);
        cmd.Parameters.AddWithValue("@StepType", (int)s.StepType);
        cmd.Parameters.AddWithValue("@Order", s.Order);
        cmd.Parameters.AddWithValue("@Status", (int)s.Status);
        cmd.Parameters.AddWithValue("@AssignedToUserId", (object?)s.AssignedToUserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@AssignedToRole", (object?)s.AssignedToRole ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedAtUtc", s.CreatedAtUtc);
        cmd.Parameters.AddWithValue("@StartedAtUtc", (object?)s.StartedAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CompletedAtUtc", (object?)s.CompletedAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CompletedBy", (object?)s.CompletedBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Result", (object?)s.Result ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ErrorMessage", (object?)s.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ActivityId", (object?)s.ActivityId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@StageType", (object?)s.StageType ?? DBNull.Value);
    }

    private static void AddSlaParams(NpgsqlCommand cmd, WorkflowInstanceSla s)
    {
        cmd.Parameters.AddWithValue("@Id", s.Id);
        cmd.Parameters.AddWithValue("@WorkflowInstanceId", s.WorkflowInstanceId);
        cmd.Parameters.AddWithValue("@Priority", (int)s.Priority);
        cmd.Parameters.AddWithValue("@ResponseDeadline", s.ResponseDeadline);
        cmd.Parameters.AddWithValue("@ResolutionDeadline", s.ResolutionDeadline);
        cmd.Parameters.AddWithValue("@EscalationDeadline", (object?)s.EscalationDeadline ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ResponseAchievedAt", (object?)s.ResponseAchievedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ResolutionAchievedAt", (object?)s.ResolutionAchievedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ResponseStatus", (int)s.ResponseStatus);
        cmd.Parameters.AddWithValue("@ResolutionStatus", (int)s.ResolutionStatus);
        cmd.Parameters.AddWithValue("@IsEscalated", s.IsEscalated);
        cmd.Parameters.AddWithValue("@EscalatedAt", (object?)s.EscalatedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedAtUtc", s.CreatedAtUtc);
    }

    private static void AddLookupParams(NpgsqlCommand cmd, WorkflowInstance i)
    {
        cmd.Parameters.AddWithValue("@InstanceId", i.Id);
        cmd.Parameters.AddWithValue("@WorkflowId", i.WorkflowId);
        cmd.Parameters.AddWithValue("@TenantId", i.TenantId);
        cmd.Parameters.AddWithValue("@WorkflowName", i.WorkflowName);
        cmd.Parameters.AddWithValue("@Status", (int)i.Status);
        cmd.Parameters.AddWithValue("@AssignedToUserId", (object?)i.AssignedToUserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@StartedBy", i.StartedBy);
        cmd.Parameters.AddWithValue("@CreatedAtUtc", i.CreatedAtUtc);
        cmd.Parameters.AddWithValue("@LastActivityAtUtc", (object?)i.LastActivityAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CompletedAtUtc", (object?)i.CompletedAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@IsArchived", i.IsArchived);
        cmd.Parameters.AddWithValue("@Priority", i.Priority);
        cmd.Parameters.AddWithValue("@CurrentStepInstanceId", (object?)i.CurrentStepInstanceId ?? DBNull.Value);
        AddLookupSlaParams(cmd, i);
    }

    private static void AddLookupSlaParams(NpgsqlCommand cmd, WorkflowInstance i)
    {
        if (i.Sla != null)
        {
            cmd.Parameters.AddWithValue("@SlaPriority", (int)i.Sla.Priority);
            cmd.Parameters.AddWithValue("@ResponseStatus", (int)i.Sla.ResponseStatus);
            cmd.Parameters.AddWithValue("@ResolutionStatus", (int)i.Sla.ResolutionStatus);
            cmd.Parameters.AddWithValue("@ResponseDeadline", i.Sla.ResponseDeadline);
            cmd.Parameters.AddWithValue("@ResolutionDeadline", i.Sla.ResolutionDeadline);
            cmd.Parameters.AddWithValue("@IsEscalated", i.Sla.IsEscalated);
        }
        else
        {
            cmd.Parameters.AddWithValue("@SlaPriority", DBNull.Value);
            cmd.Parameters.AddWithValue("@ResponseStatus", DBNull.Value);
            cmd.Parameters.AddWithValue("@ResolutionStatus", DBNull.Value);
            cmd.Parameters.AddWithValue("@ResponseDeadline", DBNull.Value);
            cmd.Parameters.AddWithValue("@ResolutionDeadline", DBNull.Value);
            cmd.Parameters.AddWithValue("@IsEscalated", false);
        }
    }

    private static WorkflowInstance ReadWorkflowInstance(NpgsqlDataReader r, Guid workflowId)
    {
        var instance = (WorkflowInstance)Activator.CreateInstance(typeof(WorkflowInstance), nonPublic: true)!;
        SetProperty(instance, "Id", r.GetGuid(r.GetOrdinal("id")));
        SetProperty(instance, "TenantId", r.GetGuid(r.GetOrdinal("tenant_id")));
        SetProperty(instance, "WorkflowId", workflowId);
        SetProperty(instance, "WorkflowName", r.GetString(r.GetOrdinal("workflow_name")));
        SetProperty(instance, "WorkflowVersion", r.GetInt32(r.GetOrdinal("workflow_version")));
        SetProperty(instance, "Status", (WorkflowInstanceStatus)r.GetInt32(r.GetOrdinal("status")));
        SetProperty(instance, "CurrentStepInstanceId", GetGuidOrNull(r, "current_step_instance_id"));
        SetProperty(instance, "CreatedAtUtc", r.GetDateTime(r.GetOrdinal("created_at_utc")));
        SetProperty(instance, "StartedAtUtc", GetDateTimeOrNull(r, "started_at_utc"));
        SetProperty(instance, "CompletedAtUtc", GetDateTimeOrNull(r, "completed_at_utc"));
        SetProperty(instance, "StartedBy", r.GetGuid(r.GetOrdinal("started_by")));
        SetProperty(instance, "Context", GetStringOrNull(r, "context"));
        SetProperty(instance, "ErrorMessage", GetStringOrNull(r, "error_message"));
        SetProperty(instance, "ReferenceNumber", GetStringOrNull(r, "reference_number"));
        SetProperty(instance, "CustomerName", GetStringOrNull(r, "customer_name"));
        SetProperty(instance, "CustomerEmail", GetStringOrNull(r, "customer_email"));
        SetProperty(instance, "CustomerPhone", GetStringOrNull(r, "customer_phone"));
        SetProperty(instance, "Department", GetStringOrNull(r, "department"));
        SetProperty(instance, "Category", GetStringOrNull(r, "category"));
        SetProperty(instance, "Priority", r.GetInt32(r.GetOrdinal("priority")));
        SetProperty(instance, "Tags", GetStringOrNull(r, "tags"));
        SetProperty(instance, "CustomFieldsJson", GetStringOrNull(r, "custom_fields_json"));
        SetProperty(instance, "AssignedToUserId", GetGuidOrNull(r, "assigned_to_user_id"));
        SetProperty(instance, "AssignedToGroupId", GetGuidOrNull(r, "assigned_to_group_id"));
        SetProperty(instance, "LastActivityAtUtc", GetDateTimeOrNull(r, "last_activity_at_utc"));
        SetProperty(instance, "ViewCount", r.GetInt32(r.GetOrdinal("view_count")));
        SetProperty(instance, "IsArchived", r.GetBoolean(r.GetOrdinal("is_archived")));
        SetProperty(instance, "ArchivedAtUtc", GetDateTimeOrNull(r, "archived_at_utc"));
        SetProperty(instance, "SourceType", GetStringOrNull(r, "source_type"));
        SetProperty(instance, "SourceId", GetStringOrNull(r, "source_id"));
        return instance;
    }

    private static WorkflowStepInstance ReadWorkflowStepInstance(NpgsqlDataReader r, Guid instanceId)
    {
        var step = (WorkflowStepInstance)Activator.CreateInstance(typeof(WorkflowStepInstance), nonPublic: true)!;
        SetProperty(step, "Id", r.GetGuid(r.GetOrdinal("id")));
        SetProperty(step, "WorkflowInstanceId", instanceId);
        SetProperty(step, "WorkflowStepId", r.GetGuid(r.GetOrdinal("workflow_step_id")));
        SetProperty(step, "StepName", r.GetString(r.GetOrdinal("step_name")));
        SetProperty(step, "StepType", (StepType)r.GetInt32(r.GetOrdinal("step_type")));
        SetProperty(step, "Order", r.GetInt32(r.GetOrdinal("order")));
        SetProperty(step, "Status", (StepInstanceStatus)r.GetInt32(r.GetOrdinal("status")));
        SetProperty(step, "AssignedToUserId", GetGuidOrNull(r, "assigned_to_user_id"));
        SetProperty(step, "AssignedToRole", GetStringOrNull(r, "assigned_to_role"));
        SetProperty(step, "CreatedAtUtc", r.GetDateTime(r.GetOrdinal("created_at_utc")));
        SetProperty(step, "StartedAtUtc", GetDateTimeOrNull(r, "started_at_utc"));
        SetProperty(step, "CompletedAtUtc", GetDateTimeOrNull(r, "completed_at_utc"));
        SetProperty(step, "CompletedBy", GetGuidOrNull(r, "completed_by"));
        SetProperty(step, "Result", GetStringOrNull(r, "result"));
        SetProperty(step, "ErrorMessage", GetStringOrNull(r, "error_message"));
        if (HasColumn(r, "activity_id"))
            SetProperty(step, "ActivityId", GetStringOrNull(r, "activity_id"));
        if (HasColumn(r, "stage_type"))
            SetProperty(step, "StageType", GetStringOrNull(r, "stage_type"));
        return step;
    }

    private static bool HasColumn(NpgsqlDataReader reader, string columnName)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (reader.GetName(i).Equals(columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static WorkflowInstanceSla ReadWorkflowInstanceSla(NpgsqlDataReader r, Guid instanceId)
    {
        var sla = (WorkflowInstanceSla)Activator.CreateInstance(typeof(WorkflowInstanceSla), nonPublic: true)!;
        SetProperty(sla, "Id", r.GetGuid(r.GetOrdinal("id")));
        SetProperty(sla, "WorkflowInstanceId", instanceId);
        SetProperty(sla, "Priority", (SlaPriority)r.GetInt32(r.GetOrdinal("priority")));
        SetProperty(sla, "ResponseDeadline", r.GetDateTime(r.GetOrdinal("response_deadline")));
        SetProperty(sla, "ResolutionDeadline", r.GetDateTime(r.GetOrdinal("resolution_deadline")));
        SetProperty(sla, "EscalationDeadline", GetDateTimeOrNull(r, "escalation_deadline"));
        SetProperty(sla, "ResponseAchievedAt", GetDateTimeOrNull(r, "response_achieved_at"));
        SetProperty(sla, "ResolutionAchievedAt", GetDateTimeOrNull(r, "resolution_achieved_at"));
        SetProperty(sla, "ResponseStatus", (SlaStatus)r.GetInt32(r.GetOrdinal("response_status")));
        SetProperty(sla, "ResolutionStatus", (SlaStatus)r.GetInt32(r.GetOrdinal("resolution_status")));
        SetProperty(sla, "IsEscalated", r.GetBoolean(r.GetOrdinal("is_escalated")));
        SetProperty(sla, "EscalatedAt", GetDateTimeOrNull(r, "escalated_at"));
        SetProperty(sla, "CreatedAtUtc", r.GetDateTime(r.GetOrdinal("created_at_utc")));
        return sla;
    }

    private static void SetProperty(object obj, string name, object? value)
    {
        var prop = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        prop?.SetValue(obj, value);
    }

    private static Guid? GetGuidOrNull(NpgsqlDataReader r, string name)
    {
        var idx = r.GetOrdinal(name);
        return r.IsDBNull(idx) ? null : r.GetGuid(idx);
    }

    private static DateTime? GetDateTimeOrNull(NpgsqlDataReader r, string name)
    {
        var idx = r.GetOrdinal(name);
        return r.IsDBNull(idx) ? null : r.GetDateTime(idx);
    }

    private static string? GetStringOrNull(NpgsqlDataReader r, string name)
    {
        var idx = r.GetOrdinal(name);
        return r.IsDBNull(idx) ? null : r.GetString(idx);
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, string tableName, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT 1 FROM information_schema.tables
            WHERE table_schema = 'workflow' AND table_name = @TableName;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TableName", tableName);
        return await cmd.ExecuteScalarAsync(cancellationToken) != null;
    }

    /// <summary>
    /// PHASE 4: WorkflowTableCreator.cs's GenerateWorkflowStepInstancesTableScript already adds
    /// activity_id/stage_type via ALTER TABLE ... ADD COLUMN IF NOT EXISTS at table-creation time,
    /// so there is no pre-existing-without-these-columns shape on Postgres. Kept as a cheap
    /// idempotent safety net rather than dropped outright (same reasoning as WorkflowStepSyncService.cs).
    /// </summary>
    private static async Task EnsureStepInstanceColumnsAsync(
        NpgsqlConnection connection,
        string suffix,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            ALTER TABLE IF EXISTS workflow.workflow_step_instances_{suffix} ADD COLUMN IF NOT EXISTS activity_id varchar(128) NULL;
            ALTER TABLE IF EXISTS workflow.workflow_step_instances_{suffix} ADD COLUMN IF NOT EXISTS stage_type varchar(64) NULL;
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
