using Npgsql;
using SaaSApp.MultiTenancy;
using SaaSApp.Repository.Application.Contracts;

namespace SaaSApp.Repository.Infrastructure.Services;

public sealed class RepositoryBrowseService : IRepositoryBrowseService
{
    private readonly ITenantConnectionProvider _connectionProvider;
    private readonly IStaticRepositoryProvisioner _provisioner;

    public RepositoryBrowseService(ITenantConnectionProvider connectionProvider, IStaticRepositoryProvisioner provisioner)
    {
        _connectionProvider = connectionProvider;
        _provisioner = provisioner;
    }

    public async Task<BrowseStructureDto> GetBrowseStructureAsync(
        Guid repositoryId,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var repo = await _provisioner.GetRepositoryAsync(repositoryId, tenantId, cancellationToken)
            ?? throw new InvalidOperationException("Repository not found.");

        var folderFields = RepositoryFolderStructureHelper.OrderFolderFields(
            repo.Fields.Where(f => f.IncludeInFolderStructure))
            .Select(f => new BrowseFolderFieldDto(f.Level, f.Name, f.SqlColumnName))
            .ToList();

        var paths = BuildBrowsePaths(folderFields);
        return new BrowseStructureDto(folderFields, paths);
    }

    public async Task<BrowseChildrenResponseDto> GetBrowseChildrenAsync(
        Guid repositoryId,
        Guid tenantId,
        string pathId,
        IReadOnlyDictionary<string, string> parentFilters,
        int page,
        int pageSize,
        string? search,
        CancellationToken cancellationToken = default)
    {
        var structure = await GetBrowseStructureAsync(repositoryId, tenantId, cancellationToken);
        if (structure.FolderFields.Count == 0)
            throw new InvalidOperationException("No folder structure fields configured for this repository.");

        var path = ResolveBrowsePath(structure, pathId);

        var folderByColumn = structure.FolderFields.ToDictionary(f => f.SqlColumnName, StringComparer.OrdinalIgnoreCase);
        var appliedFilters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var column in path.FieldOrder)
        {
            if (!folderByColumn.TryGetValue(column, out var field))
                continue;

            var value = TryGetFilterValue(parentFilters, field);
            if (value == null)
                break;

            appliedFilters[field.SqlColumnName] = value;
        }

        if (appliedFilters.Count >= path.FieldOrder.Count)
        {
            return new BrowseChildrenResponseDto(
                Level: path.FieldOrder.Count,
                GroupField: string.Empty,
                GroupFieldName: string.Empty,
                PathId: path.Id,
                PathLabel: path.Label,
                ParentFilters: appliedFilters,
                IsLeafLevel: true,
                Groups: new PagedResult<BrowseGroupDto>(Array.Empty<BrowseGroupDto>(), page, pageSize, 0));
        }

        var nextColumn = path.FieldOrder[appliedFilters.Count];
        var nextField = folderByColumn[nextColumn];
        var groups = await GetGroupsAsync(
            repositoryId,
            tenantId,
            nextField.SqlColumnName,
            appliedFilters,
            page,
            pageSize,
            search,
            cancellationToken);

