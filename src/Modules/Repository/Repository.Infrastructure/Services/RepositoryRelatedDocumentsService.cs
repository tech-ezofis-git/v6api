using System.Collections.Concurrent;
using Npgsql;
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
    private static readonly ConcurrentDictionary<string, byte> SchemaEnsured = new(StringComparer.OrdinalIgnoreCase);
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
            fields: null,
            value: null,
            cancellationToken);

    public Task<RepositoryRelatedDocumentsResultDto?> GetRelatedExactAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid itemId,
        int page = 1,
        int pageSize = 50,
        IReadOnlyList<string>? fields = null,
        string? value = null,
        CancellationToken cancellationToken = default) =>
        GetRelatedCoreAsync(
            repositoryId,
            tenantId,
            itemId,
            requireAllFields: true,
            useAllRepositoryFields: true,
            page,
            pageSize,
            fields,
            value,
            cancellationToken);

    public async Task<RepositorySavedRelatedDocumentsResultDto?> GetSavedRelatedAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        var sourceRepo = await _provisioner.GetRepositoryAsync(repositoryId, tenantId, cancellationToken);
        if (sourceRepo == null)
            return null;

        var source = await _items.GetItemAsync(repositoryId, tenantId, itemId, cancellationToken);
        if (source == null)
            return null;

        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await EnsureRelatedSchemaAsync(connectionString, cancellationToken);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT "Id", "RelatedRepositoryId", "RelatedItemId", "MatchField", "MatchValue", "MatchScore", "CreatedAtUtc"
            FROM repository."ItemRelatedDocuments"
            WHERE "TenantId" = @TenantId
              AND "RepositoryId" = @RepositoryId
              AND "ItemId" = @ItemId
              AND "IsDeleted" = false
            ORDER BY "CreatedAtUtc" DESC, "Id" DESC;
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        cmd.Parameters.AddWithValue("@RepositoryId", repositoryId);
        cmd.Parameters.AddWithValue("@ItemId", itemId);

        var links = new List<(Guid LinkId, Guid RelRepoId, Guid RelItemId, string? MatchField, string? MatchValue, int? MatchScore, DateTime CreatedAtUtc)>();
        string? matchField = null;
        string? matchValue = null;

        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var mf = reader.IsDBNull(3) ? null : reader.GetString(3);
                var mv = reader.IsDBNull(4) ? null : reader.GetString(4);
                matchField ??= mf;
                matchValue ??= mv;
                links.Add((
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetGuid(2),
                    mf,
                    mv,
                    reader.IsDBNull(5) ? null : Convert.ToInt32(reader.GetValue(5)),
                    reader.GetDateTime(6)));
            }
        }

        var data = new List<RepositorySavedRelatedDocumentDto>();
        foreach (var link in links)
        {
            var detail = await TryLoadRelatedItemAsync(
                connectionString,
                tenantId,
                link.RelRepoId,
                link.RelItemId,
                cancellationToken);

            data.Add(new RepositorySavedRelatedDocumentDto(
                link.LinkId,
                link.RelRepoId,
                detail?.RepositoryName,
                link.RelItemId,
                detail?.FileName,
                detail?.FileType,
                detail?.FileSize,
                detail?.DocumentType,
                detail?.Supplier,
                detail?.PoNumber,
                detail?.InvoiceNumber,
                link.MatchScore,
                link.MatchField,
                link.MatchValue,
                link.CreatedAtUtc));
        }

        return new RepositorySavedRelatedDocumentsResultDto(
            repositoryId,
            itemId,
            matchField,
            matchValue,
            data.Count,
            data);
    }

    public async Task<RepositorySavedRelatedDocumentsResultDto?> SaveRelatedAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid itemId,
        SaveRepositoryRelatedDocumentsRequest request,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sourceRepo = await _provisioner.GetRepositoryAsync(repositoryId, tenantId, cancellationToken);
        if (sourceRepo == null)
            return null;

        var source = await _items.GetItemAsync(repositoryId, tenantId, itemId, cancellationToken);
        if (source == null)
            return null;

        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await EnsureRelatedSchemaAsync(connectionString, cancellationToken);

        var items = (request.Items ?? Array.Empty<SaveRepositoryRelatedDocumentRef>())
            .Where(i => i.RepositoryId != Guid.Empty && i.ItemId != Guid.Empty)
            .Where(i => !(i.RepositoryId == repositoryId && i.ItemId == itemId))
            .GroupBy(i => (i.RepositoryId, i.ItemId))
            .Select(g => g.First())
            .ToList();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        // Replace semantics: soft-delete previous set for this source item.
        await using (var clear = new NpgsqlCommand(
                         """
                         UPDATE repository."ItemRelatedDocuments"
                         SET "IsDeleted" = true
                         WHERE "TenantId" = @TenantId
                           AND "RepositoryId" = @RepositoryId
                           AND "ItemId" = @ItemId
                           AND "IsDeleted" = false;
                         """,
                         connection,
                         tx))
        {
            clear.Parameters.AddWithValue("@TenantId", tenantId);
            clear.Parameters.AddWithValue("@RepositoryId", repositoryId);
            clear.Parameters.AddWithValue("@ItemId", itemId);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var item in items)
        {
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO repository."ItemRelatedDocuments"
                    ("Id", "TenantId", "RepositoryId", "ItemId", "RelatedRepositoryId", "RelatedItemId",
                     "MatchField", "MatchValue", "MatchScore", "CreatedBy", "CreatedAtUtc", "IsDeleted")
                VALUES
                    (@Id, @TenantId, @RepositoryId, @ItemId, @RelatedRepositoryId, @RelatedItemId,
                     @MatchField, @MatchValue, @MatchScore, @CreatedBy, now(), false);
                """,
                connection,
                tx);
            insert.Parameters.AddWithValue("@Id", Guid.NewGuid());
            insert.Parameters.AddWithValue("@TenantId", tenantId);
            insert.Parameters.AddWithValue("@RepositoryId", repositoryId);
            insert.Parameters.AddWithValue("@ItemId", itemId);
            insert.Parameters.AddWithValue("@RelatedRepositoryId", item.RepositoryId);
            insert.Parameters.AddWithValue("@RelatedItemId", item.ItemId);
            insert.Parameters.AddWithValue("@MatchField", (object?)TrimOrNull(request.MatchField) ?? DBNull.Value);
            insert.Parameters.AddWithValue("@MatchValue", (object?)TrimOrNull(request.MatchValue) ?? DBNull.Value);
            insert.Parameters.AddWithValue("@MatchScore", (object?)item.MatchScore ?? DBNull.Value);
            insert.Parameters.AddWithValue("@CreatedBy", (object?)userId ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return await GetSavedRelatedAsync(repositoryId, tenantId, itemId, cancellationToken);
    }

    private async Task<RepositoryRelatedDocumentsResultDto?> GetRelatedCoreAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid itemId,
        bool requireAllFields,
        bool useAllRepositoryFields,
        int page,
        int pageSize,
        IReadOnlyList<string>? fields = null,
        string? value = null,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var sourceRepo = await _provisioner.GetRepositoryAsync(repositoryId, tenantId, cancellationToken);
        if (sourceRepo == null)
            return null;

        var source = await _items.GetItemAsync(repositoryId, tenantId, itemId, cancellationToken);
        if (source == null)
            return null;

        var matchCriteria = BuildMatchCriteria(
            sourceRepo,
            source.Fields,
            useAllRepositoryFields,
            fields,
            value);
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
        // Exact/score: only return rows with matchScore >= 50 (e.g. 8/15 → 53, 7/14 → 50).
        var minRequired = requireAllFields
            ? Math.Max(1, (int)Math.Ceiling(matchCriteria.Count * 0.5))
            : Math.Min(2, matchCriteria.Count);
        const int exactMinScore = 50;

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

        IEnumerable<RepositoryRelatedDocumentDto> candidates = bag;
        if (requireAllFields)
            candidates = candidates.Where(x => x.MatchScore >= exactMinScore);

        var ordered = candidates
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
    /// (skips DYNAMIC_TABLE / line-item payloads), or only the requested field(s).
    /// </summary>
    private static List<MatchCriterion> BuildMatchCriteria(
        RepositoryDetailDto sourceRepo,
        IReadOnlyDictionary<string, object?> fields,
        bool useAllRepositoryFields,
        IReadOnlyList<string>? requestedFields = null,
        string? overrideValue = null)
    {
        var requested = NormalizeRequestedFields(requestedFields);

        IEnumerable<RepositoryFieldDto> sourceFields = useAllRepositoryFields
            ? sourceRepo.Fields
                .Where(f => !IsExcludedFromExactMatch(f.DataType))
                .OrderBy(f => f.OrderId ?? int.MaxValue)
                .ThenBy(f => f.Level)
                .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            : RepositoryFolderStructureHelper.OrderFolderFields(
                sourceRepo.Fields.Where(f => f.IncludeInFolderStructure));

        if (requested.Count > 0)
        {
            sourceFields = sourceFields.Where(f =>
                requested.Any(r =>
                    string.Equals(r, f.SqlColumnName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(r, f.Name, StringComparison.OrdinalIgnoreCase)
                    || AliasesFor(f.SqlColumnName, f.Name).Any(a => string.Equals(a, r, StringComparison.OrdinalIgnoreCase))));
        }

        var criteria = new List<MatchCriterion>();
        foreach (var field in sourceFields)
        {
            var aliases = AliasesFor(field.SqlColumnName, field.Name);
            string? value;
            if (!string.IsNullOrWhiteSpace(overrideValue) && requested.Count == 1)
            {
                value = overrideValue.Trim();
            }
            else
            {
                value = ResolveFieldValue(fields, aliases);
            }

            if (string.IsNullOrWhiteSpace(value))
                continue;

            criteria.Add(new MatchCriterion(field.SqlColumnName, NormalizeMatchValue(value, field.DataType), aliases));
        }

        // Particular field + override value, but field not in repo definitions — still search aliases.
        if (criteria.Count == 0
            && useAllRepositoryFields
            && requested.Count == 1
            && !string.IsNullOrWhiteSpace(overrideValue))
        {
            var key = requested[0];
            var aliases = AliasesFor(key, key);
            criteria.Add(new MatchCriterion(key, NormalizeMatchValue(overrideValue.Trim(), null), aliases));
            return criteria;
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

    private static List<string> NormalizeRequestedFields(IReadOnlyList<string>? requestedFields)
    {
        if (requestedFields == null || requestedFields.Count == 0)
            return [];

        return requestedFields
            .SelectMany(f => (f ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
            SELECT "Id", "Name", "ItemsTableName"
            FROM repository."Repositories"
            WHERE "TenantId" = @TenantId AND "IsDeleted" = false
            ORDER BY "Name";
            """;

        var list = new List<(Guid, string, string)>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, connection);
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
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var columns = await RepositoryItemTableColumns.LoadAsync(connection, repo.ItemsTableName, cancellationToken);
        if (minRequired <= 0 || matchCriteria.Count == 0)
            return [];

        var where = new List<string> { "i.repository_id = @RepositoryId", "i.is_deleted = false" };
        var parameters = new List<NpgsqlParameter> { new("@RepositoryId", repo.Id) };
        var matchScoreParts = new List<string>();
        var resolvedKeys = new List<string>();

        for (var i = 0; i < matchCriteria.Count; i++)
        {
            var criterion = matchCriteria[i];
            var col = ResolveCanonical(columns, criterion.Aliases);
            if (col == null)
                continue; // missing column = unmatched toward score denominator

            var paramName = $"@m{i}";
            parameters.Add(new NpgsqlParameter(paramName, criterion.Value));
            matchScoreParts.Add($"(CASE WHEN i.{RepositorySqlHelper.PhysicalColumnRef(col)} = {paramName} THEN 1 ELSE 0 END)");
            resolvedKeys.Add(criterion.Key);
        }

        if (matchScoreParts.Count == 0 || matchScoreParts.Count < minRequired)
            return [];

        var matchCountExpr = $"({string.Join(" + ", matchScoreParts)})";
        where.Add($"{matchCountExpr} >= @MinRequired");
        parameters.Add(new NpgsqlParameter("@MinRequired", minRequired));

        if (repo.Id == sourceRepositoryId)
        {
            where.Add("i.id <> @SourceItemId");
            parameters.Add(new NpgsqlParameter("@SourceItemId", sourceItemId));
        }

        var supplierCol = ResolveCanonical(columns, SupplierAliases);
        var poCol = ResolveCanonical(columns, PoAliases);
        var invoiceCol = ResolveCanonical(columns, InvoiceAliases);
        var docTypeCol = ResolveCanonical(columns, DocumentTypeAliases);
        var createdCol = RepositoryItemTableColumns.Has(columns, "CreatedAtUtc") ? "created_at_utc" : null;

        var selectSupplier = supplierCol != null ? $"i.{RepositorySqlHelper.PhysicalColumnRef(supplierCol)}" : "CAST(NULL AS varchar(1))";
        var selectPo = poCol != null ? $"i.{RepositorySqlHelper.PhysicalColumnRef(poCol)}" : "CAST(NULL AS varchar(1))";
        var selectInvoice = invoiceCol != null ? $"i.{RepositorySqlHelper.PhysicalColumnRef(invoiceCol)}" : "CAST(NULL AS varchar(1))";
        var selectDocType = docTypeCol != null ? $"i.{RepositorySqlHelper.PhysicalColumnRef(docTypeCol)}" : "CAST(NULL AS varchar(1))";
        var selectCreated = createdCol != null ? $"i.{createdCol}" : "CAST(NULL AS timestamptz)";
        var orderBy = createdCol != null
            ? $"{matchCountExpr} DESC, i.{createdCol} DESC, i.id DESC"
            : $"{matchCountExpr} DESC, i.id DESC";

        var fieldFlagSelects = new List<string>();
        for (var i = 0; i < matchScoreParts.Count; i++)
            fieldFlagSelects.Add($"{matchScoreParts[i]} AS f{i}");

        // Denominator: all source criteria (e.g. 14). Numerator: how many matched (e.g. 10 → 71).
        var totalFields = Math.Max(1, matchCriteria.Count);
        var table = RepositorySqlHelper.QualifiedItemsTable(repo.ItemsTableName);
        var sql = $"""
            SELECT
                i.id,
                i.file_name,
                i.file_type,
                i.file_size,
                {selectDocType} AS document_type,
                {selectSupplier} AS supplier,
                {selectPo} AS po_number,
                {selectInvoice} AS invoice_number,
                {selectCreated} AS created_at_utc,
                {matchCountExpr} AS match_count,
                {string.Join(",\n                ", fieldFlagSelects)}
            FROM {table} i
            WHERE {string.Join(" AND ", where)}
            ORDER BY {orderBy}
            LIMIT @Limit;
            """;

        var list = new List<RepositoryRelatedDocumentDto>();
        await using var cmd = new NpgsqlCommand(sql, connection);
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
                reader.IsDBNull(4) ? null : Convert.ToString(reader.GetValue(4)),
                reader.IsDBNull(5) ? null : Convert.ToString(reader.GetValue(5)),
                reader.IsDBNull(6) ? null : Convert.ToString(reader.GetValue(6)),
                reader.IsDBNull(7) ? null : Convert.ToString(reader.GetValue(7)),
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

    private static async Task EnsureRelatedSchemaAsync(string connectionString, CancellationToken cancellationToken)
    {
        if (SchemaEnsured.ContainsKey(connectionString))
            return;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(EnsureRelatedSchemaSql, connection) { CommandTimeout = 120 };
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        SchemaEnsured.TryAdd(connectionString, 0);
    }

    private async Task<(string? RepositoryName, string? FileName, string? FileType, int? FileSize, string? DocumentType, string? Supplier, string? PoNumber, string? InvoiceNumber)?> TryLoadRelatedItemAsync(
        string connectionString,
        Guid tenantId,
        Guid relatedRepositoryId,
        Guid relatedItemId,
        CancellationToken cancellationToken)
    {
        _ = connectionString;
        try
        {
            var repo = await _provisioner.GetRepositoryAsync(relatedRepositoryId, tenantId, cancellationToken);
            var item = await _items.GetItemAsync(relatedRepositoryId, tenantId, relatedItemId, cancellationToken);
            if (item == null)
                return null;

            return (
                repo?.Name,
                item.FileName,
                item.FileType,
                item.FileSize,
                ResolveFieldValue(item.Fields, DocumentTypeAliases),
                ResolveFieldValue(item.Fields, SupplierAliases),
                ResolveFieldValue(item.Fields, PoAliases),
                ResolveFieldValue(item.Fields, InvoiceAliases));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Failed to load related item {RelatedItemId} in repository {RelatedRepositoryId}.",
                relatedItemId,
                relatedRepositoryId);
            return null;
        }
    }

    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private const string EnsureRelatedSchemaSql = """
        CREATE TABLE IF NOT EXISTS repository."ItemRelatedDocuments" (
            "Id"                    uuid NOT NULL CONSTRAINT "PK_ItemRelatedDocuments" PRIMARY KEY,
            "TenantId"              uuid NOT NULL,
            "RepositoryId"          uuid NOT NULL,
            "ItemId"                uuid NOT NULL,
            "RelatedRepositoryId"   uuid NOT NULL,
            "RelatedItemId"         uuid NOT NULL,
            "MatchField"            varchar(128) NULL,
            "MatchValue"            varchar(450) NULL,
            "MatchScore"            integer NULL,
            "CreatedBy"             uuid NULL,
            "CreatedAtUtc"          timestamptz NOT NULL CONSTRAINT "DF_ItemRelatedDocuments_CreatedAtUtc" DEFAULT now(),
            "IsDeleted"             boolean NOT NULL CONSTRAINT "DF_ItemRelatedDocuments_IsDeleted" DEFAULT false
        );
        CREATE INDEX IF NOT EXISTS "IX_ItemRelatedDocuments_Source"
            ON repository."ItemRelatedDocuments" ("TenantId", "RepositoryId", "ItemId", "IsDeleted", "CreatedAtUtc");
        """;

    private sealed record MatchCriterion(string Key, string Value, string[] Aliases);
}
