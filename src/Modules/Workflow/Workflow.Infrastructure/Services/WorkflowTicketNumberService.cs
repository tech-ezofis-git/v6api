using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Application.Workflows;
using SaaSApp.Workflow.Application.Workflows.Commands.CreateWorkflow;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>
/// Allocates sequential ticket numbers per workflow from
/// <c>settings.general.processNumberPrefix</c> in workflow JSON
/// (e.g. prefix=REQ, separator=-, autoIncrement → REQ-1, REQ-2).
/// </summary>
public sealed class WorkflowTicketNumberService : IWorkflowTicketNumberService
{
    private readonly ITenantContext _tenantContext;
    private readonly IWorkflowJsonStorageService _workflowJsonStorage;

    public WorkflowTicketNumberService(
        ITenantContext tenantContext,
        IWorkflowJsonStorageService workflowJsonStorage)
    {
        _tenantContext = tenantContext;
        _workflowJsonStorage = workflowJsonStorage;
    }

    public async Task<string> AllocateNextAsync(Guid workflowId, CancellationToken cancellationToken = default)
    {
        if (workflowId == Guid.Empty)
            throw new ArgumentException("workflowId is required.", nameof(workflowId));

        var format = await ResolveFormatAsync(workflowId, cancellationToken);

        var connectionString = _tenantContext.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable,
            cancellationToken);

