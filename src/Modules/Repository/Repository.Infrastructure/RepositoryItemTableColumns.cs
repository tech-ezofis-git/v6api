using Microsoft.Data.SqlClient;

namespace SaaSApp.Repository.Infrastructure;

internal static class RepositoryItemTableColumns
{
    public static Task<HashSet<string>> LoadAsync(
        SqlConnection connection,
        string itemsTableName,
        CancellationToken cancellationToken = default) =>
        LoadAsync(connection, itemsTableName, transaction: null, cancellationToken);

    public static async Task<HashSet<string>> LoadAsync(
        SqlConnection connection,
        string itemsTableName,
        SqlTransaction? transaction,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT c.name
            FROM sys.columns c
            INNER JOIN sys.tables t ON t.object_id = c.object_id
            INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = N'repository' AND t.name = @TableName;
            """;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = new SqlCommand(sql, connection, transaction);
        cmd.Parameters.AddWithValue("@TableName", itemsTableName);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            set.Add(reader.GetString(0));

        return set;
    }

    public static bool Has(HashSet<string> columns, string name) =>
        columns.Contains(name);

    /// <summary>Returns the exact table column name for a case-insensitive match.</summary>
    public static bool TryGetCanonicalName(HashSet<string> columns, string name, out string canonical)
    {
        foreach (var col in columns)
        {
            if (!string.Equals(col, name, StringComparison.OrdinalIgnoreCase))
                continue;

            canonical = col;
            return true;
        }

        canonical = string.Empty;
        return false;
    }
}
