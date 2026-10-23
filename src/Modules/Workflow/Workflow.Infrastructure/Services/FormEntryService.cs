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
        await EnsureMissingControlColumnsAsync(
            connection,
            tableName,
            tableSuffix,
            wFormIdValue,
            controls,
            ezfbColumns,
            cancellationToken);

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
            ezfbColumns,
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

    public async Task<FormEntryByColumnResult> GetEntriesByColumnAsync(
        string formId,
        string? columnName,
        string? columnValue = null,
        IReadOnlyList<FormEntryColumnFilter>? filters = null,
        string? columnId = null,
        CancellationToken cancellationToken = default)
    {
        var lookupKey = FirstNonBlank(columnName, columnId);
        if (string.IsNullOrWhiteSpace(formId))
            return InvalidByColumn(formId, lookupKey, "form id is required.");
        if (string.IsNullOrWhiteSpace(lookupKey))
            return InvalidByColumn(formId, lookupKey, "columnName or columnId is required.");

        var normalizedFormId = FormIdNaming.NormalizeFormId(formId);
        var connectionString = _tenantContext.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        if (await LoadFormMetadataAsync(connection, normalizedFormId, cancellationToken) == null)
            return NotFoundByColumn(normalizedFormId, lookupKey, "form not found.");

        var tableSuffix = FormIdNaming.GetEzfbTableSuffix(normalizedFormId);
        var tableName = $"ezfb_{tableSuffix}_items";
        if (!await EzfbTableExistsAsync(connection, tableName, cancellationToken))
            return NotFoundByColumn(normalizedFormId, lookupKey, "form entry table not found.");

        var ezfbColumns = await LoadTableColumnsAsync(connection, tableName, cancellationToken);
        if (ezfbColumns.Count == 0)
            return NotFoundByColumn(normalizedFormId, lookupKey, "form entry table has no columns.");

        var wFormIdValue = await ResolveWFormIdParameterAsync(normalizedFormId);
        var controls = await LoadFormControlsAsync(connection, wFormIdValue, cancellationToken);

        if (!TryResolveLookupColumn(lookupKey, controls, ezfbColumns, out var resolvedColumn))
            return InvalidByColumn(normalizedFormId, lookupKey, $"column '{lookupKey}' was not found on this form.");

        if (!TryResolveColumnFilters(filters, controls, ezfbColumns, out var resolvedFilters, out var filterError))
            return InvalidByColumn(normalizedFormId, lookupKey, filterError ?? "invalid filters.", resolvedColumn);

        // Column only (optional filters) → distinct non-empty values for that column.
        if (string.IsNullOrWhiteSpace(columnValue))
        {
            var values = await LoadDistinctColumnValuesAsync(
                connection, tableName, resolvedColumn, resolvedFilters, cancellationToken);
            return new FormEntryByColumnResult(
                FormEntryByColumnStatus.Found,
                null,
                normalizedFormId,
                lookupKey,
                resolvedColumn,
                Entries: null,
                UpdatedCount: 0,
                Values: values);
        }

        var rows = await LoadEntriesByColumnValueAsync(
            connection, tableName, ezfbColumns, resolvedColumn, columnValue, resolvedFilters, cancellationToken);
        if (rows.Count == 0)
            return NotFoundByColumn(normalizedFormId, lookupKey, "no form entries match the given column value.", resolvedColumn);

        await MergeFormDataJsonAsync(rows, normalizedFormId, cancellationToken);

        return new FormEntryByColumnResult(
            FormEntryByColumnStatus.Found,
            null,
            normalizedFormId,
            lookupKey,
            resolvedColumn,
            rows);
    }

    public async Task<FormEntryByColumnResult> UpsertEntryByColumnsAsync(
        string formId,
        FormEntryUpsertByColumnsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(formId))
            return InvalidByColumn(formId, null, "form id is required.");

        var fieldMap = NormalizeFields(request.Fields);
        if (fieldMap.Count == 0)
            return InvalidByColumn(formId, null, "fields are required (column name → value).");

        var normalizedFormId = FormIdNaming.NormalizeFormId(formId);
        var connectionString = _tenantContext.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");
        var userId = _currentUserProvider.GetUserId()
            ?? throw new InvalidOperationException("Authenticated user is required.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var formMeta = await LoadFormMetadataAsync(connection, normalizedFormId, cancellationToken);
        if (formMeta == null)
            return NotFoundByColumn(normalizedFormId, null, "form not found.");

        var tableSuffix = FormIdNaming.GetEzfbTableSuffix(normalizedFormId);
        var tableName = $"ezfb_{tableSuffix}_items";
        if (!await EzfbTableExistsAsync(connection, tableName, cancellationToken))
            return NotFoundByColumn(normalizedFormId, null, "form entry table not found.");

        var ezfbColumns = await LoadTableColumnsAsync(connection, tableName, cancellationToken);
        if (ezfbColumns.Count == 0)
            return NotFoundByColumn(normalizedFormId, null, "form entry table has no columns.");

        var wFormIdValue = await ResolveWFormIdParameterAsync(normalizedFormId);
        var controls = await LoadFormControlsAsync(connection, wFormIdValue, cancellationToken);

        if (!TryBuildMatchFilters(
                request,
                fieldMap,
                controls,
                ezfbColumns,
                out var matchFilters,
                out var matchKey,
                out var matchError))
            return InvalidByColumn(normalizedFormId, matchKey, matchError ?? "invalid match.");

        Guid entryId = Guid.Empty;
        var isUpdate = false;
        if (matchFilters.Count > 0)
        {
            var matchingIds = await FindEntryIdsByFiltersAsync(
                connection, tableName, matchFilters, cancellationToken);
            if (matchingIds.Count > 1)
            {
                return InvalidByColumn(
                    normalizedFormId,
                    matchKey,
                    $"{matchingIds.Count} form entries match the given key; expected at most one to update unambiguously.");
            }

            if (matchingIds.Count == 1)
            {
                entryId = matchingIds[0];
                isUpdate = true;
            }
        }

        await EnsureMissingControlColumnsAsync(
            connection,
            tableName,
            tableSuffix,
            wFormIdValue,
            controls,
            ezfbColumns,
            cancellationToken);

        var resolvedFields = ResolveFieldColumns(fieldMap, controls, ezfbColumns);
        if (resolvedFields.Count == 0)
            return InvalidByColumn(normalizedFormId, matchKey, "no matching ezfb columns for submitted fields.");

        var duplicate = await CheckUniqueColumnsAsync(
            connection,
            tableName,
            formMeta.UniqueColumns,
            resolvedFields,
            isUpdate ? entryId : null,
            controls,
            cancellationToken);
        if (duplicate != null)
            return InvalidByColumn(normalizedFormId, matchKey, duplicate.Output ?? "duplicate value for a unique column.");

        var userStamp = userId.ToString("D");
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        if (isUpdate)
        {
            var updated = await UpdateEntryAsync(
                connection, tableName, entryId, resolvedFields, now, userStamp, ezfbColumns, cancellationToken);
            if (!updated)
                return NotFoundByColumn(normalizedFormId, matchKey, "form entry not found.");
        }
        else
        {
            entryId = await InsertEntryAsync(
                connection, tableName, resolvedFields, now, userStamp, cancellationToken);
            if (entryId == Guid.Empty)
                return NotFoundByColumn(normalizedFormId, matchKey, "form entry not found.");
        }

        var row = await LoadEntryRowAsync(connection, tableName, ezfbColumns, entryId, cancellationToken);
        var rows = row != null
            ? new List<Dictionary<string, object?>> { row }
            : new List<Dictionary<string, object?>>();
        if (rows.Count > 0)
            await MergeFormDataJsonAsync(rows, normalizedFormId, cancellationToken);

        return new FormEntryByColumnResult(
            FormEntryByColumnStatus.Found,
            isUpdate ? "updated form entry" : "created form entry",
            normalizedFormId,
            matchKey,
            ResolvedColumn: null,
            rows,
            UpdatedCount: isUpdate ? 1 : 0);
    }

    public async Task<FormEntryByColumnResult> UpdateEntriesByColumnAsync(
        string formId,
        string columnName,
        string columnValue,
        FormEntryUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(formId))
            return InvalidByColumn(formId, columnName, "form id is required.");
        if (string.IsNullOrWhiteSpace(columnName))
            return InvalidByColumn(formId, columnName, "column name is required.");
        if (string.IsNullOrWhiteSpace(columnValue))
            return InvalidByColumn(formId, columnName, "column value is required.");

        var fieldMap = NormalizeFields(request.Fields);
        if (fieldMap.Count == 0)
            return InvalidByColumn(formId, columnName, "fields are required.");

        var normalizedFormId = FormIdNaming.NormalizeFormId(formId);
        var connectionString = _tenantContext.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");
        var userId = _currentUserProvider.GetUserId()
            ?? throw new InvalidOperationException("Authenticated user is required.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var formMeta = await LoadFormMetadataAsync(connection, normalizedFormId, cancellationToken);
        if (formMeta == null)
            return NotFoundByColumn(normalizedFormId, columnName, "form not found.");

        var tableSuffix = FormIdNaming.GetEzfbTableSuffix(normalizedFormId);
        var tableName = $"ezfb_{tableSuffix}_items";
        if (!await EzfbTableExistsAsync(connection, tableName, cancellationToken))
            return NotFoundByColumn(normalizedFormId, columnName, "form entry table not found.");

        var ezfbColumns = await LoadTableColumnsAsync(connection, tableName, cancellationToken);
        if (ezfbColumns.Count == 0)
            return NotFoundByColumn(normalizedFormId, columnName, "form entry table has no columns.");

        var wFormIdValue = await ResolveWFormIdParameterAsync(normalizedFormId);
        var controls = await LoadFormControlsAsync(connection, wFormIdValue, cancellationToken);

        if (!TryResolveLookupColumn(columnName, controls, ezfbColumns, out var resolvedColumn))
            return InvalidByColumn(normalizedFormId, columnName, $"column '{columnName}' was not found on this form.");

        var matchingIds = await FindEntryIdsByColumnValueAsync(
            connection, tableName, resolvedColumn, columnValue, cancellationToken);
        if (matchingIds.Count == 0)
            return NotFoundByColumn(normalizedFormId, columnName, "no form entries match the given column value.", resolvedColumn);
        if (matchingIds.Count > 1)
            return InvalidByColumn(
                normalizedFormId,
                columnName,
                $"{matchingIds.Count} form entries match '{columnName}' = '{columnValue}'; expected exactly one to update unambiguously.",
                resolvedColumn);

        var entryId = matchingIds[0];

        await EnsureMissingControlColumnsAsync(
            connection,
            tableName,
            tableSuffix,
            wFormIdValue,
            controls,
            ezfbColumns,
            cancellationToken);

        var resolvedFields = ResolveFieldColumns(fieldMap, controls, ezfbColumns);
        if (resolvedFields.Count == 0)
            return InvalidByColumn(normalizedFormId, columnName, "no matching ezfb columns for submitted fields.", resolvedColumn);

        var duplicate = await CheckUniqueColumnsAsync(
            connection,
            tableName,
            formMeta.UniqueColumns,
            resolvedFields,
            entryId,
            controls,
            cancellationToken);
        if (duplicate != null)
            return InvalidByColumn(normalizedFormId, columnName, duplicate.Output ?? "duplicate value for a unique column.", resolvedColumn);

        var userStamp = userId.ToString("D");
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        var updated = await UpdateEntryAsync(
            connection, tableName, entryId, resolvedFields, now, userStamp, ezfbColumns, cancellationToken);
        if (!updated)
            return NotFoundByColumn(normalizedFormId, columnName, "form entry not found.", resolvedColumn);

        var row = await LoadEntryRowAsync(connection, tableName, ezfbColumns, entryId, cancellationToken);
        var rows = row != null
            ? new List<Dictionary<string, object?>> { row }
            : new List<Dictionary<string, object?>>();
        if (rows.Count > 0)
            await MergeFormDataJsonAsync(rows, normalizedFormId, cancellationToken);

        return new FormEntryByColumnResult(
            FormEntryByColumnStatus.Found,
            "updated form entry",
            normalizedFormId,
            columnName,
            resolvedColumn,
            rows,
            UpdatedCount: 1);
    }

    private static FormEntryByColumnResult InvalidByColumn(string? formId, string? columnName, string message, string? resolvedColumn = null) =>
        new(FormEntryByColumnStatus.InvalidRequest, message, formId, columnName, resolvedColumn, null);

    private static FormEntryByColumnResult NotFoundByColumn(string? formId, string? columnName, string message, string? resolvedColumn = null) =>
        new(FormEntryByColumnStatus.NotFound, message, formId, columnName, resolvedColumn, null);

    /// <summary>
    /// Resolves a caller-supplied key (physical ezfb column, control label/Name, stored
    /// columnName, or jsonId) to the physical ezfb column used for distinct-values and
    /// entry lookup. Same precedence as submitted field keys.
    /// </summary>
    private static bool TryResolveLookupColumn(
        string columnName,
        IReadOnlyList<FormControlRow> controls,
        IReadOnlySet<string> ezfbColumns,
        out string resolvedColumn)
    {
        if (TryResolveControlForField(columnName, controls, out var control) && control is not null
            && EzfbColumnNaming.TryResolveEzfbColumn(control.ColumnName, control.Name, control.JsonId, ezfbColumns, out resolvedColumn))
            return true;

        // No control row (or control has no physical column yet): treat the key as label and/or jsonId.
        if (EzfbColumnNaming.TryResolveEzfbColumn(columnName, columnName, ezfbColumns, out resolvedColumn))
            return true;

        return EzfbColumnNaming.TryResolveEzfbColumn(null, columnName, ezfbColumns, out resolvedColumn);
    }

    private static async Task<List<Guid>> FindEntryIdsByColumnValueAsync(
        NpgsqlConnection connection,
        string tableName,
        string resolvedColumn,
        string columnValue,
        CancellationToken cancellationToken)
    {
        return await FindEntryIdsByFiltersAsync(
            connection,
            tableName,
            [(resolvedColumn, columnValue)],
            cancellationToken);
    }

    private static async Task<List<Guid>> FindEntryIdsByFiltersAsync(
        NpgsqlConnection connection,
        string tableName,
        IReadOnlyList<(string Column, string Value)> filters,
        CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        if (filters.Count == 0)
            return ids;

        var whereParts = new List<string> { "(is_deleted = false OR is_deleted IS NULL)" };
        AppendFilterPredicates(whereParts, filters);

        var sql = $"""
            SELECT item_id
            FROM dbo.{tableName}
            WHERE {string.Join(" AND ", whereParts)};
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        AddFilterParameters(cmd, filters);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            ids.Add(reader.GetGuid(0));
        return ids;
    }

    /// <summary>
    /// Builds AND match predicates from matchColumn/matchValue and/or matchFilters.
    /// When matchColumn is set but matchValue is blank, uses the value from <paramref name="fieldMap"/>.
    /// </summary>
    private static bool TryBuildMatchFilters(
        FormEntryUpsertByColumnsRequest request,
        IReadOnlyDictionary<string, string> fieldMap,
        IReadOnlyList<FormControlRow> controls,
        IReadOnlySet<string> ezfbColumns,
        out List<(string Column, string Value)> matchFilters,
        out string? matchKey,
        out string? error)
    {
        matchFilters = [];
        matchKey = null;
        error = null;

        var matchColumnKey = FirstNonBlank(request.MatchColumn, request.MatchColumnId);
        if (!string.IsNullOrWhiteSpace(matchColumnKey))
        {
            matchKey = matchColumnKey;
            if (!TryResolveLookupColumn(matchColumnKey, controls, ezfbColumns, out var resolvedColumn))
            {
                error = $"match column '{matchColumnKey}' was not found on this form.";
                return false;
            }

            var matchValue = FirstNonBlank(request.MatchValue);
            if (string.IsNullOrWhiteSpace(matchValue))
            {
                // Prefer the submitted field keyed the same way the caller named the match column.
                if (!fieldMap.TryGetValue(matchColumnKey, out matchValue))
                {
                    foreach (var (key, value) in fieldMap)
                    {
                        if (TryResolveLookupColumn(key, controls, ezfbColumns, out var fieldCol)
                            && string.Equals(fieldCol, resolvedColumn, StringComparison.OrdinalIgnoreCase))
                        {
                            matchValue = value;
                            break;
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(matchValue))
            {
                error = "matchValue is required (or include the match column in fields).";
                return false;
            }

            matchFilters.Add((resolvedColumn, matchValue.Trim()));
        }

        if (!TryResolveColumnFilters(request.MatchFilters, controls, ezfbColumns, out var extra, out var filterError))
        {
            error = filterError;
            return false;
        }

        foreach (var filter in extra)
        {
            if (matchFilters.Any(m =>
                    string.Equals(m.Column, filter.Column, StringComparison.OrdinalIgnoreCase)))
                continue;
            matchFilters.Add(filter);
        }

        if (matchFilters.Count > 0 && string.IsNullOrWhiteSpace(matchKey))
            matchKey = matchFilters[0].Column;

        return true;
    }

    private static async Task<List<Dictionary<string, object?>>> LoadEntriesByColumnValueAsync(
        NpgsqlConnection connection,
        string tableName,
        IReadOnlySet<string> ezfbColumns,
        string resolvedColumn,
        string columnValue,
        IReadOnlyList<(string Column, string Value)> filters,
        CancellationToken cancellationToken)
    {
        var selectColumns = ezfbColumns
            .Where(c => !IsSystemColumn(c) || c.Equals("item_id", StringComparison.OrdinalIgnoreCase))
            .Select(c => $"\"{EscapeColumn(c)}\"")
            .ToList();
        var rows = new List<Dictionary<string, object?>>();
        if (selectColumns.Count == 0)
            return rows;

        var escapedCol = EscapeColumn(resolvedColumn);
        var whereParts = new List<string>
        {
            $"\"{escapedCol}\" = @Value",
            "(is_deleted = false OR is_deleted IS NULL)"
        };
        AppendFilterPredicates(whereParts, filters);

        var sql = $"""
            SELECT {string.Join(", ", selectColumns)}
            FROM dbo.{tableName}
            WHERE {string.Join(" AND ", whereParts)};
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Value", columnValue);
        AddFilterParameters(cmd, filters);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                row[name] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// Resolves optional AND filters. Each filter's columnName/columnId uses the same lookup
    /// as the target column (label, jsonId, or physical ezfb column).
    /// </summary>
    private static bool TryResolveColumnFilters(
        IReadOnlyList<FormEntryColumnFilter>? filters,
        IReadOnlyList<FormControlRow> controls,
        IReadOnlySet<string> ezfbColumns,
        out List<(string Column, string Value)> resolved,
        out string? error)
    {
        resolved = [];
        error = null;
        if (filters is null || filters.Count == 0)
            return true;

        for (var i = 0; i < filters.Count; i++)
        {
            var filter = filters[i];
            var key = FirstNonBlank(filter.ColumnName, filter.ColumnId);
            if (string.IsNullOrWhiteSpace(key))
            {
                error = $"filters[{i}]: columnName or columnId is required.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(filter.ColumnValue))
            {
                error = $"filters[{i}]: columnValue is required.";
                return false;
            }

            if (!TryResolveLookupColumn(key, controls, ezfbColumns, out var resolvedColumn))
            {
                error = $"filters[{i}]: column '{key}' was not found on this form.";
                return false;
            }

            resolved.Add((resolvedColumn, filter.ColumnValue.Trim()));
        }

        return true;
    }

    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    private static void AppendFilterPredicates(
        List<string> whereParts,
        IReadOnlyList<(string Column, string Value)> filters)
    {
        for (var i = 0; i < filters.Count; i++)
            whereParts.Add($"\"{EscapeColumn(filters[i].Column)}\" = @Filter{i}");
    }

    private static void AddFilterParameters(
        NpgsqlCommand cmd,
        IReadOnlyList<(string Column, string Value)> filters)
    {
        for (var i = 0; i < filters.Count; i++)
            cmd.Parameters.AddWithValue($"@Filter{i}", filters[i].Value);
    }

    /// <summary>Merges jsonId-keyed WorkflowForms form data (if any) onto each row's item_id, in place.</summary>
    private async Task MergeFormDataJsonAsync(
        List<Dictionary<string, object?>> rows,
        string normalizedFormId,
        CancellationToken cancellationToken)
    {
        foreach (var row in rows)
        {
            if (!row.TryGetValue("item_id", out var itemObj) || itemObj == null)
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
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        row[prop.Name] = JsonElementToObject(prop.Value);
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Could not merge jsonId form data for entry {EntryId}.", entryId);
            }
        }
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

    private static readonly HashSet<string> UpsertBodyReservedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "fields", "field", "data", "entry", "entryId", "formEntryId", "item_id", "itemId",
        "id", "formId", "wFormId", "created_at", "modified_at", "created_by", "modified_by",
        "is_deleted", "EncryptOutput", "creditconsumed", "output"
    };

    /// <summary>
    /// Accepts <c>{ "fields": { ... } }</c> (any casing), or a flat column→value map at the root
    /// (master-form edit often posts the row without a <c>fields</c> wrapper).
    /// </summary>
    private static FormEntryUpsertRequest ParseUpsertRequest(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return new FormEntryUpsertRequest();

        if (TryGetObjectPropertyIgnoreCase(body, "fields", out var fields)
            || TryGetObjectPropertyIgnoreCase(body, "field", out fields)
            || TryGetObjectPropertyIgnoreCase(body, "data", out fields))
        {
            return new FormEntryUpsertRequest { Fields = EnumerateFieldsObject(fields) };
        }

        // Flat map: treat non-reserved root properties as fields.
        var flat = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in body.EnumerateObject())
        {
            if (UpsertBodyReservedKeys.Contains(prop.Name))
                continue;
            if (IsSystemColumn(prop.Name))
                continue;
            flat[prop.Name] = prop.Value;
        }

        return new FormEntryUpsertRequest { Fields = flat.Count > 0 ? flat : null };
    }

    private static bool TryGetObjectPropertyIgnoreCase(JsonElement body, string name, out JsonElement value)
    {
        foreach (var prop in body.EnumerateObject())
        {
            if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (prop.Value.ValueKind != JsonValueKind.Object)
            {
                value = default;
                return false;
            }

            value = prop.Value;
            return true;
        }

        value = default;
        return false;
    }

    private static Dictionary<string, JsonElement> EnumerateFieldsObject(JsonElement fields)
    {
        var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in fields.EnumerateObject())
        {
            if (IsSystemColumn(prop.Name) || UpsertBodyReservedKeys.Contains(prop.Name))
                continue;
            dict[prop.Name] = prop.Value;
        }

        return dict;
    }

    private static Dictionary<string, string> NormalizeFields(Dictionary<string, JsonElement>? fields)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (fields == null)
            return map;

        foreach (var (key, value) in fields)
        {
            if (string.IsNullOrWhiteSpace(key) || IsSystemColumn(key))
                continue;

            // Keep empty strings so master-form edit can clear a column; drop only null/undefined.
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                map[key.Trim()] = string.Empty;
                continue;
            }

            var text = JsonElementToStorageString(value) ?? string.Empty;
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

    private sealed class FormControlRow
    {
        public string JsonId { get; }
        public string? Name { get; }
        public string? Type { get; }
        public string? ColumnName { get; set; }

        public FormControlRow(string jsonId, string? name, string? type, string? columnName = null)
        {
            JsonId = jsonId;
            Name = name;
            Type = type;
            ColumnName = columnName;
        }
    }

    private static List<KeyValuePair<string, string>> ResolveFieldColumns(
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyList<FormControlRow> controls,
        IReadOnlySet<string> ezfbColumns)
    {
        var resolved = new List<KeyValuePair<string, string>>();
        var seenColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in fields)
        {
            if (string.IsNullOrWhiteSpace(key) || IsSystemColumn(key))
                continue;

            string? column = null;
            if (TryResolveControlForField(key, controls, out var control) && control is not null)
            {
                if (EzfbColumnNaming.TryResolveEzfbColumn(
                        control.ColumnName, control.Name, control.JsonId, ezfbColumns, out var col))
                    column = col;
            }

            // Control matched but had no physical column yet, or key is a bare label/jsonId/physical name.
            if (column is null
                && EzfbColumnNaming.TryResolveEzfbColumn(key, key, ezfbColumns, out var colFromKey))
                column = colFromKey;

            if (column is null
                && EzfbColumnNaming.TryResolveEzfbColumn(null, key, ezfbColumns, out var colFromJson))
                column = colFromJson;

            if (column is null || !seenColumns.Add(column))
                continue;

            resolved.Add(new KeyValuePair<string, string>(column, value));
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
        IReadOnlySet<string> ezfbColumns,
        CancellationToken cancellationToken)
    {
        if (resolvedFields.Count == 0)
            return false;

        var sets = resolvedFields
            .Select((f, i) => $"\"{EscapeColumn(f.Key)}\" = @V{i}")
            .ToList();
        var hasModifiedAt = ezfbColumns.Contains("modified_at");
        var hasModifiedBy = ezfbColumns.Contains("modified_by");
        if (hasModifiedAt)
            sets.Add("modified_at = @ModifiedAt");
        if (hasModifiedBy)
            sets.Add("modified_by = @ModifiedBy");

        var sql = $"""
            UPDATE dbo.{tableName}
            SET {string.Join(", ", sets)}
            WHERE item_id = @ItemId AND (is_deleted = false OR is_deleted IS NULL)
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        for (var i = 0; i < resolvedFields.Count; i++)
            cmd.Parameters.AddWithValue($"@V{i}", (object?)resolvedFields[i].Value ?? DBNull.Value);
        if (hasModifiedAt)
            cmd.Parameters.AddWithValue("@ModifiedAt", modifiedAt);
        if (hasModifiedBy)
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

    private static async Task EnsureMissingControlColumnsAsync(
        NpgsqlConnection connection,
        string tableName,
        string tableSuffix,
        string wFormIdValue,
        List<FormControlRow> controls,
        HashSet<string> ezfbColumns,
        CancellationToken cancellationToken)
    {
        var historyTable = $"ezfb_{tableSuffix}_history";
        var hasHistoryTable = await EzfbTableExistsAsync(connection, historyTable, cancellationToken);

        foreach (var control in controls)
        {
            if (EzfbColumnNaming.TryResolveEzfbColumn(
                    control.ColumnName, control.Name, control.JsonId, ezfbColumns, out var existingCol))
            {
                if (string.IsNullOrWhiteSpace(control.ColumnName) && !string.IsNullOrWhiteSpace(existingCol))
                {
                    control.ColumnName = existingCol;
                    await using var updateCmd = new NpgsqlCommand(
                        """
                        UPDATE dbo."wFormControl"
                        SET "columnName" = @ColumnName
                        WHERE "wFormId" = @FormId
                          AND "jsonId" = @JsonId
                          AND ("columnName" IS NULL OR "columnName" = '');
                        """,
                        connection);
                    updateCmd.Parameters.AddWithValue("@ColumnName", existingCol);
                    updateCmd.Parameters.AddWithValue("@FormId", wFormIdValue);
                    updateCmd.Parameters.AddWithValue("@JsonId", control.JsonId);
                    await updateCmd.ExecuteNonQueryAsync(cancellationToken);
                }
                continue;
            }

            string candidateCol;
            if (!string.IsNullOrWhiteSpace(control.ColumnName))
            {
                candidateCol = control.ColumnName.Trim();
            }
            else if (!string.IsNullOrWhiteSpace(control.Name)
                     && EzfbColumnNaming.TryToColumnNameFromLabel(control.Name, out var colFromLabel))
            {
                candidateCol = colFromLabel;
            }
            else if (EzfbColumnNaming.TryToColumnName(control.JsonId, out var colFromJson))
            {
                candidateCol = colFromJson;
            }
            else
            {
                continue;
            }

            var finalCol = candidateCol;
            var suffix = 2;
            while (ezfbColumns.Contains(finalCol))
            {
                finalCol = $"{candidateCol}_{suffix++}";
            }

            var escapedCol = EscapeColumn(finalCol);
            await using (var alterCmd = new NpgsqlCommand(
                $"ALTER TABLE dbo.\"{tableName}\" ADD COLUMN IF NOT EXISTS \"{escapedCol}\" text NULL;",
                connection))
            {
                await alterCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            if (hasHistoryTable)
            {
                await using var alterHistCmd = new NpgsqlCommand(
                    $"ALTER TABLE dbo.\"{historyTable}\" ADD COLUMN IF NOT EXISTS \"{escapedCol}\" text NULL;",
                    connection);
                await alterHistCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var updateCmd = new NpgsqlCommand(
                """
                UPDATE dbo."wFormControl"
                SET "columnName" = @ColumnName
                WHERE "wFormId" = @FormId
                  AND "jsonId" = @JsonId
                  AND ("columnName" IS NULL OR "columnName" = '');
                """,
                connection))
            {
                updateCmd.Parameters.AddWithValue("@ColumnName", finalCol);
                updateCmd.Parameters.AddWithValue("@FormId", wFormIdValue);
                updateCmd.Parameters.AddWithValue("@JsonId", control.JsonId);
                await updateCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            control.ColumnName = finalCol;
            ezfbColumns.Add(finalCol);
        }
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

        // 1) Control label / Name
        foreach (var row in controls)
        {
            if (!string.IsNullOrWhiteSpace(row.Name)
                && string.Equals(row.Name.Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                control = row;
                return true;
            }
        }

        // 2) Stored physical columnName
        foreach (var row in controls)
        {
            if (!string.IsNullOrWhiteSpace(row.ColumnName)
                && string.Equals(row.ColumnName.Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                control = row;
                return true;
            }
        }

        // 3) jsonId (raw or sanitized to ezfb column)
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

        var values = await LoadDistinctColumnValuesAsync(
            connection, tableName, resolvedColumn, filters: [], cancellationToken);
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
        IReadOnlyList<(string Column, string Value)> filters,
        CancellationToken cancellationToken)
    {
        var escaped = EscapeColumn(column);
        var whereParts = new List<string>
        {
            "(is_deleted = false OR is_deleted IS NULL)",
            $"\"{escaped}\" IS NOT NULL",
            $"TRIM(CAST(\"{escaped}\" AS text)) <> ''"
        };
        AppendFilterPredicates(whereParts, filters);

        var sql = $"""
            SELECT DISTINCT "{escaped}"
            FROM dbo.{tableName}
            WHERE {string.Join(" AND ", whereParts)}
            ORDER BY "{escaped}"
            """;

        var values = new List<string>();
        await using var cmd = new NpgsqlCommand(sql, connection);
        AddFilterParameters(cmd, filters);
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
