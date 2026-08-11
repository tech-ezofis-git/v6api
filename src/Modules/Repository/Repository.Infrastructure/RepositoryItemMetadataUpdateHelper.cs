using Npgsql;
using NpgsqlTypes;
using SaaSApp.Repository.Application.Contracts;

namespace SaaSApp.Repository.Infrastructure;

internal static class RepositoryItemMetadataUpdateHelper
{
    private static readonly HashSet<string> ReadOnlyColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "Id", "TenantId", "RepositoryId", "StorageProviderId",
        "FilePath", "FileName", "FileType", "FileSize", "TotalPages",
        "CreatedAtUtc", "CreatedBy", "IsDeleted", "ValidFrom", "ValidTo",
        "ModifiedAtUtc", "ModifiedBy"
    };

    public static async Task<int> UpdateAsync(
        NpgsqlConnection connection,
        RepositoryDetailDto repo,
        Guid tenantId,
        Guid repositoryId,
        Guid itemId,
        IReadOnlyDictionary<string, string> metadata,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        if (metadata.Count == 0)
            throw new ArgumentException("At least one metadata field is required.");

        var table = RepositorySqlHelper.QualifiedItemsTable(repo.ItemsTableName);
        var tableColumns = await RepositoryItemTableColumns.LoadAsync(connection, repo.ItemsTableName, cancellationToken);
        var allowedColumns = RepositoryItemFilterHelper.BuildFilterableColumns(repo, tableColumns);

        var columnUpdates = BuildColumnUpdates(metadata, repo, allowedColumns, tableColumns);
        if (columnUpdates.Count == 0)
        {
            throw new ArgumentException(
                "No valid metadata fields to update. Use GET .../items/filter-fields for allowed keys that exist on this repository table.");
        }

        var sets = new List<string>();
        var parameters = new List<NpgsqlParameter>();
        var index = 0;

        foreach (var (column, value) in columnUpdates)
        {
            var param = $"@U{index++}";
            sets.Add($"{RepositorySqlHelper.PhysicalColumnRef(column)} = {param}");
            parameters.Add(CreateParameter(param, column, value));
        }

        sets.Add("modified_at_utc = now()");
        sets.Add("modified_by = @ModifiedBy");

        var sql = $"""
            UPDATE {table}
            SET {string.Join(", ", sets)}
            WHERE id = @ItemId AND tenant_id = @TenantId AND repository_id = @RepositoryId AND is_deleted = false;
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        foreach (var p in parameters)
            cmd.Parameters.Add(p);
        cmd.Parameters.AddWithValue("@ModifiedBy", (object?)userId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ItemId", itemId);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        cmd.Parameters.AddWithValue("@RepositoryId", repositoryId);

        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return rows == 0 ? -1 : columnUpdates.Count;
    }

    private static Dictionary<string, object?> BuildColumnUpdates(
        IReadOnlyDictionary<string, string> metadata,
        RepositoryDetailDto repo,
        HashSet<string> allowedColumns,
        HashSet<string> tableColumns)
    {
        var updates = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, rawValue) in metadata)
        {
            if (!RepositoryItemFilterHelper.TryResolveFilterColumn(key, allowedColumns, repo, out var logicalCol))
                continue;

            if (!RepositoryItemTableColumns.Has(tableColumns, logicalCol) || ReadOnlyColumns.Contains(logicalCol))
                continue;

            var column = RepositoryItemTableColumns.TryGetCanonicalName(tableColumns, logicalCol, out var canonicalCol)
                ? canonicalCol
                : logicalCol;

            updates[column] = CoerceValue(column, rawValue, repo.Fields);
        }

        return updates;
    }

    private static object? CoerceValue(string column, string rawValue, IReadOnlyList<RepositoryFieldDto> fields)
    {
        if (string.Equals(column, "Amount", StringComparison.OrdinalIgnoreCase)
            || string.Equals(column, "POAmount", StringComparison.OrdinalIgnoreCase)
            || string.Equals(column, "InvoiceAmount", StringComparison.OrdinalIgnoreCase)
            || string.Equals(column, "InvoiceTaxAmount", StringComparison.OrdinalIgnoreCase))
        {
            return decimal.TryParse(rawValue, out var amount) ? amount : rawValue;
        }

        if (string.Equals(column, "DocumentDate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(column, "PODate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(column, "InvoiceDate", StringComparison.OrdinalIgnoreCase))
        {
            return DateTime.TryParse(rawValue, out var date) ? date.Date : rawValue;
        }

        if (string.Equals(column, "OcrScore", StringComparison.OrdinalIgnoreCase))
        {
            return byte.TryParse(rawValue, out var score) ? score : rawValue;
        }

        // Fall back to the repository's own field-definition DataType for arbitrary custom columns not
        // in the fixed "core column" list above (same 42804 text-into-typed-column issue as the insert
        // path -- see RepositoryFieldValueCoercion).
        return RepositoryFieldValueCoercion.TryCoerce(fields, column, rawValue) ?? rawValue;
    }

    private static NpgsqlParameter CreateParameter(string name, string column, object? value)
    {
        if (value is byte b)
            // ocr_score is smallint (Postgres has no 1-byte integer type); widen to short.
            return new NpgsqlParameter(name, NpgsqlDbType.Smallint) { Value = (short)b };

        // decimal/DateTime/int/bool (from RepositoryFieldValueCoercion.TryCoerce, custom-field fallback
        // above) and the plain-string/DBNull case all share the same typed-parameter construction.
        return RepositoryFieldValueCoercion.CreateParameter(name, value);
    }
}
