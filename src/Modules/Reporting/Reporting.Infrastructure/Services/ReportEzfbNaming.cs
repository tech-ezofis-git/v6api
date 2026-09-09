using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;
using SaaSApp.Workflow.Infrastructure.Services;

namespace SaaSApp.Reporting.Infrastructure.Services;

internal static class ReportEzfbNaming
{
    public const int SuffixLength = FormIdNaming.EzfbTableSuffixLength;

    public static string NormalizeFormId(string formId) => FormIdNaming.NormalizeFormId(formId);

    public static string GetTableSuffix(string formId) => FormIdNaming.GetEzfbTableSuffix(formId);

    public static string ItemsTable(string formId) => $"ezfb_{GetTableSuffix(formId)}_items";

    public static async Task<IReadOnlyList<(string FormId, string FormName, string? Type)>> ListWFormsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "wForm", cancellationToken))
            return [];

        const string sql = """
            SELECT id::text, name, type
            FROM dbo."wForm"
            WHERE "isDeleted" = false
            ORDER BY name;
            """;
        var rows = new List<(string, string, string?)>();
        await using var cmd = new NpgsqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((
                reader.GetString(0),
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return rows;
    }

    public static async Task<(string FormId, string FormName, string? Type)?> FindWFormAsync(
        NpgsqlConnection connection,
        string? key,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        if (!await TableExistsAsync(connection, "wForm", cancellationToken))
            return null;

        var token = key.Trim();
        const string sql = """
            SELECT id::text, name, type
            FROM dbo."wForm"
            WHERE "isDeleted" = false
              AND (
                    id::text = @Key
                 OR LOWER(REPLACE(id::text, '-', '')) = @Compact
                 OR LOWER(TRIM(name)) = LOWER(@Key)
              )
            ORDER BY CASE
                WHEN id::text = @Key THEN 0
                WHEN LOWER(TRIM(name)) = LOWER(@Key) THEN 1
                ELSE 2 END
            LIMIT 1;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Key", token);
        cmd.Parameters.AddWithValue("@Compact", string.Concat(token.Where(char.IsLetterOrDigit)).ToLowerInvariant());
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return (
            reader.GetString(0),
            reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    /// <summary>workflow.process_form_{suffix} uses the first 8 hex chars of the workflow GUID (N format).</summary>
    public static string WorkflowTableSuffix(Guid workflowId) => workflowId.ToString("N")[..SuffixLength];

    public static bool LooksLikeWorkflowId(string? value, Guid workflowId)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var trimmed = value.Trim();
        if (Guid.TryParse(trimmed, out var guid) && guid == workflowId)
            return true;
        var compact = string.Concat(trimmed.Where(char.IsLetterOrDigit));
        var wf = workflowId.ToString("N");
        return compact.Equals(wf, StringComparison.OrdinalIgnoreCase)
               || compact.Equals(wf[..SuffixLength], StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<bool> TableExistsAsync(
        NpgsqlConnection connection,
        string schema,
        string tableName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT 1
            FROM information_schema.tables
            WHERE table_schema = @Schema AND table_name = @TableName;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Schema", schema);
        cmd.Parameters.AddWithValue("@TableName", tableName);
        return await cmd.ExecuteScalarAsync(cancellationToken) != null;
    }

    public static async Task<string?> TryReadProcessFormWFormIdAsync(
        NpgsqlConnection connection,
        Guid workflowId,
        CancellationToken cancellationToken)
    {
        var suffix = WorkflowTableSuffix(workflowId);
        var tableName = $"process_form_{suffix}";
        if (!await TableExistsAsync(connection, "workflow", tableName, cancellationToken))
            return null;

        var sql = $"""
            SELECT w_form_id::text
            FROM workflow.{tableName}
            WHERE (is_deleted = false OR is_deleted IS NULL)
              AND w_form_id IS NOT NULL
            ORDER BY id DESC
            LIMIT 1;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        if (value is null or DBNull)
            return null;
        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public static string EscapeColumn(string column) => column.Replace("\"", "\"\"", StringComparison.Ordinal);

    /// <summary>
    /// Quote an ezfb column for dynamic SQL: system cols stay unquoted snake_case;
    /// field cols are double-quoted with casing preserved.
    /// </summary>
    public static string QuoteEzfbColumn(string column)
    {
        var physical = ToPhysicalSystemColumn(column) ?? column;
        if (IsSystemColumn(physical) || IsSystemColumn(column))
            return ToPhysicalSystemColumn(column) ?? physical.ToLowerInvariant();

        return $"\"{EscapeColumn(column)}\"";
    }

    public static bool TryToColumnName(string jsonId, out string column) =>
        EzfbColumnNaming.TryToColumnName(jsonId, out column);

    public static bool TryResolveColumn(string jsonId, IReadOnlySet<string> ezfbColumns, out string? column)
    {
        if (EzfbColumnNaming.TryResolveEzfbColumn(jsonId, ezfbColumns, out var resolved))
        {
            column = resolved;
            return true;
        }

        column = null;
        return false;
    }

    public static bool TryResolveColumn(
        string? name,
        string? jsonId,
        IReadOnlySet<string> ezfbColumns,
        out string? column)
    {
        if (EzfbColumnNaming.TryResolveEzfbColumn(name, jsonId, ezfbColumns, out var resolved))
        {
            column = resolved;
            return true;
        }

        column = null;
        return false;
    }

    public static bool TryResolveColumn(
        string? columnName,
        string? name,
        string? jsonId,
        IReadOnlySet<string> ezfbColumns,
        out string? column)
    {
        if (EzfbColumnNaming.TryResolveEzfbColumn(columnName, name, jsonId, ezfbColumns, out var resolved))
        {
            column = resolved;
            return true;
        }

        column = null;
        return false;
    }

    public static string CompactToken(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit));

    public static string DisplayCell(object? value)
    {
        var raw = CellString(value);
        if (string.IsNullOrWhiteSpace(raw))
            return raw;

        var trimmed = raw.Trim();
        if (trimmed.Length < 2
            || (trimmed[0] != '{' && trimmed[0] != '['))
            return raw;

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var display = FlattenJsonValue(doc.RootElement);
            return string.IsNullOrWhiteSpace(display) ? raw : display;
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    private static string FlattenJsonValue(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return string.Empty;
            case JsonValueKind.String:
                return element.GetString() ?? string.Empty;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return element.GetRawText();
            case JsonValueKind.Array:
                return string.Join(
                    ", ",
                    element.EnumerateArray()
                        .Select(FlattenJsonValue)
                        .Where(v => !string.IsNullOrWhiteSpace(v)));
            case JsonValueKind.Object:
                foreach (var name in new[] { "name", "label", "text", "displayName", "title", "value" })
                {
                    foreach (var prop in element.EnumerateObject())
                    {
                        if (!prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                            continue;
                        var nested = FlattenJsonValue(prop.Value);
                        if (!string.IsNullOrWhiteSpace(nested))
                            return nested;
                    }
                }

                return string.Join(
                    ", ",
                    element.EnumerateObject()
                        .Select(p => FlattenJsonValue(p.Value))
                        .Where(v => !string.IsNullOrWhiteSpace(v)));
            default:
                return element.ToString();
        }
    }

    public static bool IsSystemColumn(string column) =>
        column.Equals("item_id", StringComparison.OrdinalIgnoreCase)
        || column.Equals("itemId", StringComparison.OrdinalIgnoreCase)
        || column.Equals("created_at", StringComparison.OrdinalIgnoreCase)
        || column.Equals("createdAt", StringComparison.OrdinalIgnoreCase)
        || column.Equals("modified_at", StringComparison.OrdinalIgnoreCase)
        || column.Equals("modifiedAt", StringComparison.OrdinalIgnoreCase)
        || column.Equals("created_by", StringComparison.OrdinalIgnoreCase)
        || column.Equals("createdBy", StringComparison.OrdinalIgnoreCase)
        || column.Equals("modified_by", StringComparison.OrdinalIgnoreCase)
        || column.Equals("modifiedBy", StringComparison.OrdinalIgnoreCase)
        || column.Equals("is_deleted", StringComparison.OrdinalIgnoreCase)
        || column.Equals("isDeleted", StringComparison.OrdinalIgnoreCase)
        || column.Equals("today_task", StringComparison.OrdinalIgnoreCase)
        || column.Equals("todayTask", StringComparison.OrdinalIgnoreCase)
        || column.Equals("is_marked", StringComparison.OrdinalIgnoreCase)
        || column.Equals("isMarked", StringComparison.OrdinalIgnoreCase)
        || column.Equals("ValidFrom", StringComparison.OrdinalIgnoreCase)
        || column.Equals("ValidTo", StringComparison.OrdinalIgnoreCase);

    /// <summary>Maps legacy camelCase system names to Postgres snake_case physical columns.</summary>
    public static string? ToPhysicalSystemColumn(string column) =>
        column.Trim().ToLowerInvariant() switch
        {
            "itemid" or "item_id" => "item_id",
            "createdat" or "created_at" => "created_at",
            "modifiedat" or "modified_at" => "modified_at",
            "createdby" or "created_by" => "created_by",
            "modifiedby" or "modified_by" => "modified_by",
            "isdeleted" or "is_deleted" => "is_deleted",
            "todaytask" or "today_task" => "today_task",
            "ismarked" or "is_marked" => "is_marked",
            _ => null
        };

    public static async Task<bool> TableExistsAsync(
        NpgsqlConnection connection,
        string tableName,
        CancellationToken cancellationToken) =>
        await TableExistsAsync(connection, "dbo", tableName, cancellationToken);

    public static async Task<HashSet<string>> LoadTableColumnsAsync(
        NpgsqlConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = 'dbo' AND table_name = @TableName
            """;
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TableName", tableName);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            columns.Add(reader.GetString(0));
        return columns;
    }

    public static async Task<object> ResolveWFormIdParameterAsync(
        NpgsqlConnection connection,
        string formId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT data_type
            FROM information_schema.columns
            WHERE table_schema = 'dbo' AND table_name = 'wFormControl' AND column_name = 'wFormId'
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        var type = (await cmd.ExecuteScalarAsync(cancellationToken))?.ToString()?.ToLowerInvariant();
        if (type is "int" or "bigint" or "smallint" or "tinyint" or "integer")
        {
            if (int.TryParse(formId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                return n;
            var hex = new string(formId.Where(Uri.IsHexDigit).ToArray());
            if (hex.Length > SuffixLength)
                hex = hex[..SuffixLength];
            if (uint.TryParse(hex.PadLeft(SuffixLength, '0'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var u))
                return unchecked((int)u);
        }

        return formId;
    }

    public static async Task EnsureTryCastFunctionsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE SCHEMA IF NOT EXISTS dbo;

            CREATE OR REPLACE FUNCTION dbo.try_cast_numeric(p_text text) RETURNS numeric AS $body$
            BEGIN
                RETURN NULLIF(btrim(p_text), '')::numeric;
            EXCEPTION WHEN others THEN
                RETURN NULL;
            END;
            $body$ LANGUAGE plpgsql IMMUTABLE;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public static string CellString(object? value)
    {
        if (value is null or DBNull)
            return string.Empty;
        if (value is DateTime dt)
            return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        if (value is DateTimeOffset dto)
            return dto.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        if (value is byte[] bytes)
            return Encoding.UTF8.GetString(bytes);
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
