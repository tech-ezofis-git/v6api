using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;
using SaaSApp.Workflow.Application;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>
/// Reads ezfb row field values as JSON for inbox display. Old forms (jsonId-named columns) emit
/// jsonId keys, unchanged. New forms (Label-named columns) emit the Label as the key, plus a
/// jsonId alias, so both eras coexist without a table migration -- see EzfbColumnNaming.
/// </summary>
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
        var controls = await LoadFormControlsAsync(connection, normalizedFormId, cancellationToken);

        var selectColumns = new List<string>();
        // Physical column -> (jsonId, Name, how it matched), so the JSON key can be chosen per
        // column below: old-form (jsonId-named) columns keep emitting jsonId keys unchanged;
        // new-form (Label-named) columns emit the human Name as the primary key (plus a jsonId
        // alias, since it's cheap and helps a mixed/transitional FE).
        var columnMeta = new Dictionary<string, (string? JsonId, string? Name, EzfbColumnNaming.EzfbColumnMatchKind Kind)>(StringComparer.OrdinalIgnoreCase);
        foreach (var control in controls)
        {
            if (EzfbColumnNaming.TryResolveEzfbColumn(control.Name, control.JsonId, ezfbColumns, out var col, out var kind)
                && !selectColumns.Contains(col, StringComparer.OrdinalIgnoreCase))
            {
                selectColumns.Add(col);
                columnMeta[col] = (control.JsonId, control.Name, kind);
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
            var writtenKeys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var physicalColumn = reader.GetName(i);
                if (string.IsNullOrWhiteSpace(physicalColumn) || IsSystemColumn(physicalColumn))
                    continue;

                var value = reader.IsDBNull(i)
                    ? string.Empty
                    : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? string.Empty;

                var outputKey = physicalColumn;
                string? aliasKey = null;
                if (columnMeta.TryGetValue(physicalColumn, out var meta))
                {
                    if (meta.Kind is EzfbColumnNaming.EzfbColumnMatchKind.ExactName or EzfbColumnNaming.EzfbColumnMatchKind.SanitizedName)
                    {
                        // New-form column: emit the human Label as the key, jsonId as a cheap alias.
                        outputKey = !string.IsNullOrWhiteSpace(meta.Name) ? meta.Name!.Trim() : physicalColumn;
                        aliasKey = !string.IsNullOrWhiteSpace(meta.JsonId) ? meta.JsonId!.Trim() : null;
                    }
                    else
                    {
                        // Old-form column: unchanged behavior, key stays the jsonId.
                        outputKey = !string.IsNullOrWhiteSpace(meta.JsonId) ? meta.JsonId!.Trim() : physicalColumn;
                    }
                }

                if (writtenKeys.Add(outputKey))
                    writer.WriteString(outputKey, value);

                if (aliasKey != null && writtenKeys.Add(aliasKey))
                    writer.WriteString(aliasKey, value);
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

    private sealed record ControlIdAndName(string JsonId, string? Name);

    private static async Task<List<ControlIdAndName>> LoadFormControlsAsync(
        NpgsqlConnection connection,
        string formId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT "jsonId", name
            FROM dbo."wFormControl"
            WHERE "wFormId" = @FormId
              AND "isDeleted" = false
              AND "jsonId" IS NOT NULL
              AND TRIM("jsonId") <> ''
            """;
        var controls = new List<ControlIdAndName>();
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@FormId", formId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            controls.Add(new ControlIdAndName(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        return controls;
    }
}