        try
        {
            await EnsureCounterTableAsync(connection, transaction, cancellationToken);

            var next = await IncrementCounterAsync(connection, transaction, workflowId, cancellationToken);
            if (next <= 0)
            {
                var seeded = await SeedFromExistingInstancesAsync(
                    connection,
                    transaction,
                    workflowId,
                    format,
                    cancellationToken);
                if (seeded < format.StartAt)
                    seeded = format.StartAt;

                next = await IncrementCounterAsync(connection, transaction, workflowId, cancellationToken, seeded);
            }

            await transaction.CommitAsync(cancellationToken);
            return format.Format(next);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<ProcessNumberFormat> ResolveFormatAsync(
        Guid workflowId,
        CancellationToken cancellationToken)
    {
        var rawJson = await _workflowJsonStorage.GetWorkflowJsonAsync(workflowId, cancellationToken);
        if (string.IsNullOrWhiteSpace(rawJson))
            return ProcessNumberFormat.Default;

        try
        {
            var dto = JsonSerializer.Deserialize<WorkflowJsonDto>(
                rawJson,
                WorkflowJsonSerializerOptions.Storage);
            return ProcessNumberFormat.FromProcessNumberPrefix(dto?.Settings?.General?.ProcessNumberPrefix);
        }
        catch (JsonException)
        {
            return ProcessNumberFormat.Default;
        }
    }

    private static async Task EnsureCounterTableAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE SCHEMA IF NOT EXISTS workflow;

            CREATE TABLE IF NOT EXISTS workflow.ticket_number_counters (
                workflow_id uuid NOT NULL
                    CONSTRAINT pk_ticket_number_counters PRIMARY KEY,
                last_number integer NOT NULL DEFAULT 0,
                modified_at_utc timestamptz NOT NULL DEFAULT now()
            );
            """;

        await using var cmd = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 60 };
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> IncrementCounterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workflowId,
        CancellationToken cancellationToken,
        int? insertStartingAt = null)
    {
        if (insertStartingAt is int seed)
        {
            const string insertSql = """
                INSERT INTO workflow.ticket_number_counters (workflow_id, last_number, modified_at_utc)
                VALUES (@WorkflowId, @LastNumber, now())
                ON CONFLICT (workflow_id) DO UPDATE
                SET last_number = CASE
                        WHEN workflow.ticket_number_counters.last_number < EXCLUDED.last_number
                        THEN EXCLUDED.last_number
                        ELSE workflow.ticket_number_counters.last_number + 1
                    END,
                    modified_at_utc = now()
                RETURNING last_number;
                """;

            await using var insertCmd = new NpgsqlCommand(insertSql, connection, transaction);
            insertCmd.Parameters.AddWithValue("@WorkflowId", workflowId);
            insertCmd.Parameters.AddWithValue("@LastNumber", seed);
            var inserted = await insertCmd.ExecuteScalarAsync(cancellationToken);
            return inserted is int i ? i : Convert.ToInt32(inserted, CultureInfo.InvariantCulture);
        }

        const string updateSql = """
            UPDATE workflow.ticket_number_counters
            SET last_number = last_number + 1,
                modified_at_utc = now()
            WHERE workflow_id = @WorkflowId
            RETURNING last_number;
            """;

        await using var cmd = new NpgsqlCommand(updateSql, connection, transaction);
        cmd.Parameters.AddWithValue("@WorkflowId", workflowId);
        var scalar = await cmd.ExecuteScalarAsync(cancellationToken);
        if (scalar is null || scalar == DBNull.Value)
            return 0;
        return scalar is int n ? n : Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    private static async Task<int> SeedFromExistingInstancesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workflowId,
        ProcessNumberFormat format,
        CancellationToken cancellationToken)
    {
        var suffix = workflowId.ToString("N")[..8];
        var tableName = $"workflow_instances_{suffix}";

        const string tableExistsSql = """
            SELECT 1
            FROM information_schema.tables
            WHERE table_schema = 'workflow' AND table_name = @TableName;
            """;

        await using (var existsCmd = new NpgsqlCommand(tableExistsSql, connection, transaction))
        {
            existsCmd.Parameters.AddWithValue("@TableName", tableName);
            var exists = await existsCmd.ExecuteScalarAsync(cancellationToken);
            if (exists is null || exists == DBNull.Value)
                return format.StartAt;
        }

        // Match "{staticPrefix}{digits}" e.g. REQ-1, VC-42 (not long timestamp suffixes).
        // Lock instance rows first — Postgres forbids FOR UPDATE on aggregate queries.
        var lockSql = $"SELECT id FROM workflow.{tableName} FOR UPDATE;";
        await using (var lockCmd = new NpgsqlCommand(lockSql, connection, transaction))
            await lockCmd.ExecuteNonQueryAsync(cancellationToken);

        var prefixLen = format.StaticPrefix.Length;
        var maxSql = $"""
            SELECT COALESCE(MAX(
                CASE
                    WHEN reference_number LIKE @LikePrefix || '%'
                     AND substring(reference_number FROM @PrefixLen + 1) ~ '^[0-9]+$'
                     AND length(reference_number) <= @PrefixLen + 9
                    THEN CAST(substring(reference_number FROM @PrefixLen + 1) AS integer)
                    ELSE NULL
                END
            ), 0)
            FROM workflow.{tableName};
            """;

        await using var maxCmd = new NpgsqlCommand(maxSql, connection, transaction);
        maxCmd.Parameters.AddWithValue("@LikePrefix", format.StaticPrefix);
        maxCmd.Parameters.AddWithValue("@PrefixLen", prefixLen);
        var maxScalar = await maxCmd.ExecuteScalarAsync(cancellationToken);
        var max = maxScalar is int m ? m : Convert.ToInt32(maxScalar ?? 0, CultureInfo.InvariantCulture);
        return max > 0 ? max + 1 : format.StartAt;
    }

    internal sealed class ProcessNumberFormat
    {
        public static ProcessNumberFormat Default { get; } = new("REQ", "-", 1);

        public string Prefix { get; }
        public string Separator { get; }
        public int StartAt { get; }

        /// <summary>Everything before the numeric sequence (e.g. <c>REQ-</c>).</summary>
        public string StaticPrefix => $"{Prefix}{Separator}";

        private ProcessNumberFormat(string prefix, string separator, int startAt)
        {
            Prefix = prefix;
            Separator = separator;
            StartAt = startAt < 1 ? 1 : startAt;
        }

        public string Format(int number) => $"{Prefix}{Separator}{number.ToString(CultureInfo.InvariantCulture)}";

        public static ProcessNumberFormat FromProcessNumberPrefix(string? processNumberPrefix)
        {
            if (string.IsNullOrWhiteSpace(processNumberPrefix))
                return Default;

            var parts = ParseParts(processNumberPrefix.Trim());
            if (parts.Count == 0)
                return Default;

            // Semantic order for ticket: prefix + separator + autoIncrement number
            // (designer JSON id order may list separator before prefix).
            string? prefix = null;
            string? separator = null;
            var startAt = 1;

            foreach (var part in parts.OrderBy(p => p.SortId).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                var key = part.Key.Trim();
                if (IsAutoIncrement(key))
                {
                    if (int.TryParse(part.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0)
                        startAt = n;
                }
                else if (IsSeparator(key))
                {
                    separator = part.Value;
                }
                else if (IsPrefix(key))
                {
                    prefix = part.Value;
                }
            }

            // Fallback: if no typed prefix, concatenate non-separator/non-auto parts by id
            if (string.IsNullOrWhiteSpace(prefix))
            {
                var sb = new StringBuilder();
                foreach (var part in parts.OrderBy(p => p.SortId))
                {
                    if (IsSeparator(part.Key) || IsAutoIncrement(part.Key))
                        continue;
                    sb.Append(part.Value);
                }

                prefix = sb.Length > 0 ? sb.ToString() : "REQ";
            }

            separator ??= "-";
            return new ProcessNumberFormat(prefix.Trim(), separator, startAt);
        }

        private static List<ProcessNumberPart> ParseParts(string raw)
        {
            try
            {
                // Stored as JSON string of array, or already an array JSON.
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.String)
                {
                    var inner = root.GetString();
                    if (string.IsNullOrWhiteSpace(inner))
                        return [];
                    using var innerDoc = JsonDocument.Parse(inner);
                    root = innerDoc.RootElement.Clone();
                }

                if (root.ValueKind != JsonValueKind.Array)
                    return [];

                var list = new List<ProcessNumberPart>();
                foreach (var el in root.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object)
                        continue;

                    var id = ReadString(el, "id") ?? "0";
                    var key = ReadString(el, "key") ?? string.Empty;
                    var value = ReadFlexibleValue(el, "value");
                    if (string.IsNullOrWhiteSpace(key))
                        continue;

                    list.Add(new ProcessNumberPart(id, key, value ?? string.Empty));
                }

                return list;
            }
            catch (JsonException)
            {
                return [];
            }
        }

        private static string? ReadString(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var prop))
                return null;
            return prop.ValueKind == JsonValueKind.String ? prop.GetString()
                : prop.ValueKind == JsonValueKind.Number ? prop.ToString()
                : null;
        }

        private static string? ReadFlexibleValue(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var prop))
                return null;
            return prop.ValueKind switch
            {
                JsonValueKind.String => prop.GetString(),
                JsonValueKind.Number => prop.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
        }

        private static bool IsPrefix(string key) =>
            key.Equals("prefix", StringComparison.OrdinalIgnoreCase)
            || key.Contains("prefix", StringComparison.OrdinalIgnoreCase);

        private static bool IsSeparator(string key) =>
            key.Equals("seperator", StringComparison.OrdinalIgnoreCase) // designer typo
            || key.Equals("separator", StringComparison.OrdinalIgnoreCase)
            || key.Contains("separ", StringComparison.OrdinalIgnoreCase);

        private static bool IsAutoIncrement(string key) =>
            key.Equals("autoIncrement", StringComparison.OrdinalIgnoreCase)
            || key.Equals("autoincrement", StringComparison.OrdinalIgnoreCase)
            || key.Contains("auto", StringComparison.OrdinalIgnoreCase)
            && key.Contains("increment", StringComparison.OrdinalIgnoreCase);

        private sealed record ProcessNumberPart(string Id, string Key, string Value)
        {
            public int SortId =>
                int.TryParse(Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue;
        }
    }
}
