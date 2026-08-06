using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SaaSApp.MultiTenancy;
using SaaSApp.Repository.Application.Contracts;

namespace SaaSApp.Repository.Infrastructure.Services;

/// <summary>
/// Related documents across all tenant repositories.
/// Match uses the source repository's folder-structure fields
/// (<c>IncludeInFolderStructure</c>), i.e. the same virtual folder path.
/// FE only passes repositoryId + itemId.
/// </summary>
public sealed class RepositoryRelatedDocumentsService : IRepositoryRelatedDocumentsService
{
    private static readonly string[] SupplierAliases = ["Supplier", "VendorName", "Vendor", "Vendor Name"];
    private static readonly string[] PoAliases = ["PONumber", "PoNumber", "PO Number"];
    private static readonly string[] InvoiceAliases = ["InvoiceNo", "InvoiceNumber", "Invoice No", "Invoice Number"];
    private static readonly string[] DocumentTypeAliases = ["DocumentType", "Document Type"];

    private const int PerRepoLimit = 50;
    private const int MaxParallelRepos = 8;

    private readonly ITenantConnectionProvider _connectionProvider;
    private readonly IStaticRepositoryProvisioner _provisioner;
    private readonly IRepositoryItemQueryService _items;
    private readonly ILogger<RepositoryRelatedDocumentsService> _logger;

    public RepositoryRelatedDocumentsService(
        ITenantConnectionProvider connectionProvider,
        IStaticRepositoryProvisioner provisioner,
        IRepositoryItemQueryService items,
        ILogger<RepositoryRelatedDocumentsService> logger)
    {
        _connectionProvider = connectionProvider;
        _provisioner = provisioner;
        _items = items;
        _logger = logger;
    }

