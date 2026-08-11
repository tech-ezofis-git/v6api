using Npgsql;
using SaaSApp.MultiTenancy;
using SaaSApp.Repository.Application.Contracts;
using SaaSApp.Repository.Infrastructure.Storage;

namespace SaaSApp.Repository.Infrastructure.Services;

public sealed class RepositoryItemQueryService : IRepositoryItemQueryService
{
    private readonly ITenantConnectionProvider _connectionProvider;
    private readonly IStaticRepositoryProvisioner _provisioner;
    private readonly IRepositoryFileStorage _fileStorage;
    private readonly IRepositoryItemActivityService _activity;

    public RepositoryItemQueryService(
        ITenantConnectionProvider connectionProvider,
        IStaticRepositoryProvisioner provisioner,
        IRepositoryFileStorage fileStorage,
        IRepositoryItemActivityService activity)
    {
        _connectionProvider = connectionProvider;
        _provisioner = provisioner;
        _fileStorage = fileStorage;
        _activity = activity;
    }

    public async Task<ItemListFilterSchemaDto> GetItemListFilterSchemaAsync(
        Guid repositoryId,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var repo = await _provisioner.GetRepositoryAsync(repositoryId, tenantId, cancellationToken)
            ?? throw new InvalidOperationException("Repository not found.");
        return RepositoryItemFilterHelper.BuildFilterSchema(repo);
    }

    public async Task<PagedResult<RepositoryItemListDto>> ListItemsAsync(
        Guid repositoryId,
        Guid tenantId,
        RepositoryItemListQuery query,
        CancellationToken cancellationToken = default)
    {
        var repo = await _provisioner.GetRepositoryAsync(repositoryId, tenantId, cancellationToken)
            ?? throw new InvalidOperationException("Repository not found.");

        if (!RepositorySqlHelper.IsValidItemsTableName(repo.ItemsTableName))
            throw new InvalidOperationException("Invalid items table.");

        var table = RepositorySqlHelper.QualifiedItemsTable(repo.ItemsTableName);
        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var tableColumns = await RepositoryItemTableColumns.LoadAsync(connection, repo.ItemsTableName, cancellationToken);
        var allowedColumns = RepositoryItemFilterHelper.BuildFilterableColumns(repo, tableColumns);
        var filters = RepositoryItemFilterHelper.ParseItemFilters(query.Filters)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var statusValues = RepositoryItemFilterHelper.ExtractStatusFilterValues(filters, allowedColumns, repo);

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, RepositoryItemCursorHelper.MaxPageSize);
        var sortCol = RepositoryItemFilterHelper.ResolveSortColumn(query.SortBy, allowedColumns, tableColumns);
        if (!RepositoryItemTableColumns.Has(tableColumns, sortCol))
            sortCol = RepositoryItemTableColumns.Has(tableColumns, "CreatedAtUtc") ? "CreatedAtUtc" : "FileName";

        var sortAscending = string.Equals(query.SortOrder, "asc", StringComparison.OrdinalIgnoreCase);
        var sortDir = sortAscending ? "ASC" : "DESC";
        var useCursor = !string.IsNullOrWhiteSpace(query.Cursor);
        int offset = 0;
        if (!useCursor)
            offset = (page - 1) * pageSize;

        var where = new List<string> { "i.repository_id = @RepositoryId", "i.is_deleted = false" };
        var parameters = new List<NpgsqlParameter> { new("@RepositoryId", repositoryId) };

        if (statusValues.Count > 0)
        {
            var matchingWorkflowIds = await ResolveMatchingWorkflowInstanceIdsAsync(
                connection, table, repositoryId, tableColumns, statusValues, cancellationToken);
            RepositoryItemFilterHelper.ApplyDisplayStatusFilter(
                where, parameters, statusValues, matchingWorkflowIds, tableColumns);
        }

        RepositoryItemFilterHelper.ApplyEqualityFilters(where, parameters, filters, allowedColumns, repo);

