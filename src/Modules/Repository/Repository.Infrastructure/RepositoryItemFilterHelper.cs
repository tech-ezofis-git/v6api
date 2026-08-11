using System.Text.Json;
using Npgsql;
using SaaSApp.Repository.Application;
using SaaSApp.Repository.Application.Contracts;

namespace SaaSApp.Repository.Infrastructure;

internal static class RepositoryItemFilterHelper
{
    private static readonly string[] BuiltInOperationalColumns =
    [
        "FileName", "Status", "OcrScore", "AiStatus", "CreatedAtUtc", "ModifiedAtUtc"
    ];

    public static ItemListFilterSchemaDto BuildFilterSchema(RepositoryDetailDto repo)
    {
        var fields = new List<ItemListFilterFieldDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in repo.Fields.OrderBy(f => f.Level).ThenBy(f => f.Name))
        {
            var sqlColumn = RepositorySqlHelper.SanitizeColumnName(field.SqlColumnName);
            if (!seen.Add(sqlColumn))
                continue;

            fields.Add(new ItemListFilterFieldDto(field.Name, sqlColumn, field.DataType));
        }

        return new ItemListFilterSchemaDto(fields);
    }

    public static HashSet<string> BuildFilterableColumns(
        RepositoryDetailDto repo,
        IReadOnlySet<string>? tableColumns = null)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in BuildFilterSchema(repo).Fields)
            set.Add(f.SqlColumnName);

        foreach (var col in BuiltInOperationalColumns)
        {
            // tableColumns holds physical names (snake_case for reserved) -- normalize before
            // checking membership, same reasoning as RepositoryItemTableColumns.Has.
            if (tableColumns is null || tableColumns.Contains(RepositorySqlHelper.ToPhysicalName(col)))
                set.Add(col);
        }

        return set;
    }

    public static IReadOnlyDictionary<string, string> ParseFilters(string? json) =>
        RepositoryMetadataParser.Parse(json);

    public static IReadOnlyDictionary<string, string> ParseMetadataJson(string? json) =>
        RepositoryMetadataParser.Parse(json);

    /// <summary>
    /// Parse item/browse filters. Supports one or many values per column:
    /// <c>{"Status":"Verifier"}</c>,
    /// <c>{"Status":["Verifier","Approved"]}</c>,
    /// <c>{"Status":"Verifier,Approved"}</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseItemFilters(string? json)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
            return result;

        try
        {
            using var doc = JsonDocument.Parse(json.Trim());
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    "filters must be a JSON object, e.g. {\"Status\":[\"Verifier\",\"Approved\"],\"Supplier\":\"Acme\"}.");
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(prop.Name))
                    continue;

                var values = ReadFilterValues(prop.Value);
                if (values.Count == 0)
                    continue;

                result[prop.Name.Trim()] = values;
            }

            return result;
        }
        catch (JsonException ex)
        {
            throw new ArgumentException(
                $"filters must be a JSON object, e.g. {{\"Status\":[\"Verifier\",\"Approved\"]}}. {ex.Message}");
        }
    }

    private static IReadOnlyList<string> ReadFilterValues(JsonElement element)
    {
        var values = new List<string>();
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var text = FilterValueToString(item);
                    if (!string.IsNullOrWhiteSpace(text))
                        values.Add(text.Trim());
                }
                break;

            case JsonValueKind.String:
                var raw = element.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(raw))
                    break;

                // If client sent a stringified JSON array, parse it (common with query-string encoding).
                if (raw.StartsWith('[') && raw.EndsWith(']'))
                {
                    try
                    {
                        using var nested = JsonDocument.Parse(raw);
                        if (nested.RootElement.ValueKind == JsonValueKind.Array)
                            return ReadFilterValues(nested.RootElement);
                    }
                    catch (JsonException)
                    {
                        // fall through -- treat as a single literal value
                    }
                }

                // Do NOT split on commas -- supplier names often contain commas ("Acme, Inc.").
                values.Add(raw);
                break;

            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                values.Add(element.GetRawText());
                break;
        }

        return values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? FilterValueToString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => null,
        _ => element.ToString()
    };

    public static bool TryResolveFilterColumn(
        string key,
        HashSet<string> allowedColumns,
        RepositoryDetailDto repo,
        out string column)
    {
        column = string.Empty;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        if (allowedColumns.Contains(key))
        {
            var matchedField = repo.Fields.FirstOrDefault(f =>
                string.Equals(f.SqlColumnName, key, StringComparison.OrdinalIgnoreCase)
                || string.Equals(f.Name, key, StringComparison.OrdinalIgnoreCase));

            column = RepositorySqlHelper.SanitizeColumnName(matchedField?.SqlColumnName ?? key);
            return true;
        }

        var field = repo.Fields.FirstOrDefault(f =>
            string.Equals(f.Name, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(f.SqlColumnName, key, StringComparison.OrdinalIgnoreCase));

        if (field != null && allowedColumns.Contains(field.SqlColumnName))
        {
            column = RepositorySqlHelper.SanitizeColumnName(field.SqlColumnName);
            return true;
        }

        return false;
    }

    public static string ResolveFilterColumn(string key, HashSet<string> allowedColumns, RepositoryDetailDto repo)
    {
        if (TryResolveFilterColumn(key, allowedColumns, repo, out var column))
            return column;

        throw new ArgumentException(
            $"Unknown filter field '{key}'. Use GET .../items/filter-fields for allowed keys.");
    }

    public static void ApplyEqualityFilters(
        ICollection<string> where,
        IList<NpgsqlParameter> parameters,
        IReadOnlyDictionary<string, string> filters,
        HashSet<string> allowedColumns,
        RepositoryDetailDto repo,
        string tableAlias = "i")
    {
        var multi = filters.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<string>)new[] { kv.Value },
            StringComparer.OrdinalIgnoreCase);
        ApplyEqualityFilters(where, parameters, multi, allowedColumns, repo, tableAlias);
    }

    public static void ApplyEqualityFilters(
        ICollection<string> where,
        IList<NpgsqlParameter> parameters,
        IReadOnlyDictionary<string, IReadOnlyList<string>> filters,
        HashSet<string> allowedColumns,
        RepositoryDetailDto repo,
        string tableAlias = "i")
    {
        var index = 0;
        foreach (var (key, values) in filters)
        {
            var col = ResolveFilterColumn(key, allowedColumns, repo);
            var colRef = RepositorySqlHelper.ColumnRef(col);
            var prefix = string.IsNullOrEmpty(tableAlias) ? string.Empty : $"{tableAlias}.";
            var cleaned = values
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (cleaned.Count == 0)
                continue;

            if (cleaned.Count == 1)
            {
                var param = $"@F{index++}";
                // Trim DB side so trailing spaces do not hide matches.
                where.Add($"TRIM(CAST({prefix}{colRef} AS varchar(4000))) = {param}");
                parameters.Add(new NpgsqlParameter(param, cleaned[0]));
                continue;
            }

            var paramNames = new List<string>(cleaned.Count);
            foreach (var value in cleaned)
            {
                var param = $"@F{index++}";
                paramNames.Add(param);
                parameters.Add(new NpgsqlParameter(param, value));
            }

            where.Add(
                $"TRIM(CAST({prefix}{colRef} AS varchar(4000))) IN ({string.Join(", ", paramNames)})");
        }
    }

    /// <summary>
    /// Pulls Status / StageStatus out of <paramref name="filters"/> so callers can apply
    /// <see cref="ApplyDisplayStatusFilter"/> (list Status is often workflow-enriched, not the raw column).
    /// </summary>
    public static IReadOnlyList<string> ExtractStatusFilterValues(
        IDictionary<string, IReadOnlyList<string>> filters,
        HashSet<string> allowedColumns,
        RepositoryDetailDto repo)
    {
        var combined = new List<string>();
        foreach (var key in filters.Keys.ToList())
        {
            if (!TryResolveFilterColumn(key, allowedColumns, repo, out var column))
                continue;
            if (!IsStatusFilterColumn(column))
                continue;

            if (filters.TryGetValue(key, out var values) && values.Count > 0)
                combined.AddRange(values);
            filters.Remove(key);
        }

        return combined
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsStatusFilterColumn(string column) =>
        string.Equals(column, "Status", StringComparison.OrdinalIgnoreCase)
        || string.Equals(column, "StageStatus", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Match UI Status: raw Status/StageStatus, AiStatus/MatchedStatus, or workflow display status
    /// via <paramref name="matchingWorkflowInstanceIds"/>.
    /// </summary>
    public static void ApplyDisplayStatusFilter(
        ICollection<string> where,
        IList<NpgsqlParameter> parameters,
        IReadOnlyList<string> statusValues,
        IReadOnlyList<Guid> matchingWorkflowInstanceIds,
        HashSet<string> tableColumns,
        string tableAlias = "i")
    {
        var cleaned = statusValues
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (cleaned.Count == 0)
            return;

        var prefix = string.IsNullOrEmpty(tableAlias) ? string.Empty : $"{tableAlias}.";
        var paramNames = new List<string>(cleaned.Count);
        for (var i = 0; i < cleaned.Count; i++)
        {
            var param = $"@StatusF{i}";
            paramNames.Add(param);
            parameters.Add(new NpgsqlParameter(param, cleaned[i]));
        }

        var inList = string.Join(", ", paramNames);
        var orParts = new List<string>();

        foreach (var col in new[] { "Status", "StageStatus", "AiStatus", "MatchedStatus" })
        {
            if (!RepositoryItemTableColumns.Has(tableColumns, col))
                continue;
            orParts.Add($"TRIM(CAST({prefix}{RepositorySqlHelper.ColumnRef(col)} AS varchar(4000))) IN ({inList})");
        }

        if (matchingWorkflowInstanceIds.Count > 0
            && RepositoryItemTableColumns.Has(tableColumns, "WorkflowInstanceId"))
        {
            orParts.Add($"{prefix}workflow_instance_id = ANY(@StatusWfIds)");
            parameters.Add(new NpgsqlParameter("@StatusWfIds", matchingWorkflowInstanceIds.ToArray()));
        }

        if (orParts.Count == 0)
            return;

        where.Add("(" + string.Join(" OR ", orParts) + ")");
    }

    public static string ResolveSortColumn(string sortBy, HashSet<string> allowedColumns, HashSet<string>? tableColumns = null)
    {
        var mapped = RepositorySqlHelper.MapSortColumn(sortBy);
        var candidates = SortColumnCandidates(mapped, sortBy);
        foreach (var col in candidates)
        {
            if (!allowedColumns.Contains(col))
                continue;
            if (tableColumns != null && !tableColumns.Contains(RepositorySqlHelper.ToPhysicalName(col)))
                continue;

            return col;
        }

        return allowedColumns.Contains("CreatedAtUtc") ? "CreatedAtUtc" : "FileName";
    }

    private static IEnumerable<string> SortColumnCandidates(string mapped, string rawSortBy)
    {
        yield return mapped;

        if (!string.Equals(mapped, rawSortBy, StringComparison.OrdinalIgnoreCase))
            yield return RepositorySqlHelper.SanitizeColumnName(rawSortBy);

        foreach (var col in mapped switch
        {
            "DocumentDate" => new[] { "InvoiceDate", "PODate" },
            "Supplier" => new[] { "VendorName", "Vendor" },
            "InvoiceNumber" => new[] { "InvoiceNo" },
            "Amount" => new[] { "InvoiceAmount", "POAmount" },
            "AiStatus" => new[] { "MatchedStatus" },
            "Status" => new[] { "StageStatus" },
            "OcrScore" => Array.Empty<string>(),
            _ => Array.Empty<string>()
        })
        {
            yield return col;
        }
    }
}