    public Task<RepositoryRelatedDocumentsResultDto?> GetRelatedAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid itemId,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default) =>
        GetRelatedCoreAsync(
            repositoryId,
            tenantId,
            itemId,
            requireAllFields: false,
            useAllRepositoryFields: false,
            page,
            pageSize,
            cancellationToken);

    public Task<RepositoryRelatedDocumentsResultDto?> GetRelatedExactAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid itemId,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default) =>
        GetRelatedCoreAsync(
            repositoryId,
            tenantId,
            itemId,
            requireAllFields: true,
            useAllRepositoryFields: true,
            page,
            pageSize,
            cancellationToken);

    private async Task<RepositoryRelatedDocumentsResultDto?> GetRelatedCoreAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid itemId,
        bool requireAllFields,
        bool useAllRepositoryFields,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var sourceRepo = await _provisioner.GetRepositoryAsync(repositoryId, tenantId, cancellationToken);
        if (sourceRepo == null)
            return null;

        var source = await _items.GetItemAsync(repositoryId, tenantId, itemId, cancellationToken);
        if (source == null)
            return null;

        var matchCriteria = BuildMatchCriteria(sourceRepo, source.Fields, useAllRepositoryFields);
        if (matchCriteria.Count == 0)
        {
            return new RepositoryRelatedDocumentsResultDto(
                repositoryId,
                itemId,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Array.Empty<string>(),
                page,
                pageSize,
                0,
                Array.Empty<RepositoryRelatedDocumentDto>());
        }

        var match = matchCriteria.ToDictionary(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);
        var matchFields = matchCriteria.Select(c => c.Key).ToList();
        // Loose: any 2 of N folder fields.
        // Exact/score: all repo fields used for scoring; include partials (e.g. 10/14 → ~71, 14/14 → 100).
        var minRequired = requireAllFields
            ? 1
            : Math.Min(2, matchCriteria.Count);

        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        var repos = await ListActiveRepositoriesAsync(connectionString, tenantId, cancellationToken);
        var bag = new ConcurrentBag<RepositoryRelatedDocumentDto>();
        using var gate = new SemaphoreSlim(MaxParallelRepos, MaxParallelRepos);

        var tasks = repos.Select(async repo =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var rows = await QueryRepoAsync(
                    connectionString,
                    repo,
                    matchCriteria,
                    minRequired,
                    sourceRepositoryId: repositoryId,
                    sourceItemId: itemId,
                    cancellationToken);
                foreach (var row in rows)
                    bag.Add(row);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Related docs skipped repository {RepositoryId} ({RepositoryName}).",
                    repo.Id,
                    repo.Name);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);

        var ordered = bag
            .OrderByDescending(x => x.MatchScore)
            .ThenByDescending(x => x.MatchCount)
            .ThenByDescending(x => x.CreatedAtUtc)
            .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var total = ordered.Count;
        var pageData = ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new RepositoryRelatedDocumentsResultDto(
            repositoryId,
            itemId,
            match,
            matchFields,
            page,
            pageSize,
            total,
            pageData);
    }

    /// <summary>
    /// Loose: folder-structure fields (fallback Supplier/PO/Invoice).
    /// Exact: all repository-defined fields that have a value on the source item
    /// (skips DYNAMIC_TABLE / line-item payloads).
    /// </summary>
    private static List<MatchCriterion> BuildMatchCriteria(
        RepositoryDetailDto sourceRepo,
        IReadOnlyDictionary<string, object?> fields,
        bool useAllRepositoryFields)
    {
        IEnumerable<RepositoryFieldDto> sourceFields = useAllRepositoryFields
            ? sourceRepo.Fields
                .Where(f => !IsExcludedFromExactMatch(f.DataType))
                .OrderBy(f => f.OrderId ?? int.MaxValue)
                .ThenBy(f => f.Level)
                .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            : RepositoryFolderStructureHelper.OrderFolderFields(
                sourceRepo.Fields.Where(f => f.IncludeInFolderStructure));

        var criteria = new List<MatchCriterion>();
        foreach (var field in sourceFields)
        {
            var aliases = AliasesFor(field.SqlColumnName, field.Name);
            var value = ResolveFieldValue(fields, aliases);
            if (string.IsNullOrWhiteSpace(value))
                continue;

            criteria.Add(new MatchCriterion(field.SqlColumnName, NormalizeMatchValue(value, field.DataType), aliases));
        }

        if (criteria.Count > 0 || useAllRepositoryFields)
            return criteria;

        // Fallback when repository has no folder-structure fields (loose mode only).
        var supplier = ResolveFieldValue(fields, SupplierAliases);
        var poNumber = ResolveFieldValue(fields, PoAliases);
        var invoiceNo = ResolveFieldValue(fields, InvoiceAliases);

        if (!string.IsNullOrWhiteSpace(supplier) && !string.IsNullOrWhiteSpace(poNumber))
        {
            return
            [
                new MatchCriterion("Supplier", supplier!.Trim(), SupplierAliases),
                new MatchCriterion("PONumber", poNumber!.Trim(), PoAliases)
            ];
        }

        if (!string.IsNullOrWhiteSpace(supplier) && !string.IsNullOrWhiteSpace(invoiceNo))
        {
            return
            [
                new MatchCriterion("Supplier", supplier!.Trim(), SupplierAliases),
                new MatchCriterion("InvoiceNo", invoiceNo!.Trim(), InvoiceAliases)
            ];
        }

        if (!string.IsNullOrWhiteSpace(supplier))
            return [new MatchCriterion("Supplier", supplier!.Trim(), SupplierAliases)];

        return criteria;
    }

    private static bool IsExcludedFromExactMatch(string? dataType)
    {
        var dt = (dataType ?? string.Empty).Trim().ToUpperInvariant();
        return dt is "DYNAMIC_TABLE" or "TABLE" or "JSON" or "FILE" or "ATTACHMENT";
    }

    private static string NormalizeMatchValue(string value, string? dataType)
    {
        var trimmed = value.Trim();
        var dt = (dataType ?? string.Empty).Trim().ToUpperInvariant();
        if (dt is "DATE" or "DATETIME")
        {
            if (DateTime.TryParse(trimmed, out var date))
                return date.ToString("yyyy-MM-dd");
        }

        if (dt is "CURRENCY_AMOUNT" or "AMOUNT" or "NUMBER" or "DECIMAL")
        {
            if (decimal.TryParse(trimmed, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var amount)
                || decimal.TryParse(trimmed, out amount))
            {
                return amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return trimmed;
    }

    private static string[] AliasesFor(string sqlColumnName, string name)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sqlColumnName, name };

        void Expand(string[] group)
        {
            if (group.Any(a => set.Contains(a)))
            {
                foreach (var a in group)
                    set.Add(a);
            }
        }

        Expand(SupplierAliases);
        Expand(PoAliases);
        Expand(InvoiceAliases);
        Expand(DocumentTypeAliases);
        return set.ToArray();
    }

    private static async Task<List<(Guid Id, string Name, string ItemsTableName)>> ListActiveRepositoriesAsync(
        string connectionString,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT Id, Name, ItemsTableName
            FROM repository.Repositories
            WHERE TenantId = @TenantId AND IsDeleted = 0
            ORDER BY Name;
            """;

        var list = new List<(Guid, string, string)>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var table = reader.GetString(2);
            if (!RepositorySqlHelper.IsValidItemsTableName(table))
                continue;
            list.Add((reader.GetGuid(0), reader.GetString(1), table));
        }

        return list;
    }

    private static async Task<List<RepositoryRelatedDocumentDto>> QueryRepoAsync(
        string connectionString,
        (Guid Id, string Name, string ItemsTableName) repo,
        IReadOnlyList<MatchCriterion> matchCriteria,
        int minRequired,
        Guid sourceRepositoryId,
        Guid sourceItemId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var columns = await RepositoryItemTableColumns.LoadAsync(connection, repo.ItemsTableName, cancellationToken);
        if (minRequired <= 0 || matchCriteria.Count == 0)
            return [];

        var where = new List<string> { "i.RepositoryId = @RepositoryId", "i.IsDeleted = 0" };
        var parameters = new List<SqlParameter> { new("@RepositoryId", repo.Id) };
        var matchScoreParts = new List<string>();
        var resolvedKeys = new List<string>();

        for (var i = 0; i < matchCriteria.Count; i++)
        {
            var criterion = matchCriteria[i];
            var col = ResolveCanonical(columns, criterion.Aliases);
            if (col == null)
                continue; // missing column = unmatched toward score denominator

            var paramName = $"@m{i}";
            parameters.Add(new SqlParameter(paramName, criterion.Value));
            matchScoreParts.Add($"(CASE WHEN i.[{col}] = {paramName} THEN 1 ELSE 0 END)");
            resolvedKeys.Add(criterion.Key);
        }

        if (matchScoreParts.Count == 0 || matchScoreParts.Count < minRequired)
            return [];

        var matchCountExpr = $"({string.Join(" + ", matchScoreParts)})";
        where.Add($"{matchCountExpr} >= @MinRequired");
        parameters.Add(new SqlParameter("@MinRequired", minRequired));

        if (repo.Id == sourceRepositoryId)
        {
            where.Add("i.Id <> @SourceItemId");
            parameters.Add(new SqlParameter("@SourceItemId", sourceItemId));
        }

        var supplierCol = ResolveCanonical(columns, SupplierAliases);
        var poCol = ResolveCanonical(columns, PoAliases);
        var invoiceCol = ResolveCanonical(columns, InvoiceAliases);
        var docTypeCol = ResolveCanonical(columns, DocumentTypeAliases);
        var createdCol = RepositoryItemTableColumns.Has(columns, "CreatedAtUtc") ? "CreatedAtUtc" : null;

        var selectSupplier = supplierCol != null ? $"i.[{supplierCol}]" : "CAST(NULL AS nvarchar(1))";
        var selectPo = poCol != null ? $"i.[{poCol}]" : "CAST(NULL AS nvarchar(1))";
        var selectInvoice = invoiceCol != null ? $"i.[{invoiceCol}]" : "CAST(NULL AS nvarchar(1))";
        var selectDocType = docTypeCol != null ? $"i.[{docTypeCol}]" : "CAST(NULL AS nvarchar(1))";
        var selectCreated = createdCol != null ? $"i.[{createdCol}]" : "CAST(NULL AS datetime2)";
        var orderBy = createdCol != null
            ? $"{matchCountExpr} DESC, i.[{createdCol}] DESC, i.Id DESC"
            : $"{matchCountExpr} DESC, i.Id DESC";

        var fieldFlagSelects = new List<string>();
        for (var i = 0; i < matchScoreParts.Count; i++)
            fieldFlagSelects.Add($"{matchScoreParts[i]} AS F{i}");

        // Denominator: all source criteria (e.g. 14). Numerator: how many matched (e.g. 10 → 71).
        var totalFields = Math.Max(1, matchCriteria.Count);
        var table = RepositorySqlHelper.QualifiedItemsTable(repo.ItemsTableName);
        var sql = $"""
            SELECT TOP (@Limit)
                i.Id,
                i.FileName,
                i.FileType,
                i.FileSize,
                {selectDocType} AS DocumentType,
                {selectSupplier} AS Supplier,
                {selectPo} AS PoNumber,
                {selectInvoice} AS InvoiceNumber,
                {selectCreated} AS CreatedAtUtc,
                {matchCountExpr} AS MatchCount,
                {string.Join(",\n                ", fieldFlagSelects)}
            FROM {table} i
            WHERE {string.Join(" AND ", where)}
            ORDER BY {orderBy};
            """;

        var list = new List<RepositoryRelatedDocumentDto>();
        await using var cmd = new SqlCommand(sql, connection);
        RepositorySqlHelper.AddParameters(cmd, parameters);
        cmd.Parameters.AddWithValue("@Limit", PerRepoLimit);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var matchCount = reader.IsDBNull(9) ? 0 : Convert.ToInt32(reader.GetValue(9));
            var matchedFields = new List<string>();
            for (var i = 0; i < resolvedKeys.Count; i++)
            {
                var flagOrdinal = 10 + i;
                if (!reader.IsDBNull(flagOrdinal) && Convert.ToInt32(reader.GetValue(flagOrdinal)) == 1)
                    matchedFields.Add(resolvedKeys[i]);
            }

            // score = matched / total * 100  (10/14 → 71, 14/14 → 100)
            var score = (int)Math.Round(matchCount * 100.0 / totalFields);
            if (score > 100)
                score = 100;

            list.Add(new RepositoryRelatedDocumentDto(
                repo.Id,
                repo.Name,
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : Convert.ToInt32(reader.GetValue(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetDateTime(8),
                score,
                matchCount,
                matchedFields));
        }

        return list;
    }

    private static string? ResolveCanonical(HashSet<string> columns, IReadOnlyList<string> aliases)
    {
        foreach (var alias in aliases)
        {
            if (RepositoryItemTableColumns.TryGetCanonicalName(columns, alias, out var canonical))
                return canonical;
        }

        return null;
    }

    private static string? ResolveFieldValue(
        IReadOnlyDictionary<string, object?> fields,
        IReadOnlyList<string> aliases)
    {
        foreach (var alias in aliases)
        {
            foreach (var kv in fields)
            {
                if (!string.Equals(kv.Key, alias, StringComparison.OrdinalIgnoreCase))
                    continue;
                var text = kv.Value?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }

        return null;
    }

    private sealed record MatchCriterion(string Key, string Value, string[] Aliases);
}
