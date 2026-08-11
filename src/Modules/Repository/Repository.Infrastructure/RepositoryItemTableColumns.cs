using Npgsql;

namespace SaaSApp.Repository.Infrastructure;

internal static class RepositoryItemTableColumns
{
    public static Task<HashSet<string>> LoadAsync(
        NpgsqlConnection connection,
        string itemsTableName,
        CancellationToken cancellationToken = default) =>
        LoadAsync(connection, itemsTableName, transaction: null, cancellationToken);

    public static async Task<HashSet<string>> LoadAsync(
        NpgsqlConnection connection,
        string itemsTableName,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = 'repository' AND table_name = @TableName;
            """;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = new NpgsqlCommand(sql, connection, transaction);
        cmd.Parameters.AddWithValue("@TableName", itemsTableName);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            set.Add(reader.GetString(0));

        return set;
    }

    /// <summary>
    /// Accepts either a historical PascalCase reserved-column literal (the vast majority of
    /// call sites, unmodified from the pre-port code) or an already-physical name (snake_case
    /// reserved, or a custom column's original casing) -- routed through
    /// RepositorySqlHelper.ToPhysicalName so both forms resolve to the same physical column
    /// before the membership check. Without this normalization, every pre-existing
    /// `Has(tableColumns, "TenantId")`-style call would silently return false against the
    /// physical "tenant_id" column.
    /// </summary>
    public static bool Has(HashSet<string> columns, string name) =>
        columns.Contains(RepositorySqlHelper.ToPhysicalName(name));

    /// <summary>Returns the exact table column name for a case-insensitive match (see <see cref="Has"/> on name normalization).</summary>
    public static bool TryGetCanonicalName(HashSet<string> columns, string name, out string canonical)
    {
        var physical = RepositorySqlHelper.ToPhysicalName(name);
        foreach (var col in columns)
        {
            if (!string.Equals(col, physical, StringComparison.OrdinalIgnoreCase))
                continue;

            canonical = col;
            return true;
        }

        canonical = string.Empty;
        return false;
    }
}
