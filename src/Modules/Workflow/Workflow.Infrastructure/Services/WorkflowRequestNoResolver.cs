using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace SaaSApp.Workflow.Infrastructure.Services;

internal static class WorkflowRequestNoResolver
{
    private static readonly Regex TicketNoInMessage = new(
        @"ticket\s+no\s*-\s*(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string? FromDataJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            return FirstNonEmpty(
                ReadString(root, "requestNo"),
                ReadString(root, "referenceNumber"),
                ReadString(root, "requestNumber"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? FromMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var match = TicketNoInMessage.Match(message.Trim());
        return match.Success ? NullIfEmpty(match.Groups[1].Value) : null;
    }

    public static async Task<string?> ResolveAsync(
        NpgsqlConnection connection,
        Guid workflowId,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        var suffix = workflowId.ToString("N")[..8];

        var fromInstance = await LoadFromInstanceAsync(connection, suffix, instanceId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(fromInstance))
            return fromInstance;

        var fromMailbox = await LoadFromMailboxAsync(connection, suffix, instanceId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(fromMailbox))
            return fromMailbox;

        var fromProcessForm = await LoadFromProcessFormAsync(connection, suffix, instanceId, cancellationToken);
        return fromProcessForm;
    }

    public static async Task<Dictionary<Guid, string>> ResolveManyAsync(
        NpgsqlConnection connection,
        Guid workflowId,
        IReadOnlyList<Guid> instanceIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, string>();
        if (instanceIds.Count == 0)
            return result;

        var suffix = workflowId.ToString("N")[..8];
        await FillFromInstanceAsync(connection, suffix, instanceIds, result, cancellationToken);

        var missing = instanceIds.Where(id => !result.ContainsKey(id)).ToList();
        if (missing.Count > 0)
            await FillFromMailboxAsync(connection, suffix, missing, result, cancellationToken);

        missing = instanceIds.Where(id => !result.ContainsKey(id)).ToList();
        if (missing.Count > 0)
            await FillFromProcessFormAsync(connection, suffix, missing, result, cancellationToken);

        return result;
    }

    private static async Task<string?> LoadFromInstanceAsync(
        NpgsqlConnection connection,
        string suffix,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<Guid, string>();
        await FillFromInstanceAsync(connection, suffix, [instanceId], map, cancellationToken);
        return map.TryGetValue(instanceId, out var value) ? value : null;
    }

    private static async Task FillFromInstanceAsync(
        NpgsqlConnection connection,
        string suffix,
        IReadOnlyList<Guid> instanceIds,
        Dictionary<Guid, string> result,
        CancellationToken cancellationToken)
    {
        var tableName = $"workflow_instances_{suffix}";
        if (!await TableExistsAsync(connection, "workflow", tableName, cancellationToken))
            return;

        var hasRequestNo = await ColumnExistsAsync(connection, "workflow", tableName, "request_no", cancellationToken);
        var hasReference = await ColumnExistsAsync(connection, "workflow", tableName, "reference_number", cancellationToken);
        if (!hasRequestNo && !hasReference)
            return;

        var selectCols = hasRequestNo && hasReference
            ? "id, reference_number, request_no"
            : hasReference
                ? "id, reference_number"
                : "id, request_no";

        var sql = $"""
            SELECT {selectCols}
            FROM workflow.{tableName}
            WHERE id IN ({BuildGuidInClause(instanceIds.Count, "n")});
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        BindGuids(cmd, "n", instanceIds);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetGuid(0);
            var value = NullIfEmpty(reader.IsDBNull(1) ? null : reader.GetString(1));
            if (value == null && reader.FieldCount > 2 && !reader.IsDBNull(2))
                value = NullIfEmpty(reader.GetString(2));
            if (value != null)
                result[id] = value;
        }
    }

    private static async Task<string?> LoadFromMailboxAsync(
        NpgsqlConnection connection,
        string suffix,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<Guid, string>();
        await FillFromMailboxAsync(connection, suffix, [instanceId], map, cancellationToken);
        return map.TryGetValue(instanceId, out var value) ? value : null;
    }

    private static async Task FillFromMailboxAsync(
        NpgsqlConnection connection,
        string suffix,
        IReadOnlyList<Guid> instanceIds,
        Dictionary<Guid, string> result,
        CancellationToken cancellationToken)
    {
        var idD = instanceIds.Select(id => id.ToString("D")).ToList();
        var idN = instanceIds.Select(id => id.ToString("N")).ToList();

        foreach (var prefix in new[] { "inbox", "sent", "completed" })
        {
            var tableName = $"{prefix}_{suffix}";
            if (!await TableExistsAsync(connection, "workflow", tableName, cancellationToken))
                continue;
            if (!await ColumnExistsAsync(connection, "workflow", tableName, "reference_number", cancellationToken))
                continue;
            if (!await ColumnExistsAsync(connection, "workflow", tableName, "workflow_instance_id", cancellationToken))
                continue;

            var remaining = instanceIds.Where(id => !result.ContainsKey(id)).ToList();
            if (remaining.Count == 0)
                return;

            var sql = $"""
                SELECT workflow_instance_id, reference_number
                FROM workflow.{tableName}
                WHERE reference_number IS NOT NULL
                  AND LTRIM(RTRIM(reference_number)) <> ''
                  AND (
                    workflow_instance_id IN ({BuildStringInClause(idD.Count, "d")})
                    OR workflow_instance_id IN ({BuildStringInClause(idN.Count, "n")})
                  );
                """;

            await using var cmd = new NpgsqlCommand(sql, connection);
            BindStrings(cmd, "d", idD);
            BindStrings(cmd, "n", idN);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var rawId = reader.IsDBNull(0) ? null : reader.GetString(0);
                var requestNo = NullIfEmpty(reader.IsDBNull(1) ? null : reader.GetString(1));
                if (requestNo == null || !Guid.TryParse(rawId, out var instanceId))
                    continue;
                result.TryAdd(instanceId, requestNo);
            }
        }
    }

    private static async Task<string?> LoadFromProcessFormAsync(
        NpgsqlConnection connection,
        string suffix,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<Guid, string>();
        await FillFromProcessFormAsync(connection, suffix, [instanceId], map, cancellationToken);
        return map.TryGetValue(instanceId, out var value) ? value : null;
    }

    private static async Task FillFromProcessFormAsync(
        NpgsqlConnection connection,
        string suffix,
        IReadOnlyList<Guid> instanceIds,
        Dictionary<Guid, string> result,
        CancellationToken cancellationToken)
    {
        var tableName = $"process_form_{suffix}";
        if (!await TableExistsAsync(connection, "workflow", tableName, cancellationToken))
            return;
        if (!await ColumnExistsAsync(connection, "workflow", tableName, "workflow_instance_id", cancellationToken))
            return;

        var sql = $"""
            SELECT workflow_instance_id, MIN(id)
            FROM workflow.{tableName}
            WHERE workflow_instance_id IN ({BuildGuidInClause(instanceIds.Count, "p")})
            GROUP BY workflow_instance_id;
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        BindGuids(cmd, "p", instanceIds);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
                continue;
            result.TryAdd(reader.GetGuid(0), reader.GetInt32(1).ToString(CultureInfo.InvariantCulture));
        }
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

    private static async Task<bool> ColumnExistsAsync(
        NpgsqlConnection connection,
        string schema,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT 1
            FROM information_schema.columns
            WHERE table_name = @Table AND table_schema = @Schema AND column_name = @Column;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Table", table);
        cmd.Parameters.AddWithValue("@Schema", schema);
        cmd.Parameters.AddWithValue("@Column", column);
        var scalar = await cmd.ExecuteScalarAsync(cancellationToken);
        return scalar is not null && scalar != DBNull.Value;
    }

    private static string BuildGuidInClause(int count, string prefix) =>
        string.Join(", ", Enumerable.Range(0, count).Select(i => $"@{prefix}{i}"));

    private static string BuildStringInClause(int count, string prefix) =>
        string.Join(", ", Enumerable.Range(0, count).Select(i => $"@{prefix}{i}"));

    private static void BindGuids(NpgsqlCommand cmd, string prefix, IReadOnlyList<Guid> ids)
    {
        for (var i = 0; i < ids.Count; i++)
            cmd.Parameters.AddWithValue($"@{prefix}{i}", ids[i]);
    }

    private static void BindStrings(NpgsqlCommand cmd, string prefix, IReadOnlyList<string> values)
    {
        for (var i = 0; i < values.Count; i++)
            cmd.Parameters.AddWithValue($"@{prefix}{i}", values[i]);
    }

    private static string? ReadString(JsonElement root, string name)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (!prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (prop.Value.ValueKind == JsonValueKind.String)
                return NullIfEmpty(prop.Value.GetString());
            if (prop.Value.ValueKind == JsonValueKind.Number)
                return prop.Value.ToString();
        }

        return null;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