        if (useCursor)
        {
            var (cursorSortCol, cursorAscending, cursorValue, cursorId) =
                RepositoryItemCursorHelper.Decode(query.Cursor!);
            if (!string.Equals(cursorSortCol, sortCol, StringComparison.OrdinalIgnoreCase)
                || cursorAscending != sortAscending)
            {
                throw new ArgumentException(
                    "cursor sort does not match sortBy/sortOrder. Use the same sort or omit cursor.");
            }

            RepositoryItemCursorHelper.ApplyKeysetFilter(
                where, parameters, sortCol, sortAscending, cursorValue, cursorId);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            where.Add("i.file_name LIKE @Search");
            parameters.Add(new NpgsqlParameter("@Search", $"%{query.Search.Trim()}%"));
        }

        var dateFilterCol = RepositoryItemListReader.ResolveDateFilterColumn(tableColumns, repo);
        if (query.DateFrom.HasValue && dateFilterCol != null)
        {
            where.Add($"i.{RepositorySqlHelper.ColumnRef(dateFilterCol)} >= @DateFrom");
            parameters.Add(new NpgsqlParameter("@DateFrom", query.DateFrom.Value.Date));
        }

        if (query.DateTo.HasValue && dateFilterCol != null)
        {
            where.Add($"i.{RepositorySqlHelper.ColumnRef(dateFilterCol)} <= @DateTo");
            parameters.Add(new NpgsqlParameter("@DateTo", query.DateTo.Value.Date));
        }

        var whereSql = string.Join(" AND ", where);
        var selectList = RepositoryItemListReader.BuildSelectList(tableColumns, repo);