        return new BrowseChildrenResponseDto(
            Level: appliedFilters.Count + 1,
            GroupField: nextField.SqlColumnName,
            GroupFieldName: nextField.Name,
            PathId: path.Id,
            PathLabel: path.Label,
            ParentFilters: appliedFilters,
            IsLeafLevel: false,
            Groups: groups);
    }

    public Task<PagedResult<BrowseGroupDto>> GetBrowseGroupsAsync(
        Guid repositoryId,
        Guid tenantId,
        string groupField,
        IReadOnlyDictionary<string, string> parentFilters,
        int page,
        int pageSize,
        string? search,
        CancellationToken cancellationToken = default)
    {
        return GetGroupsAsync(repositoryId, tenantId, groupField, parentFilters, page, pageSize, search, cancellationToken);
    }

    private async Task<PagedResult<BrowseGroupDto>> GetGroupsAsync(
        Guid repositoryId,
        Guid tenantId,
        string groupField,
        IReadOnlyDictionary<string, string> parentFilters,
        int page,
        int pageSize,
        string? search,
        CancellationToken cancellationToken)
    {
        var repo = await _provisioner.GetRepositoryAsync(repositoryId, tenantId, cancellationToken)
            ?? throw new InvalidOperationException("Repository not found.");

        if (!RepositorySqlHelper.IsValidItemsTableName(repo.ItemsTableName))
            throw new InvalidOperationException("Invalid items table.");

        var folderFields = RepositoryFolderStructureHelper.OrderFolderFields(
            repo.Fields.Where(f => f.IncludeInFolderStructure));

        if (folderFields.Count == 0)
            throw new InvalidOperationException("No folder structure fields configured for this repository.");

        var groupFieldDef = ResolveFolderField(folderFields, groupField)
            ?? throw new ArgumentException($"Field '{groupField}' is not a folder structure field for this repository.");

        // Folder-structure fields are always repository-configured (custom) columns, never
        // reserved ones -- ColumnRef quotes them with their original casing preserved.
        var groupCol = RepositorySqlHelper.SanitizeColumnName(groupFieldDef.SqlColumnName);
        var groupColRef = RepositorySqlHelper.ColumnRef(groupCol);
        var allowedFilterColumns = folderFields
            .Where(f => !string.Equals(f.SqlColumnName, groupFieldDef.SqlColumnName, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.SqlColumnName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var table = RepositorySqlHelper.QualifiedItemsTable(repo.ItemsTableName);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var offset = (page - 1) * pageSize;

        var where = new List<string> { "repository_id = @RepositoryId", "is_deleted = false", $"{groupColRef} IS NOT NULL", $"TRIM({groupColRef}) <> ''" };
        var parameters = new List<NpgsqlParameter> { new("@RepositoryId", repositoryId) };

        foreach (var (key, value) in parentFilters)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var filterField = ResolveFolderField(folderFields, key)
                ?? throw new ArgumentException($"Filter field '{key}' is not a folder structure field for this repository.");

            if (!allowedFilterColumns.Contains(filterField.SqlColumnName))
                throw new ArgumentException($"Filter '{key}' cannot be combined with group field '{groupFieldDef.Name}'.");

            var filterCol = RepositorySqlHelper.SanitizeColumnName(filterField.SqlColumnName);
            var paramName = "@F_" + filterCol;
            where.Add($"{RepositorySqlHelper.ColumnRef(filterCol)} = {paramName}");
            parameters.Add(new NpgsqlParameter(paramName, value.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            where.Add($"{groupColRef} LIKE @Search");
            parameters.Add(new NpgsqlParameter("@Search", $"%{search.Trim()}%"));
        }

        var whereSql = string.Join(" AND ", where);

        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var countSql = $"""
            SELECT COUNT(*) FROM (
                SELECT {groupColRef} AS group_name
                FROM {table}
                WHERE {whereSql}
                GROUP BY {groupColRef}
            ) g;
            """;

        int total;
        await using (var countCmd = new NpgsqlCommand(countSql, connection))
        {
            RepositorySqlHelper.AddParameters(countCmd, parameters);
            total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(cancellationToken));
        }

        var dataSql = $"""
            SELECT {groupColRef} AS group_name,
                   COUNT(*) AS item_count,
                   MAX(modified_at_utc) AS date_modified
            FROM {table}
            WHERE {whereSql}
            GROUP BY {groupColRef}
            ORDER BY {groupColRef}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """;

        var list = new List<BrowseGroupDto>();
        await using (var cmd = new NpgsqlCommand(dataSql, connection))
        {
            RepositorySqlHelper.AddParameters(cmd, parameters);
            cmd.Parameters.AddWithValue("@Offset", offset);
            cmd.Parameters.AddWithValue("@PageSize", pageSize);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                list.Add(new BrowseGroupDto(
                    Convert.ToString(reader.GetValue(0)) ?? string.Empty,
                    Convert.ToInt32(reader.GetValue(1)),
                    reader.IsDBNull(2) ? null : reader.GetDateTime(2)));
            }
        }

        return new PagedResult<BrowseGroupDto>(list, page, pageSize, total);
    }

    private static RepositoryFieldDto? ResolveFolderField(IReadOnlyList<RepositoryFieldDto> folderFields, string fieldName) =>
        folderFields.FirstOrDefault(f =>
            string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(f.SqlColumnName, fieldName, StringComparison.OrdinalIgnoreCase));

    private static string? TryGetFilterValue(IReadOnlyDictionary<string, string> filters, BrowseFolderFieldDto field)
    {
        foreach (var key in new[] { field.SqlColumnName, field.Name })
        {
            if (filters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    private static string? TryGetFilterValue(IReadOnlyDictionary<string, string> filters, RepositoryFieldDto field)
    {
        foreach (var key in new[] { field.SqlColumnName, field.Name })
        {
            if (filters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    private static BrowsePathDto ResolveBrowsePath(BrowseStructureDto structure, string? pathId)
    {
        if (structure.BrowsePaths.Count == 0)
            throw new InvalidOperationException("No browse paths configured.");

        var id = pathId?.Trim();
        if (!string.IsNullOrWhiteSpace(id))
        {
            var match = structure.BrowsePaths.FirstOrDefault(p =>
                string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

            // Allow shorthand: pathId=Supplier → by-Supplier
            if (match == null && !id.StartsWith("by-", StringComparison.OrdinalIgnoreCase))
            {
                match = structure.BrowsePaths.FirstOrDefault(p =>
                    string.Equals(p.Id, $"by-{id}", StringComparison.OrdinalIgnoreCase));
            }

            if (match != null)
                return match;

            var valid = string.Join(", ", structure.BrowsePaths.Select(p => p.Id));
            throw new ArgumentException(
                $"Unknown pathId '{pathId}'. Use an id from GET .../browse/structure browsePaths, e.g. {valid}");
        }

        var firstField = structure.FolderFields.OrderBy(f => f.Level).FirstOrDefault();
        if (firstField != null)
        {
            var byFirst = structure.BrowsePaths.FirstOrDefault(p =>
                string.Equals(p.Id, $"by-{firstField.SqlColumnName}", StringComparison.OrdinalIgnoreCase));
            if (byFirst != null)
                return byFirst;
        }

        return structure.BrowsePaths[0];
    }

    private static IReadOnlyList<BrowsePathDto> BuildBrowsePaths(IReadOnlyList<BrowseFolderFieldDto> folderFields)
    {
        if (folderFields.Count == 0)
            return Array.Empty<BrowsePathDto>();

        var paths = new List<BrowsePathDto>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddPath(string id, string label, IEnumerable<string> order)
        {
            if (!seenIds.Add(id))
                return;
            paths.Add(new BrowsePathDto(id, label, order.ToList()));
        }

        foreach (var root in folderFields.OrderBy(f => f.Level))
        {
            var order = new[] { root.SqlColumnName }
                .Concat(folderFields.Where(f => f.SqlColumnName != root.SqlColumnName).OrderBy(f => f.Level).Select(f => f.SqlColumnName));
            AddPath($"by-{root.SqlColumnName}", $"By {root.Name}", order);
        }

        return paths;
    }
}
