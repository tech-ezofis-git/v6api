using System.Globalization;
using System.Text.Json;
using Npgsql;
using Microsoft.Extensions.Logging;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Application.Forms;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>v5 PostformentryFn / GetformentryidFn parity for dbo.ezfb_{form}_items.</summary>
public sealed class FormEntryService : IFormEntryService
{
    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUserProvider _currentUserProvider;
    private readonly IWorkflowEzfbFormDataLoader _formDataLoader;
    private readonly ILogger<FormEntryService> _logger;

    public FormEntryService(
        ITenantContext tenantContext,
        ICurrentUserProvider currentUserProvider,
        IWorkflowEzfbFormDataLoader formDataLoader,
        ILogger<FormEntryService> logger)
    {
        _tenantContext = tenantContext;
        _currentUserProvider = currentUserProvider;
        _formDataLoader = formDataLoader;
        _logger = logger;
    }

    public Task<FormEntryResult> UpsertEntryAsync(
        string formId,
        Guid entryId,
        JsonElement body,
        CancellationToken cancellationToken = default)
    {
        var request = ParseUpsertRequest(body);
        return UpsertEntryAsync(formId, entryId, request, cancellationToken);
    }

    public async Task<FormEntryResult> UpsertEntryAsync(
        string formId,
        Guid entryId,
        FormEntryUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(formId))
            return Failed("form id is required.");

        var normalizedFormId = FormIdNaming.NormalizeFormId(formId);
        var fieldMap = NormalizeFields(request.Fields);
        if (fieldMap.Count == 0)
            return Failed("fields are required.");

        var connectionString = _tenantContext.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");
        var userId = _currentUserProvider.GetUserId()
            ?? throw new InvalidOperationException("Authenticated user is required.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var formMeta = await LoadFormMetadataAsync(connection, normalizedFormId, cancellationToken);
        if (formMeta == null)
            return Failed("form entry not found");

        var tableSuffix = FormIdNaming.GetEzfbTableSuffix(normalizedFormId);
        var tableName = $"ezfb_{tableSuffix}_items";
        if (!await EzfbTableExistsAsync(connection, tableName, cancellationToken))
            return Failed("form entry table not found");

        var ezfbColumns = await LoadTableColumnsAsync(connection, tableName, cancellationToken);
        if (ezfbColumns.Count == 0)
            return Failed("form entry table has no columns");

        var wFormIdValue = await ResolveWFormIdParameterAsync(normalizedFormId);
        var controls = await LoadFormControlsAsync(connection, wFormIdValue, cancellationToken);
        var resolvedFields = ResolveFieldColumns(fieldMap, controls, ezfbColumns);
        if (resolvedFields.Count == 0)
            return Failed("no matching ezfb columns for submitted fields");

        var duplicate = await CheckUniqueColumnsAsync(
            connection,
            tableName,
            formMeta.UniqueColumns,
            resolvedFields,
            entryId != Guid.Empty ? entryId : null,
            controls,
            cancellationToken);
        if (duplicate != null)
            return duplicate;

        var userStamp = userId.ToString("D");
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        if (entryId == Guid.Empty)
        {
            var newId = await InsertEntryAsync(
                connection,
                tableName,
                resolvedFields,
                now,
                userStamp,
                cancellationToken);
            if (newId == Guid.Empty)
                return Failed("form entry not found");

            return new FormEntryResult(1, newId.ToString("D"), "created new form entry");
        }

        if (!await EntryExistsAsync(connection, tableName, entryId, cancellationToken))
            return Failed("form entry not found");

        var updated = await UpdateEntryAsync(
            connection,
            tableName,
            entryId,
            resolvedFields,
            now,
            userStamp,
            cancellationToken);
        if (!updated)
            return Failed("form entry not found");

        return new FormEntryResult(
            2,
            entryId.ToString("D"),
            "updated form entry");
    }

    public async Task<FormEntryGetResult> GetEntriesAsync(
        string formId,
        string entryIds,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(formId) || string.IsNullOrWhiteSpace(entryIds))
            return new FormEntryGetResult(FormEntryGetStatus.NotFound, null);

        var ids = entryIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return new FormEntryGetResult(FormEntryGetStatus.NotFound, null);

