using Npgsql;
using NpgsqlTypes;
using SaaSApp.Repository.Infrastructure.Storage;

namespace SaaSApp.Repository.Infrastructure;

internal static class RepositoryItemVersionResolver
{
    public static async Task EnsureFileVersionColumnAsync(
        NpgsqlConnection connection,
        string itemsTableName,
        CancellationToken cancellationToken)
    {
        if (!RepositorySqlHelper.IsValidItemsTableName(itemsTableName))
            return;

        var tableColumns = await RepositoryItemTableColumns.LoadAsync(connection, itemsTableName, cancellationToken);
        if (RepositoryItemTableColumns.Has(tableColumns, "FileVersion"))
            return;

        var sql = $"ALTER TABLE repository.{itemsTableName} ADD COLUMN IF NOT EXISTS file_version integer NOT NULL DEFAULT 1;";

        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Next version for the same repository folder + file name (1 = first upload).
    /// </summary>
    public static async Task<int> ResolveNextFileVersionAsync(
        NpgsqlConnection connection,
        string itemsTableName,
        Guid tenantId,
        Guid repositoryId,
        Guid? folderId,
        string fileName,
        CancellationToken cancellationToken)
    {
        await EnsureFileVersionColumnAsync(connection, itemsTableName, cancellationToken);

        var tableColumns = await RepositoryItemTableColumns.LoadAsync(connection, itemsTableName, cancellationToken);
        if (!RepositoryItemTableColumns.Has(tableColumns, "FileVersion"))
            return 1;

        var baseFileName = RepositoryFilePathHelper.GetBaseFileName(fileName);
        if (string.IsNullOrWhiteSpace(baseFileName))
            return 1;

        var versionedLike = RepositoryFilePathHelper.BuildVersionedFileNameLikePattern(baseFileName);

        var table = RepositorySqlHelper.QualifiedItemsTable(itemsTableName);
        var sql = $"""
            SELECT MAX(COALESCE(file_version, 1))
            FROM {table}
            WHERE tenant_id = @TenantId
              AND repository_id = @RepositoryId
              AND is_deleted = false
              AND (
                    file_name = @BaseFileName
                 OR file_name LIKE @VersionedLike
              )
              AND (
                    (@FolderId IS NULL AND folder_id IS NULL)
                 OR folder_id = @FolderId
              );
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        cmd.Parameters.AddWithValue("@RepositoryId", repositoryId);
        cmd.Parameters.AddWithValue("@BaseFileName", baseFileName);
        cmd.Parameters.AddWithValue("@VersionedLike", versionedLike);
        // Explicit NpgsqlDbType.Uuid (not AddWithValue): @FolderId is also used in a bare
        // "@FolderId IS NULL" test with no column context at that node, and Postgres can fail
        // to infer the parameter's type from the other, typed usage ("folder_id = @FolderId")
        // alone -- confirmed empirically elsewhere in this migration (42P08 "could not
        // determine data type of parameter"), so this is typed defensively up front.
        cmd.Parameters.Add(new NpgsqlParameter("FolderId", NpgsqlDbType.Uuid)
        {
            Value = (object?)folderId ?? DBNull.Value
        });

        var scalar = await cmd.ExecuteScalarAsync(cancellationToken);
        if (scalar is null or DBNull)
            return 1;

        var max = Convert.ToInt32(scalar);
        return max < 1 ? 1 : max + 1;
    }
}
