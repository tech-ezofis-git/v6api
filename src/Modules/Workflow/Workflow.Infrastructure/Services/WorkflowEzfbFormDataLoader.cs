using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;
using SaaSApp.Workflow.Application;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>Reads ezfb row field values as JSON (jsonId keys) for inbox display.</summary>
public sealed class WorkflowEzfbFormDataLoader : SaaSApp.Workflow.Application.Contracts.IWorkflowEzfbFormDataLoader
{
    // Physical (snake_case unquoted) system columns -- see FormService.cs's
    // EnsureFormEntryTableAsync doc comment for why (an earlier draft left these unquoted
    // camelCase, which Postgres silently folds to all-lowercase; fixed to genuine snake_case).
    private static readonly HashSet<string> SystemColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "item_id", "created_at", "modified_at", "created_by", "modified_by", "is_deleted", "today_task", "is_marked"
    };

    private readonly ITenantContext _tenantContext;

    public WorkflowEzfbFormDataLoader(ITenantContext tenantContext)
    {
        _tenantContext = tenantContext;
    }

    public async Task<string?> LoadFormDataJsonAsync(
        string formId,
        int formEntryId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(formId) || formEntryId <= 0)
            return null;

        var connectionString = _tenantContext.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return await LoadFormDataJsonAsync(connection, formId, formEntryId, cancellationToken);
    }

    internal static async Task<string?> LoadFormDataJsonAsync(
        NpgsqlConnection connection,
        string rawFormId,
        int formEntryId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawFormId) || formEntryId <= 0)
            return null;

        string tableSuffix;
        try
        {
            tableSuffix = FormIdNaming.GetEzfbTableSuffix(FormIdNaming.NormalizeFormId(rawFormId));
        }
        catch
        {
            return null;
        }

        var tableName = $"ezfb_{tableSuffix}_items";
        const string existsSql = "SELECT 1 FROM information_schema.tables WHERE table_schema = 'dbo' AND table_name = @TableName;";
        await using (var existsCmd = new NpgsqlCommand(existsSql, connection))
        {
            existsCmd.Parameters.AddWithValue("@TableName", tableName);
            var exists = await existsCmd.ExecuteScalarAsync(cancellationToken);
            if (exists == null || exists == DBNull.Value)
                return null;
        }

        var ezfbColumns = await LoadTableColumnsAsync(connection, tableName, cancellationToken);
        if (ezfbColumns.Count == 0)
            return null;

        var normalizedFormId = FormIdNaming.NormalizeFormId(rawFormId);
        var controls = await LoadFormControlJsonIdsAsync(connection, normalizedFormId, cancellationToken);

        var selectColumns = new List<string>();
        foreach (var jsonId in controls)
        {
            if (TryResolveEzfbColumn(jsonId, ezfbColumns, out var col)
                && !selectColumns.Contains(col, StringComparer.OrdinalIgnoreCase))
            {
                selectColumns.Add(col);
            }
        }

        if (selectColumns.Count == 0)
        {
            selectColumns = ezfbColumns
                .Where(c => !IsSystemColumn(c))
                .ToList();
        }

        if (selectColumns.Count == 0)
            return null;

        // Build JSON in C# — do NOT use row_to_json + ExecuteScalar so behavior mirrors the
        // original's "no silent truncation" intent (SQL Server's FOR JSON truncated at ~2033
        // chars, which dropped fields from inbox formData).
        // System columns are unquoted snake_case; custom per-field columns stay quoted with
        // their original (sanitized) casing, same as repository custom columns.
        var selectList = string.Join(", ", selectColumns.Select(c =>
            SystemColumns.Contains(c) ? c : $"\"{EscapeColumn(c)}\""));

        var dataSql = $@"
SELECT {selectList}
FROM dbo.""{tableName}""
WHERE item_id = @ItemId AND (is_deleted = false OR is_deleted IS NULL);";

        await using var dataCmd = new NpgsqlCommand(dataSql, connection);
        dataCmd.Parameters.AddWithValue("@ItemId", formEntryId);
        await using var reader = await dataCmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        using var stream = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                if (string.IsNullOrWhiteSpace(name) || IsSystemColumn(name))
                    continue;

                var value = reader.IsDBNull(i)
                    ? string.Empty
                    : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? string.Empty;
                writer.WriteString(name, value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool IsSystemColumn(string column) => SystemColumns.Contains(column);

    private static string EscapeColumn(string column) => column.Replace("\"", "\"\"");

    private static async Task<HashSet<string>> LoadTableColumnsAsync(
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

    private static async Task<List<string>> LoadFormControlJsonIdsAsync(
        NpgsqlConnection connection,
        string formId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT "jsonId"
            FROM dbo."wFormControl"
            WHERE "wFormId" = @FormId
              AND "isDeleted" = false
              AND "jsonId" IS NOT NULL
              AND TRIM("jsonId") <> ''
            """;
        var jsonIds = new List<string>();
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@FormId", formId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            jsonIds.Add(reader.GetString(0));
        return jsonIds;
    }

    private static bool TryResolveEzfbColumn(string jsonId, IReadOnlySet<string> ezfbColumns, out string column)
    {
        column = string.Empty;
        if (string.IsNullOrWhiteSpace(jsonId))
            return false;

        var trimmed = jsonId.Trim();
        if (ezfbColumns.Contains(trimmed))
        {
            column = trimmed;
            return true;
        }

        if (EzfbColumnNaming.TryToColumnName(trimmed, out var fromJsonId) && ezfbColumns.Contains(fromJsonId))
        {
            column = fromJsonId;
            return true;
        }

        if (EzfbColumnNaming.TryToColumnName(trimmed, out var baseName)
            && baseName.Length > 0
            && char.IsDigit(baseName[0]))
        {
            var legacy = "F_" + baseName;
            if (ezfbColumns.Contains(legacy))
            {
                column = legacy;
                return true;
            }
        }

        return false;
    }
}