        var normalizedFormId = FormIdNaming.NormalizeFormId(formId);
        var connectionString = _tenantContext.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var tableSuffix = FormIdNaming.GetEzfbTableSuffix(normalizedFormId);
        var tableName = $"ezfb_{tableSuffix}_items";
        if (!await EzfbTableExistsAsync(connection, tableName, cancellationToken))
            return new FormEntryGetResult(FormEntryGetStatus.NotFound, null);

        var ezfbColumns = await LoadTableColumnsAsync(connection, tableName, cancellationToken);
        var entries = new List<Dictionary<string, object?>>();

        foreach (var entryId in ids)
        {
            var row = await LoadEntryRowAsync(connection, tableName, ezfbColumns, entryId, cancellationToken);
            if (row == null)
                continue;

            var formDataJson = await _formDataLoader.LoadFormDataJsonAsync(normalizedFormId, entryId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(formDataJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(formDataJson);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in doc.RootElement.EnumerateObject())
                            row[prop.Name] = JsonElementToObject(prop.Value);
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogDebug(ex, "Could not merge jsonId form data for entry {EntryId}.", entryId);
                }
            }

            entries.Add(row);
        }

        return entries.Count == 0
            ? new FormEntryGetResult(FormEntryGetStatus.NotFound, null)
            : new FormEntryGetResult(FormEntryGetStatus.Found, entries);
    }

    public async Task<FormEntryAllResult> ListEntriesAsync(
        string formId,
        FormEntryAllRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(formId))
            return new FormEntryAllResult(FormEntryGetStatus.NotFound, formId, null, null);

        var normalizedFormId = FormIdNaming.NormalizeFormId(formId);
        var connectionString = _tenantContext.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        if (await LoadFormMetadataAsync(connection, normalizedFormId, cancellationToken) == null)
            return new FormEntryAllResult(FormEntryGetStatus.NotFound, normalizedFormId, null, null);

        var tableSuffix = FormIdNaming.GetEzfbTableSuffix(normalizedFormId);
        var tableName = $"ezfb_{tableSuffix}_items";
        if (!await EzfbTableExistsAsync(connection, tableName, cancellationToken))
            return new FormEntryAllResult(FormEntryGetStatus.NotFound, normalizedFormId, null, null);

        var ezfbColumns = await LoadTableColumnsAsync(connection, tableName, cancellationToken);
        if (ezfbColumns.Count == 0)
            return new FormEntryAllResult(FormEntryGetStatus.NotFound, normalizedFormId, null, null);

        var page = request.CurrentPage <= 0 ? 1 : request.CurrentPage;
        var pageSize = request.ItemsPerPage < 0 ? 0 : request.ItemsPerPage;
        var skip = (page - 1) * (pageSize == 0 ? int.MaxValue : pageSize);
        var mode = (request.Mode ?? "browse").Trim().ToLowerInvariant();
        var includeDeleted = mode is "recyclebin" or "recycle" or "deleted";

        var sortColumn = MapEntrySortColumn(request.SortBy?.Criteria, ezfbColumns);
        var sortOrder = string.Equals(request.SortBy?.Order, "ASC", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";

        var whereParts = new List<string>
        {
            includeDeleted ? "(is_deleted = true)" : "(is_deleted = false OR is_deleted IS NULL)"
        };
        var parameters = new List<NpgsqlParameter>();

        foreach (var filter in (request.FilterBy ?? new List<FormAllFilterGroup>())
                     .SelectMany(g => g.Filters ?? new List<FormAllFilter>()))
        {
            if (TryBuildEntryFilterCondition(filter, ezfbColumns, out var condition, out var filterParam))
            {
                whereParts.Add(condition);
                if (filterParam != null)
                    parameters.Add(filterParam);
            }
        }

        var whereSql = string.Join(" AND ", whereParts);
        var countSql = $"""SELECT COUNT(1) FROM dbo.{tableName} WHERE {whereSql};""";
        var selectColumns = ezfbColumns
            .Select(c => $"\"{EscapeColumn(c)}\"")
            .ToList();
        var selectSql = $"""
            SELECT {string.Join(", ", selectColumns)}
            FROM dbo.{tableName}
            WHERE {whereSql}
            ORDER BY {sortColumn} {sortOrder}
            OFFSET @Skip ROWS
            FETCH NEXT @Take ROWS ONLY;
            """;

        int totalItems;
        await using (var countCmd = new NpgsqlCommand(countSql, connection))
        {
            foreach (var p in parameters)
                countCmd.Parameters.Add(new NpgsqlParameter(p.ParameterName, p.Value));
            totalItems = Convert.ToInt32(await countCmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }

        var entries = new List<Dictionary<string, object?>>();
        await using (var listCmd = new NpgsqlCommand(selectSql, connection))
        {
            foreach (var p in parameters)
                listCmd.Parameters.Add(p);
            listCmd.Parameters.AddWithValue("@Skip", Math.Max(skip, 0));
            listCmd.Parameters.AddWithValue("@Take", pageSize == 0 ? Math.Max(totalItems, 1) : pageSize);
            await using var reader = await listCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var name = reader.GetName(i);
                    row[name] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }

                entries.Add(row);
            }
        }

        if (request.IncludeFormJson && entries.Count > 0)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                if (!entries[i].TryGetValue("item_id", out var itemObj) || itemObj == null)
                    continue;

                var entryId = itemObj switch
                {
                    Guid guid => guid,
                    string text when Guid.TryParse(text, out var parsed) => parsed,
                    _ => Guid.Empty
                };
                if (entryId == Guid.Empty)
                    continue;

                var formDataJson = await _formDataLoader.LoadFormDataJsonAsync(normalizedFormId, entryId, cancellationToken);
                if (string.IsNullOrWhiteSpace(formDataJson))
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(formDataJson);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in doc.RootElement.EnumerateObject())
                            entries[i][prop.Name] = JsonElementToObject(prop.Value);
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogDebug(ex, "Could not merge jsonId form data for entry {EntryId}.", entryId);
                }
            }
        }

        return new FormEntryAllResult(
            FormEntryGetStatus.Found,
            normalizedFormId,
            entries,
            new FormAllMeta(page, pageSize, totalItems));
    }

    private static string MapEntrySortColumn(string? criteria, IReadOnlySet<string> ezfbColumns)
    {
        var c = (criteria ?? "modified_at").Trim();
        if (ezfbColumns.Contains(c))
            return $"\"{EscapeColumn(c)}\"";

        var lower = c.ToLowerInvariant();
        return lower switch
        {
            "itemid" or "id" or "item_id" when ezfbColumns.Contains("item_id") => "\"item_id\"",
            "createdat" or "created_at" when ezfbColumns.Contains("created_at") => "\"created_at\"",
            "modifiedat" or "modified_at" when ezfbColumns.Contains("modified_at") => "\"modified_at\"",
            _ => ezfbColumns.Contains("modified_at")
                ? "\"modified_at\""
                : "\"item_id\""
        };
    }

    private static bool TryBuildEntryFilterCondition(
        FormAllFilter filter,
        IReadOnlySet<string> ezfbColumns,
        out string conditionSql,
        out NpgsqlParameter? parameter)
    {
        conditionSql = string.Empty;
        parameter = null;
        if (filter == null || string.IsNullOrWhiteSpace(filter.Criteria) || string.IsNullOrWhiteSpace(filter.Condition))
            return false;

        var criteria = filter.Criteria.Trim();
        string? column = null;
        foreach (var col in ezfbColumns)
        {
            if (string.Equals(col, criteria, StringComparison.OrdinalIgnoreCase))
            {
                column = col;
                break;
            }
        }

        if (column == null)
            return false;

        var escaped = EscapeColumn(column);
        var paramName = $"@e_{Math.Abs((filter.Criteria + filter.Condition + filter.Value).GetHashCode())}";
        var cond = filter.Condition.Trim().ToLowerInvariant();
        if (cond is "contains" or "like")
        {
            conditionSql = $"\"{escaped}\" LIKE {paramName}";
            parameter = new NpgsqlParameter(paramName, $"%{filter.Value ?? string.Empty}%");
            return true;
        }

        if (cond is "eq" or "=" or "equal")
        {
            conditionSql = $"\"{escaped}\" = {paramName}";
            parameter = new NpgsqlParameter(paramName, filter.Value ?? string.Empty);
            return true;
        }

        if (cond is "neq" or "!=" or "notequal")
        {
            conditionSql = $"\"{escaped}\" <> {paramName}";
            parameter = new NpgsqlParameter(paramName, filter.Value ?? string.Empty);
            return true;
        }

        return false;
    }

    private static FormEntryUpsertRequest ParseUpsertRequest(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return new FormEntryUpsertRequest();

        if (!body.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
            return new FormEntryUpsertRequest();

        var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in fields.EnumerateObject())
            dict[prop.Name] = prop.Value;

        return new FormEntryUpsertRequest { Fields = dict };
    }

    private static Dictionary<string, string> NormalizeFields(Dictionary<string, JsonElement>? fields)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (fields == null)
            return map;

        foreach (var (key, value) in fields)
        {
            var text = JsonElementToStorageString(value);
            if (!string.IsNullOrWhiteSpace(text))
                map[key.Trim()] = text;
        }

        return map;
    }

    private static string? JsonElementToStorageString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Array or JsonValueKind.Object => value.GetRawText(),
        _ => value.GetRawText()
    };

    private static object? JsonElementToObject(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var l) ? l : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array or JsonValueKind.Object => JsonSerializer.Deserialize<object>(value.GetRawText()),
        _ => value.GetRawText()
    };

    private static FormEntryResult Failed(string message) =>
        new(0, null, message);

    private static async Task<FormMetadata?> LoadFormMetadataAsync(
        NpgsqlConnection connection,
        string formId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT "uniqueColumns"
            FROM dbo."wForm"
            WHERE id = @FormId AND "isDeleted" = false
            LIMIT 1;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@FormId", formId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var uniqueColumns = reader.IsDBNull(0) ? null : reader.GetString(0);
        return new FormMetadata(uniqueColumns);
    }

    private sealed record FormMetadata(string? UniqueColumns);

    private sealed record FormControlRow(string JsonId, string? Name, string? Type, string? ColumnName = null);

    private static List<KeyValuePair<string, string>> ResolveFieldColumns(
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyList<FormControlRow> controls,
        IReadOnlySet<string> ezfbColumns)
    {
        var resolved = new List<KeyValuePair<string, string>>();
        foreach (var (key, value) in fields)
        {
            if (TryResolveControlForField(key, controls, out var control) && control is not null)
            {
                if (EzfbColumnNaming.TryResolveEzfbColumn(
                        control.ColumnName, control.Name, control.JsonId, ezfbColumns, out var col))
                    resolved.Add(new KeyValuePair<string, string>(col, value));
                continue;
            }

            // No wFormControl match: the incoming key could itself be a Label (new forms) or a
            // jsonId (old forms) -- try it both ways.
            if (EzfbColumnNaming.TryResolveEzfbColumn(key, key, ezfbColumns, out var colFromKey))
                resolved.Add(new KeyValuePair<string, string>(colFromKey, value));
        }

        return resolved;
    }

    private static async Task<FormEntryResult?> CheckUniqueColumnsAsync(
        NpgsqlConnection connection,
        string tableName,
        string? uniqueColumns,
        IReadOnlyList<KeyValuePair<string, string>> resolvedFields,
        Guid? excludeEntryId,
        IReadOnlyList<FormControlRow> controls,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(uniqueColumns))
            return null;

        var fieldByColumn = resolvedFields.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var duplicates = new List<FormEntryDuplicateField>();

        foreach (var rawColumn in uniqueColumns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!EzfbColumnNaming.TryResolveEzfbColumn(rawColumn, rawColumn, fieldByColumn.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase), out var ezfbCol))
            {
                if (!fieldByColumn.ContainsKey(rawColumn))
                    continue;
                ezfbCol = rawColumn;
            }

            if (!fieldByColumn.TryGetValue(ezfbCol, out var fieldValue))
                continue;

            var existingId = await FindDuplicateEntryIdAsync(
                connection,
                tableName,
                ezfbCol,
                fieldValue,
                excludeEntryId,
                cancellationToken);
            if (existingId == null)
                continue;

            var controlName = controls
                .FirstOrDefault(c => string.Equals(c.JsonId, rawColumn, StringComparison.OrdinalIgnoreCase))
                ?.Name;
            duplicates.Add(new FormEntryDuplicateField(rawColumn, controlName, fieldValue));
        }

        if (duplicates.Count == 0)
            return null;

        return new FormEntryResult(
            3,
            JsonSerializer.Serialize(duplicates),
            "already existing form entry");
    }

    /// <summary>
    /// PHASE 4: SQL Server used ISJSON()/JSON_VALUE() inline; Postgres has no exception-safe inline
    /// JSON parse, so a reusable dbo.try_json_email() PL/pgSQL wrapper (same TryCast pattern as
    /// WorkflowTicketSearchService.cs's dbo.try_cast_timestamp/numeric) replaces the two-step check.
    /// </summary>
    private static async Task EnsureTryJsonEmailFunctionAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE OR REPLACE FUNCTION dbo.try_json_email(p_text text) RETURNS text AS $body$
            BEGIN
                RETURN p_text::jsonb ->> 'email';
            EXCEPTION WHEN OTHERS THEN
                RETURN NULL;
            END;
            $body$ LANGUAGE plpgsql IMMUTABLE;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Guid?> FindDuplicateEntryIdAsync(
        NpgsqlConnection connection,
        string tableName,
        string column,
        string fieldValue,
        Guid? excludeEntryId,
        CancellationToken cancellationToken)
    {
        var escapedCol = EscapeColumn(column);
        string sql;
        if (string.IsNullOrWhiteSpace(fieldValue))
        {
            sql = $"""
                SELECT item_id
                FROM dbo.{tableName}
                WHERE "{escapedCol}" = '' AND "{escapedCol}" <> ''
                  AND (is_deleted = false OR is_deleted IS NULL)
                """;
        }
        else
        {
            await EnsureTryJsonEmailFunctionAsync(connection, cancellationToken);
            sql = $"""
                SELECT item_id
                FROM dbo.{tableName}
                WHERE (
                    "{escapedCol}" = @Value
                    OR dbo.try_json_email("{escapedCol}") = @Value
                )
                  AND (is_deleted = false OR is_deleted IS NULL)
                """;
        }

        if (excludeEntryId is { } excluded && excluded != Guid.Empty)
            sql += " AND item_id <> @ExcludeId";
        sql += " LIMIT 1;";

        await using var cmd = new NpgsqlCommand(sql, connection);
        if (!string.IsNullOrWhiteSpace(fieldValue))
            cmd.Parameters.AddWithValue("@Value", fieldValue);
        if (excludeEntryId is { } excludedId && excludedId != Guid.Empty)
            cmd.Parameters.AddWithValue("@ExcludeId", excludedId);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        if (result == null || result == DBNull.Value)
            return null;

        return (Guid)result;
    }

    private static async Task<Guid> InsertEntryAsync(
        NpgsqlConnection connection,
        string tableName,
        IReadOnlyList<KeyValuePair<string, string>> resolvedFields,
        string createdAt,
        string createdBy,
        CancellationToken cancellationToken)
    {
        var columns = resolvedFields.Select(f => $"\"{EscapeColumn(f.Key)}\"").ToList();
        columns.Add("created_at");
        columns.Add("created_by");
        columns.Add("is_deleted");

        var values = resolvedFields.Select((_, i) => $"@V{i}").ToList();
        values.Add("@CreatedAt");
        values.Add("@CreatedBy");
        values.Add("false");

        var sql = $"""
            INSERT INTO dbo.{tableName} ({string.Join(", ", columns)})
            VALUES ({string.Join(", ", values)})
            RETURNING item_id;
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        for (var i = 0; i < resolvedFields.Count; i++)
            cmd.Parameters.AddWithValue($"@V{i}", resolvedFields[i].Value);
        cmd.Parameters.AddWithValue("@CreatedAt", createdAt);
        cmd.Parameters.AddWithValue("@CreatedBy", createdBy);

        return (Guid)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<bool> UpdateEntryAsync(
        NpgsqlConnection connection,
        string tableName,
        Guid entryId,
        IReadOnlyList<KeyValuePair<string, string>> resolvedFields,
        string modifiedAt,
        string modifiedBy,
        CancellationToken cancellationToken)
    {
        var sets = resolvedFields
            .Select((f, i) => $"\"{EscapeColumn(f.Key)}\" = @V{i}")
            .ToList();
        sets.Add("modified_at = @ModifiedAt");
        sets.Add("modified_by = @ModifiedBy");

        var sql = $"""
            UPDATE dbo.{tableName}
            SET {string.Join(", ", sets)}
            WHERE item_id = @ItemId AND (is_deleted = false OR is_deleted IS NULL)
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        for (var i = 0; i < resolvedFields.Count; i++)
            cmd.Parameters.AddWithValue($"@V{i}", resolvedFields[i].Value);
        cmd.Parameters.AddWithValue("@ModifiedAt", modifiedAt);
        cmd.Parameters.AddWithValue("@ModifiedBy", modifiedBy);
        cmd.Parameters.AddWithValue("@ItemId", entryId);

        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static async Task<bool> EntryExistsAsync(
        NpgsqlConnection connection,
        string tableName,
        Guid entryId,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT COUNT(1)
            FROM dbo.{tableName}
            WHERE item_id = @ItemId AND (is_deleted = false OR is_deleted IS NULL)
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@ItemId", entryId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<Dictionary<string, object?>?> LoadEntryRowAsync(
        NpgsqlConnection connection,
        string tableName,
        IReadOnlySet<string> ezfbColumns,
        Guid entryId,
        CancellationToken cancellationToken)
    {
        var selectColumns = ezfbColumns
            .Where(c => !IsSystemColumn(c) || c.Equals("item_id", StringComparison.OrdinalIgnoreCase))
            .Select(c => $"\"{EscapeColumn(c)}\"")
            .ToList();
        if (selectColumns.Count == 0)
            return null;

        var sql = $"""
            SELECT {string.Join(", ", selectColumns)}
            FROM dbo.{tableName}
            WHERE item_id = @ItemId AND (is_deleted = false OR is_deleted IS NULL)
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@ItemId", entryId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            row[name] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        }

        return row;
    }

    private static bool IsSystemColumn(string column) =>
        column.Equals("item_id", StringComparison.OrdinalIgnoreCase)
        || column.Equals("created_at", StringComparison.OrdinalIgnoreCase)
        || column.Equals("modified_at", StringComparison.OrdinalIgnoreCase)
        || column.Equals("created_by", StringComparison.OrdinalIgnoreCase)
        || column.Equals("modified_by", StringComparison.OrdinalIgnoreCase)
        || column.Equals("is_deleted", StringComparison.OrdinalIgnoreCase)
        || column.Equals("today_task", StringComparison.OrdinalIgnoreCase)
        || column.Equals("is_marked", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> EzfbTableExistsAsync(
        NpgsqlConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(1)
            FROM information_schema.tables
            WHERE table_schema = 'dbo' AND table_name = @TableName
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TableName", tableName);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0;
    }

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

    private static async Task<List<FormControlRow>> LoadFormControlsAsync(
        NpgsqlConnection connection,
        string wFormIdValue,
        CancellationToken cancellationToken)
    {
        await using (var alterCmd = new NpgsqlCommand(
            """ALTER TABLE dbo."wFormControl" ADD COLUMN IF NOT EXISTS "columnName" varchar(200) NULL;""",
            connection))
            await alterCmd.ExecuteNonQueryAsync(cancellationToken);

        const string sql = """
            SELECT "jsonId", name, type, "columnName"
            FROM dbo."wFormControl"
            WHERE "wFormId" = @FormId
              AND "isDeleted" = false
              AND "jsonId" IS NOT NULL
              AND TRIM("jsonId") <> ''
            """;
        var rows = new List<FormControlRow>();
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@FormId", wFormIdValue);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new FormControlRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return rows;
    }

    /// <summary>
    /// PHASE 4: dbo."wFormControl"."wFormId" is always varchar(64) on Postgres (see
    /// FormService.cs's CREATE TABLE) — there is no INT-typed legacy shape to detect the way SQL
    /// Server had, so the numeric/hex-id-parsing branch was dropped rather than translated. Same
    /// precedent as FormService.cs / other ResolveWFormIdParameterAsync call sites.
    /// </summary>
    private static Task<string> ResolveWFormIdParameterAsync(string formId) => Task.FromResult(formId);

    private static bool TryResolveControlForField(
        string fieldKey,
        IReadOnlyList<FormControlRow> controls,
        out FormControlRow? control)
    {
        control = null;
        if (string.IsNullOrWhiteSpace(fieldKey))
            return false;

        var key = fieldKey.Trim();
        foreach (var row in controls)
        {
            if (!string.IsNullOrWhiteSpace(row.Name)
                && string.Equals(row.Name.Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                control = row;
                return true;
            }
        }

        foreach (var row in controls)
        {
            if (string.Equals(row.JsonId, key, StringComparison.OrdinalIgnoreCase))
            {
                control = row;
                return true;
            }

            if (EzfbColumnNaming.TryToColumnName(row.JsonId, out var ezfbCol)
                && string.Equals(ezfbCol, key, StringComparison.OrdinalIgnoreCase))
            {
                control = row;
                return true;
            }
        }

        return false;
    }

    private static string EscapeColumn(string column) => column.Replace("\"", "\"\"", StringComparison.Ordinal);

    public async Task<FormControlDistinctValuesResult> GetDistinctControlValuesAsync(
        string wFormId,
        string wFormControlName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(wFormId) || string.IsNullOrWhiteSpace(wFormControlName))
            return new FormControlDistinctValuesResult(FormControlDistinctValuesStatus.FormNotFound, wFormId, wFormControlName, null, null, null);

        var normalizedFormId = FormIdNaming.NormalizeFormId(wFormId);
        var connectionString = _tenantContext.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var formMeta = await LoadFormMetadataAsync(connection, normalizedFormId, cancellationToken);
        if (formMeta == null)
            return new FormControlDistinctValuesResult(FormControlDistinctValuesStatus.FormNotFound, normalizedFormId, wFormControlName, null, null, null);

        var wFormIdValue = await ResolveWFormIdParameterAsync(normalizedFormId);
        var controls = await LoadFormControlsAsync(connection, wFormIdValue, cancellationToken);

        var control = controls.FirstOrDefault(c =>
            !string.IsNullOrWhiteSpace(c.Name) &&
            string.Equals(c.Name.Trim(), wFormControlName.Trim(), StringComparison.OrdinalIgnoreCase));

        if (control == null)
            return new FormControlDistinctValuesResult(FormControlDistinctValuesStatus.ControlNotFound, normalizedFormId, wFormControlName, null, null, null);

        var tableSuffix = FormIdNaming.GetEzfbTableSuffix(normalizedFormId);
        var tableName = $"ezfb_{tableSuffix}_items";
        if (!await EzfbTableExistsAsync(connection, tableName, cancellationToken))
            return new FormControlDistinctValuesResult(FormControlDistinctValuesStatus.TableNotFound, normalizedFormId, wFormControlName, control.JsonId, null, null);

        var ezfbColumns = await LoadTableColumnsAsync(connection, tableName, cancellationToken);
        if (!EzfbColumnNaming.TryResolveEzfbColumn(
                control.ColumnName, control.Name, control.JsonId, ezfbColumns, out var resolvedColumn))
            return new FormControlDistinctValuesResult(FormControlDistinctValuesStatus.ColumnNotFound, normalizedFormId, wFormControlName, control.JsonId, null, null);

        var values = await LoadDistinctColumnValuesAsync(connection, tableName, resolvedColumn, cancellationToken);
        return new FormControlDistinctValuesResult(
            FormControlDistinctValuesStatus.Found,
            normalizedFormId,
            wFormControlName.Trim(),
            control.JsonId,
            resolvedColumn,
            values);
    }

    private static async Task<List<string>> LoadDistinctColumnValuesAsync(
        NpgsqlConnection connection,
        string tableName,
        string column,
        CancellationToken cancellationToken)
    {
        var escaped = EscapeColumn(column);
        var sql = $"""
            SELECT DISTINCT "{escaped}"
            FROM dbo.{tableName}
            WHERE (is_deleted = false OR is_deleted IS NULL)
              AND "{escaped}" IS NOT NULL
              AND TRIM(CAST("{escaped}" AS text)) <> ''
            ORDER BY "{escaped}"
            """;

        var values = new List<string>();
        await using var cmd = new NpgsqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var val = reader.IsDBNull(0) ? null : reader.GetValue(0)?.ToString();
            if (!string.IsNullOrWhiteSpace(val))
                values.Add(val);
        }

        return values;
    }
}