        int total = -1;
        if (!query.SkipTotal)
        {
            var countSql = $"SELECT COUNT(*) FROM {table} i WHERE {whereSql};";
            await using var countCmd = new NpgsqlCommand(countSql, connection);
            RepositorySqlHelper.AddParameters(countCmd, parameters);
            total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(cancellationToken));
        }

        var pagingSql = useCursor
            ? "FETCH NEXT @PageSize ROWS ONLY"
            : "OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

        var dataSql = $"""
            SELECT {selectList}
            FROM {table} i
            INNER JOIN repository."StorageProviders" sp ON sp."Id" = i.storage_provider_id
            WHERE {whereSql}
            ORDER BY i.{RepositorySqlHelper.ColumnRef(sortCol)} {sortDir}, i.id {sortDir}
            {pagingSql};
            """;

        var list = new List<RepositoryItemListDto>();
        await using (var cmd = new NpgsqlCommand(dataSql, connection))
        {
            RepositorySqlHelper.AddParameters(cmd, parameters);
            if (!useCursor)
                cmd.Parameters.AddWithValue("@Offset", offset);
            cmd.Parameters.AddWithValue("@PageSize", pageSize);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                list.Add(RepositoryItemListReader.ReadRow(reader, tableColumns, repo));
        }

        await RepositoryItemWorkflowStatusEnricher.EnrichListAsync(connection, list, cancellationToken);
        await RepositoryItemListReader.ResolveCreatedByEmailsAsync(connection, list, cancellationToken);

        string? nextCursor = null;
        if (list.Count == pageSize)
        {
            var last = list[^1];
            var sortValue = RepositoryItemCursorHelper.GetSortValueFromRow(last, sortCol);
            nextCursor = RepositoryItemCursorHelper.Encode(sortCol, sortAscending, sortValue, last.Id);
        }

        return new PagedResult<RepositoryItemListDto>(list, useCursor ? 0 : page, pageSize, total, nextCursor);
    }

    public async Task<IReadOnlyList<FacetValueDto>> GetFacetsAsync(
        Guid repositoryId,
        Guid tenantId,
        string fieldName,
        string? scopeFilters,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var repo = await _provisioner.GetRepositoryAsync(repositoryId, tenantId, cancellationToken)
            ?? throw new InvalidOperationException("Repository not found.");

        var allowedColumns = RepositoryItemFilterHelper.BuildFilterableColumns(repo);
        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var tableColumns = await RepositoryItemTableColumns.LoadAsync(connection, repo.ItemsTableName, cancellationToken);
        var col = RepositoryItemFilterHelper.ResolveFilterColumn(fieldName, allowedColumns, repo);

        var table = RepositorySqlHelper.QualifiedItemsTable(repo.ItemsTableName);
        limit = Math.Clamp(limit, 1, 500);

        var where = new List<string> { "repository_id = @RepositoryId", "is_deleted = false" };
        var parameters = new List<NpgsqlParameter> { new("@RepositoryId", repositoryId) };

        var scope = RepositoryItemFilterHelper.ParseItemFilters(scopeFilters)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var statusValues = RepositoryItemFilterHelper.ExtractStatusFilterValues(scope, allowedColumns, repo);
        if (statusValues.Count > 0)
        {
            var matchingWorkflowIds = await ResolveMatchingWorkflowInstanceIdsAsync(
                connection, table, repositoryId, tableColumns, statusValues, cancellationToken);
            RepositoryItemFilterHelper.ApplyDisplayStatusFilter(
                where, parameters, statusValues, matchingWorkflowIds, tableColumns, tableAlias: string.Empty);
        }

        RepositoryItemFilterHelper.ApplyEqualityFilters(where, parameters, scope, allowedColumns, repo, tableAlias: string.Empty);

        // Status facets: prefer values the UI actually shows (workflow + AI + raw columns).
        if (RepositoryItemFilterHelper.IsStatusFilterColumn(col))
        {
            return await GetStatusFacetsAsync(
                connection, table, tableColumns, where, parameters, limit, cancellationToken);
        }

        var colRef = RepositorySqlHelper.ColumnRef(col);
        where.Add($"{colRef} IS NOT NULL");

        var sql = $"""
            SELECT {colRef} AS value, COUNT(*) AS cnt
            FROM {table}
            WHERE {string.Join(" AND ", where)}
            GROUP BY {colRef}
            ORDER BY COUNT(*) DESC, {colRef}
            LIMIT @Limit;
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        RepositorySqlHelper.AddParameters(cmd, parameters);
        cmd.Parameters.AddWithValue("@Limit", limit);

        var list = new List<FacetValueDto>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            list.Add(new FacetValueDto(Convert.ToString(reader.GetValue(0)) ?? string.Empty, Convert.ToInt32(reader.GetValue(1))));

        return list;
    }

    public async Task<RepositoryItemDetailDto?> GetItemAsync(Guid repositoryId, Guid tenantId, Guid itemId, CancellationToken cancellationToken = default)
    {
        var repo = await _provisioner.GetRepositoryAsync(repositoryId, tenantId, cancellationToken)
            ?? throw new InvalidOperationException("Repository not found.");

        var table = RepositorySqlHelper.QualifiedItemsTable(repo.ItemsTableName);
        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var tableColumns = await RepositoryItemTableColumns.LoadAsync(connection, repo.ItemsTableName, cancellationToken);
        // Explicit aliased select instead of `i.*` -- reserved physical columns are lowercase
        // snake_case now, so an unaliased `i.*` would return them under names that no longer
        // match the PascalCase dictionary keys the rest of this method (and its callers) read.
        var selectList = RepositorySqlHelper.BuildAliasedSelectList(
            tableColumns.OrderBy(c => c, StringComparer.OrdinalIgnoreCase), "i");

        var sql = $"""
            SELECT {selectList}, sp."Code" AS "Code"
            FROM {table} i
            INNER JOIN repository."StorageProviders" sp ON sp."Id" = i.storage_provider_id
            WHERE i.id = @ItemId AND i.repository_id = @RepositoryId AND i.is_deleted = false;
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@ItemId", itemId);
        cmd.Parameters.AddWithValue("@RepositoryId", repositoryId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var providerCode = reader.GetString(reader.GetOrdinal("Code"));
        var fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            if (name is "Code")
                continue;
            fields[name] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        }

        await reader.CloseAsync();

        if (fields.TryGetValue("WorkflowInstanceId", out var wfRaw)
            && wfRaw is Guid wfId
            && wfId != Guid.Empty)
        {
            var workflowStatus = await RepositoryItemWorkflowStatusEnricher.ResolveStatusAsync(
                connection, wfId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(workflowStatus))
                fields["Status"] = workflowStatus;
        }

        var filePath = fields.TryGetValue("FilePath", out var fpRaw) ? fpRaw as string ?? fpRaw?.ToString() : null;
        var fileType = fields.TryGetValue("FileType", out var ftRaw) ? ftRaw as string ?? ftRaw?.ToString() : null;
        var fileName = RepositoryFilePathHelper.EnsureFileNameHasExtension(
            fields.TryGetValue("FileName", out var fnRaw) ? fnRaw as string ?? fnRaw?.ToString() : null,
            fileType,
            filePath);
        fields["FileName"] = fileName;

        return new RepositoryItemDetailDto(
            Guid.Parse(fields["Id"]!.ToString()!),
            fileName,
            filePath,
            fileType,
            fields.TryGetValue("FileSize", out var fs) && fs is int fileSize ? fileSize : fs is long fileSizeL ? (int)fileSizeL : null,
            (Guid)fields["StorageProviderId"]!,
            providerCode,
            fields);
    }

    public async Task<RepositoryItemWorkspaceDto?> GetItemWorkspaceAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        var repo = await _provisioner.GetRepositoryAsync(repositoryId, tenantId, cancellationToken)
            ?? throw new InvalidOperationException("Repository not found.");

        var item = await GetItemAsync(repositoryId, tenantId, itemId, cancellationToken);
        if (item == null)
            return null;

        var fields = new Dictionary<string, object?>(item.Fields, StringComparer.OrdinalIgnoreCase);
        await TryResolveCreatedByEmailAsync(fields, cancellationToken);

        return RepositoryItemWorkspaceBuilder.Build(
            repositoryId,
            repo,
            item.Id,
            item.FileName,
            item.FileType,
            item.FileSize,
            item.StorageProviderId,
            item.StorageProviderCode,
            fields);
    }

    private async Task TryResolveCreatedByEmailAsync(
        IDictionary<string, object?> fields,
        CancellationToken cancellationToken)
    {
        if (!fields.TryGetValue("CreatedBy", out var createdByRaw)
            || !RepositoryUserNameResolver.TryParseUserId(createdByRaw, out var createdById))
        {
            return;
        }

        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var email = await RepositoryUserNameResolver.ResolveEmailAsync(
            connection,
            createdById,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(email))
        {
            fields["CreatedById"] = createdById;
            fields["CreatedByEmail"] = email;
            // Keep GUID on CreatedBy so document-security share grants (CreatedBy equals userId) still match.
            fields["CreatedBy"] = createdById;
            fields["CreatedByName"] = email;
        }
    }

    public async Task<Guid> CreateItemAsync(
        Guid repositoryId,
        Guid tenantId,
        CreateRepositoryItemRequest request,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        var repo = await _provisioner.GetRepositoryAsync(repositoryId, tenantId, cancellationToken)
            ?? throw new InvalidOperationException("Repository not found.");

        var itemId = Guid.NewGuid();
        var storageProviderId = request.StorageProviderId ?? repo.StorageProviderId;

        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await RepositoryItemInsertHelper.InsertItemAsync(
            connection, repo, tenantId, repositoryId, itemId, storageProviderId, request, userId, cancellationToken);

        return itemId;
    }

    public async Task<UpdateRepositoryItemMetadataResult?> UpdateItemMetadataAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid itemId,
        IReadOnlyDictionary<string, string> metadata,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        var repo = await _provisioner.GetRepositoryAsync(repositoryId, tenantId, cancellationToken)
            ?? throw new InvalidOperationException("Repository not found.");

        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var updatedFieldCount = await RepositoryItemMetadataUpdateHelper.UpdateAsync(
            connection, repo, tenantId, repositoryId, itemId, metadata, userId, cancellationToken);

        if (updatedFieldCount < 0)
            return null;

        // Do not write noisy "Metadata updated" timeline rows -- ingest/OCR/workflow history are shown instead.
        return new UpdateRepositoryItemMetadataResult(itemId, updatedFieldCount);
    }

    public async Task<RepositoryItemFileContent?> OpenItemFileAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        var item = await GetItemAsync(repositoryId, tenantId, itemId, cancellationToken);
        if (item == null || string.IsNullOrWhiteSpace(item.FilePath))
            return null;

        var providerCode = string.IsNullOrWhiteSpace(item.StorageProviderCode) ? "EZOFIS" : item.StorageProviderCode;
        if (!_fileStorage.CanRead(providerCode))
            throw new InvalidOperationException($"File download is not available for storage provider '{providerCode}'.");

        var stream = await _fileStorage.OpenReadAsync(tenantId, item.FilePath, providerCode, cancellationToken);
        var contentType = string.IsNullOrWhiteSpace(item.FileType) ? "application/octet-stream" : item.FileType;
        var fileName = RepositoryFilePathHelper.EnsureFileNameHasExtension(
            item.FileName,
            item.FileType,
            item.FilePath);
        return new RepositoryItemFileContent(stream, fileName, contentType, item.FileSize);
    }

    private static async Task<IReadOnlyList<Guid>> ResolveMatchingWorkflowInstanceIdsAsync(
        NpgsqlConnection connection,
        string qualifiedItemsTable,
        Guid repositoryId,
        HashSet<string> tableColumns,
        IReadOnlyList<string> statusValues,
        CancellationToken cancellationToken)
    {
        if (!RepositoryItemTableColumns.Has(tableColumns, "WorkflowInstanceId"))
            return Array.Empty<Guid>();

        var candidates = new List<Guid>();
        var sql = $"""
            SELECT DISTINCT workflow_instance_id
            FROM {qualifiedItemsTable}
            WHERE repository_id = @RepositoryId
              AND is_deleted = false
              AND workflow_instance_id IS NOT NULL;
            """;
        await using (var cmd = new NpgsqlCommand(sql, connection))
        {
            cmd.Parameters.AddWithValue("@RepositoryId", repositoryId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                candidates.Add(reader.GetGuid(0));
        }

        return await RepositoryItemWorkflowStatusEnricher.FindInstanceIdsWithDisplayStatusAsync(
            connection, candidates, statusValues, cancellationToken);
    }

    private static async Task<IReadOnlyList<FacetValueDto>> GetStatusFacetsAsync(
        NpgsqlConnection connection,
        string qualifiedItemsTable,
        HashSet<string> tableColumns,
        IReadOnlyList<string> where,
        IList<NpgsqlParameter> parameters,
        int limit,
        CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            var key = value.Trim();
            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        var selectParts = new List<string> { "id AS \"Id\"" };
        foreach (var col in new[] { "Status", "StageStatus", "AiStatus", "MatchedStatus", "WorkflowInstanceId" })
        {
            if (RepositoryItemTableColumns.Has(tableColumns, col))
                selectParts.Add($"{RepositorySqlHelper.ColumnRef(col)} AS \"{col}\"");
        }

        var sql = $"""
            SELECT {string.Join(", ", selectParts)}
            FROM {qualifiedItemsTable}
            WHERE {string.Join(" AND ", where)};
            """;

        var rows = new List<(string? Status, string? StageStatus, string? AiStatus, string? MatchedStatus, Guid? WorkflowInstanceId)>();
        await using (var cmd = new NpgsqlCommand(sql, connection))
        {
            RepositorySqlHelper.AddParameters(cmd, parameters);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                string? GetOpt(string name)
                {
                    if (!HasOrdinal(reader, name) || reader.IsDBNull(reader.GetOrdinal(name)))
                        return null;
                    return Convert.ToString(reader.GetValue(reader.GetOrdinal(name)));
                }

                Guid? wf = null;
                if (HasOrdinal(reader, "WorkflowInstanceId") && !reader.IsDBNull(reader.GetOrdinal("WorkflowInstanceId")))
                    wf = reader.GetGuid(reader.GetOrdinal("WorkflowInstanceId"));

                rows.Add((GetOpt("Status"), GetOpt("StageStatus"), GetOpt("AiStatus"), GetOpt("MatchedStatus"), wf));
            }
        }

        var instanceIds = rows
            .Where(r => r.WorkflowInstanceId is Guid id && id != Guid.Empty)
            .Select(r => r.WorkflowInstanceId!.Value)
            .Distinct()
            .ToList();
        var statusByInstance = await RepositoryItemWorkflowStatusEnricher.GetDisplayStatusMapAsync(
            connection, instanceIds, cancellationToken);

        foreach (var row in rows)
        {
            if (row.WorkflowInstanceId is Guid wfId
                && statusByInstance.TryGetValue(wfId, out var wfStatus)
                && !string.IsNullOrWhiteSpace(wfStatus))
            {
                Add(wfStatus);
                continue;
            }

            Add(FirstNonEmpty(row.Status, row.StageStatus, row.AiStatus, row.MatchedStatus));
        }

        return counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(kv => new FacetValueDto(kv.Key, kv.Value))
            .ToList();
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }

        return null;
    }

    private static bool HasOrdinal(NpgsqlDataReader reader, string name)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
