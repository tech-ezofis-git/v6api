using System.Globalization;
using System.Text.Json;
using Npgsql;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Infrastructure.Services;

public sealed class WorkflowNotificationQueryService : IWorkflowNotificationQueryService
{
    private const string WorkflowCategory = "workflow";
    private const string TargetRoute = "requests";

    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUserProvider _currentUser;
    private readonly IUserEmailLookup _userEmails;

    public WorkflowNotificationQueryService(
        ITenantContext tenantContext,
        ICurrentUserProvider currentUser,
        IUserEmailLookup userEmails)
    {
        _tenantContext = tenantContext;
        _currentUser = currentUser;
        _userEmails = userEmails;
    }

    public async Task<IReadOnlyList<WorkflowNotificationItemDto>> ListForCurrentUserAsync(
        string? category = null,
        CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.GetUserId()
            ?? throw new InvalidOperationException("User context is required.");

        var connectionString = _tenantContext.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        var profiles = await _userEmails.GetProfilesAsync([userId], cancellationToken);
        var actor = ResolveActor(userId, profiles);
        var categoryFilter = string.IsNullOrWhiteSpace(category) ? null : category.Trim();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await FormMasterFileNotificationStore.EnsureTableAsync(connection, cancellationToken);

        var sql = """
            SELECT
                id, title, category, status, "Message", "Data", "Severity",
                "CreatedAtUtc", "createdAt", "readStatus", "CreatedByGuid"
            FROM dbo.notification
            WHERE "isDeleted" = false
              AND "CreatedByGuid" = @UserId
            """;
        if (categoryFilter != null)
            sql += """

              AND LOWER(LTRIM(RTRIM(COALESCE(category, '')))) = LOWER(@Category)
            """;
        sql += """

            ORDER BY COALESCE("CreatedAtUtc", '1900-01-01'::timestamptz) DESC, id DESC;
            """;

        var items = new List<WorkflowNotificationItemDto>();
        await using (var cmd = new NpgsqlCommand(sql, connection))
        {
            cmd.Parameters.AddWithValue("@UserId", userId);
            if (categoryFilter != null)
                cmd.Parameters.AddWithValue("@Category", categoryFilter);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt32(0).ToString(CultureInfo.InvariantCulture);
                var title = reader.IsDBNull(1) ? null : reader.GetString(1);
                var rowCategory = reader.IsDBNull(2) ? WorkflowCategory : reader.GetString(2);
                var status = reader.IsDBNull(3) ? null : reader.GetString(3);
                var storedMessage = reader.IsDBNull(4) ? null : reader.GetString(4);
                var dataJson = reader.IsDBNull(5) ? null : reader.GetString(5);
                var severity = reader.IsDBNull(6) ? "info" : reader.GetString(6);
                var createdAtUtc = reader.IsDBNull(7) ? (DateTime?)null : reader.GetDateTime(7);
                if (createdAtUtc == null && !reader.IsDBNull(8))
                {
                    var createdAtText = reader.GetString(8);
                    if (DateTime.TryParse(createdAtText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                        createdAtUtc = parsed;
                }

                var isRead = !reader.IsDBNull(9) && reader.GetInt32(9) != 0;
                var data = ParseData(dataJson);
                var message = IsTicketSubmitted(status)
                    ? WorkflowNotificationMessageMapper.SubmittedMessage(ReadStageName(dataJson))
                    : IsTicketReceived(status)
                        ? WorkflowNotificationMessageMapper.ReceivedMessage(ReadStageName(dataJson))
                        : storedMessage;

                items.Add(new WorkflowNotificationItemDto(
                    id,
                    actor,
                    string.IsNullOrWhiteSpace(rowCategory) ? WorkflowCategory : rowCategory.Trim(),
                    FormatRelativeCreatedAt(createdAtUtc),
                    data,
                    isRead,
                    message,
                    string.IsNullOrWhiteSpace(severity) ? "info" : severity.Trim().ToLowerInvariant(),
                    new WorkflowNotificationTargetDto(
                        TargetRoute,
                        new WorkflowNotificationSearchDto(data.ProcessId, data.TransactionId, data.WorkflowId)),
                    title));
            }
        }

        await EnrichMissingDataAsync(connection, items, cancellationToken);
        return items;
    }

    public async Task<WorkflowNotificationReadDto?> MarkReadAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.GetUserId()
            ?? throw new InvalidOperationException("User context is required.");

        var connectionString = _tenantContext.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await FormMasterFileNotificationStore.EnsureTableAsync(connection, cancellationToken);

        const string sql = """
            UPDATE dbo.notification
            SET "readStatus" = 1,
                "ModifiedAtUtc" = now()
            WHERE id = @Id
              AND "isDeleted" = false
              AND "CreatedByGuid" = @UserId
            RETURNING id, "readStatus";
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@UserId", userId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var rowId = reader.GetInt32(0).ToString(CultureInfo.InvariantCulture);
        var isRead = !reader.IsDBNull(1) && reader.GetInt32(1) != 0;
        return new WorkflowNotificationReadDto(rowId, isRead);
    }

    public async Task<bool> DeleteAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.GetUserId()
            ?? throw new InvalidOperationException("User context is required.");

        var connectionString = _tenantContext.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await FormMasterFileNotificationStore.EnsureTableAsync(connection, cancellationToken);

        const string sql = """
            UPDATE dbo.notification
            SET "isDeleted" = true,
                "ModifiedAtUtc" = now()
            WHERE id = @Id
              AND "isDeleted" = false
              AND "CreatedByGuid" = @UserId;
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@UserId", userId);
        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }

    private static WorkflowNotificationActorDto ResolveActor(
        Guid userId,
        IReadOnlyDictionary<Guid, UserProfileLookupDto> profiles)
    {
        if (!profiles.TryGetValue(userId, out var profile))
            return new WorkflowNotificationActorDto(userId.ToString("D"), string.Empty);

        var name = string.Join(
            " ",
            new[] { profile.FirstName, profile.LastName }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.Trim()));

        if (string.IsNullOrWhiteSpace(name))
            name = profile.DisplayName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
            name = profile.Email?.Trim() ?? userId.ToString("D");

        return new WorkflowNotificationActorDto(name, profile.Email?.Trim() ?? string.Empty);
    }

    private static string? FormatRelativeCreatedAt(DateTime? createdAtUtc)
    {
        if (createdAtUtc == null)
            return null;

        var created = createdAtUtc.Value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(createdAtUtc.Value, DateTimeKind.Utc)
            : createdAtUtc.Value.ToUniversalTime();

        var elapsed = DateTime.UtcNow - created;
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        if (elapsed.TotalDays < 1)
            return $"minutesAgo({(int)elapsed.TotalMinutes})";
        if (elapsed.TotalDays < 7)
            return $"daysAgo({(int)elapsed.TotalDays})";
        return $"weekAgo({(int)(elapsed.TotalDays / 7)})";
    }

    private static bool IsTicketSubmitted(string? status) =>
        string.Equals(status, "Ticket Submitted", StringComparison.OrdinalIgnoreCase);

    private static bool IsTicketReceived(string? status) =>
        string.Equals(status, "Ticket Received", StringComparison.OrdinalIgnoreCase);

    private static string? ReadStageName(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object ? ReadString(root, "stageName") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static WorkflowNotificationDataDto ParseData(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new WorkflowNotificationDataDto(null, null, null, null);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new WorkflowNotificationDataDto(null, null, null, null);

            var instanceId = ReadString(root, "instanceId") ?? ReadString(root, "processId");
            var transactionId = ReadString(root, "transactionId");
            var workflowId = ReadString(root, "workflowId");
            var workflowName = ReadString(root, "workflowName");
            return new WorkflowNotificationDataDto(instanceId, transactionId, workflowId, workflowName);
        }
        catch (JsonException)
        {
            return new WorkflowNotificationDataDto(null, null, null, null);
        }
    }

    private static string? ReadString(JsonElement root, string name)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (!prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                var value = prop.Value.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }

            if (prop.Value.ValueKind is JsonValueKind.Number)
                return prop.Value.ToString();
        }

        return null;
    }

    private static async Task EnrichMissingDataAsync(
        NpgsqlConnection connection,
        List<WorkflowNotificationItemDto> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
            return;

        var needsEnrichment = items.Any(i => string.IsNullOrWhiteSpace(i.Data.WorkflowName));
        if (!needsEnrichment)
            return;

        var instanceIds = items
            .Select(i => i.Data.ProcessId)
            .Where(id => Guid.TryParse(id, out _))
            .Select(id => Guid.Parse(id!))
            .Distinct()
            .ToList();

        var byInstance = instanceIds.Count == 0
            ? new Dictionary<Guid, InstanceEnrichment>()
            : await LoadInstanceEnrichmentAsync(connection, instanceIds, cancellationToken);

        var missingWorkflowIds = items
            .Where(i => string.IsNullOrWhiteSpace(i.Data.WorkflowName))
            .Select(i => i.Data.WorkflowId)
            .Concat(byInstance.Values
                .Where(v => string.IsNullOrWhiteSpace(v.WorkflowName))
                .Select(v => v.WorkflowId.ToString("D")))
            .Where(id => Guid.TryParse(id, out _))
            .Select(id => Guid.Parse(id!))
            .Distinct()
            .ToList();

        var workflowNames = missingWorkflowIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await LoadWorkflowNamesAsync(connection, missingWorkflowIds, cancellationToken);

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var data = item.Data;
            string? workflowName = data.WorkflowName;
            string? workflowId = data.WorkflowId;

            if (Guid.TryParse(data.ProcessId, out var instanceId)
                && byInstance.TryGetValue(instanceId, out var row))
            {
                workflowName ??= NullIfEmpty(row.WorkflowName);
                workflowId ??= row.WorkflowId.ToString("D");
            }

            if (string.IsNullOrWhiteSpace(workflowName)
                && Guid.TryParse(workflowId ?? data.WorkflowId, out var wfId)
                && workflowNames.TryGetValue(wfId, out var nameFromWorkflow))
            {
                workflowName = nameFromWorkflow;
            }

            if (workflowName == data.WorkflowName && workflowId == data.WorkflowId)
                continue;

            var enriched = data with
            {
                WorkflowName = workflowName,
                WorkflowId = workflowId
            };
            items[i] = item with
            {
                Data = enriched,
                Target = item.Target with
                {
                    Search = item.Target.Search with
                    {
                        ProcessId = enriched.ProcessId,
                        TransactionId = enriched.TransactionId,
                        WorkflowId = enriched.WorkflowId
                    }
                }
            };
        }
    }

    private static async Task<Dictionary<Guid, InstanceEnrichment>> LoadInstanceEnrichmentAsync(
        NpgsqlConnection connection,
        IReadOnlyList<Guid> instanceIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, InstanceEnrichment>();
        if (!await TableExistsAsync(connection, "workflow", "WorkflowInstanceLookup", cancellationToken))
            return result;

        var lookupSql = $"""
            SELECT "InstanceId", "WorkflowId", "WorkflowName"
            FROM workflow."WorkflowInstanceLookup"
            WHERE "InstanceId" IN ({BuildGuidInClause(instanceIds.Count, "i")});
            """;

        await using (var cmd = new NpgsqlCommand(lookupSql, connection))
        {
            BindGuids(cmd, "i", instanceIds);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var instanceId = reader.GetGuid(0);
                var workflowId = reader.GetGuid(1);
                var workflowName = reader.IsDBNull(2) ? null : reader.GetString(2);
                result[instanceId] = new InstanceEnrichment(instanceId, workflowId, NullIfEmpty(workflowName));
            }
        }

        var groups = result.Values
            .GroupBy(r => r.WorkflowId)
            .Select(g => (WorkflowId: g.Key, InstanceIds: g.Select(x => x.InstanceId).ToList()))
            .ToList();

        foreach (var group in groups)
        {
            var suffix = group.WorkflowId.ToString("N")[..8];
            var tableName = $"workflow_instances_{suffix}";
            if (!await TableExistsAsync(connection, "workflow", tableName, cancellationToken))
                continue;

            var ids = group.InstanceIds;
            var sql = $"""
                SELECT id, workflow_name
                FROM workflow.{tableName}
                WHERE id IN ({BuildGuidInClause(ids.Count, "r")});
                """;

            await using var cmd = new NpgsqlCommand(sql, connection);
            BindGuids(cmd, "r", ids);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var instanceId = reader.GetGuid(0);
                var workflowName = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (!result.TryGetValue(instanceId, out var existing))
                    continue;

                result[instanceId] = existing with
                {
                    WorkflowName = NullIfEmpty(workflowName) ?? existing.WorkflowName
                };
            }
        }

        return result;
    }

    private static async Task<Dictionary<Guid, string>> LoadWorkflowNamesAsync(
        NpgsqlConnection connection,
        IReadOnlyList<Guid> workflowIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, string>();
        if (!await TableExistsAsync(connection, "workflow", "Workflows", cancellationToken))
            return result;

        var sql = $"""
            SELECT "Id", "Name"
            FROM workflow."Workflows"
            WHERE "Id" IN ({BuildGuidInClause(workflowIds.Count, "w")})
              AND "IsDeleted" = false;
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        BindGuids(cmd, "w", workflowIds);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (!string.IsNullOrWhiteSpace(name))
                result[reader.GetGuid(0)] = name.Trim();
        }

        return result;
    }

    private static async Task<bool> TableExistsAsync(
        NpgsqlConnection connection,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT 1
            FROM information_schema.tables
            WHERE table_name = @Name AND table_schema = @Schema;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Name", table);
        cmd.Parameters.AddWithValue("@Schema", schema);
        var scalar = await cmd.ExecuteScalarAsync(cancellationToken);
        return scalar is not null && scalar != DBNull.Value;
    }

    private static string BuildGuidInClause(int count, string prefix) =>
        string.Join(", ", Enumerable.Range(0, count).Select(i => $"@{prefix}{i}"));

    private static void BindGuids(NpgsqlCommand cmd, string prefix, IReadOnlyList<Guid> ids)
    {
        for (var i = 0; i < ids.Count; i++)
            cmd.Parameters.AddWithValue($"@{prefix}{i}", ids[i]);
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record InstanceEnrichment(
        Guid InstanceId,
        Guid WorkflowId,
        string? WorkflowName);
}
