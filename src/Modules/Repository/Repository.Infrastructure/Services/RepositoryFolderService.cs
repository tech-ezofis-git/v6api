using Npgsql;
using NpgsqlTypes;
using SaaSApp.MultiTenancy;
using SaaSApp.Repository.Application.Contracts;
using SaaSApp.Repository.Infrastructure.Storage;

namespace SaaSApp.Repository.Infrastructure.Services;

public sealed class RepositoryFolderService : IRepositoryFolderService
{
    private readonly ITenantConnectionProvider _connectionProvider;

    public RepositoryFolderService(ITenantConnectionProvider connectionProvider) =>
        _connectionProvider = connectionProvider;

    public async Task<RepositoryFolderResolveResult?> ResolveOrCreateFolderPathAsync(
        Guid repositoryId,
        Guid tenantId,
        IReadOnlyDictionary<string, string> metadata,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var repositoryName = await LoadRepositoryNameAsync(connection, repositoryId, tenantId, cancellationToken)
            ?? throw new InvalidOperationException("Repository not found.");

        var folderFields = RepositoryFolderStructureHelper.OrderFolderFields(
            await LoadFolderStructureFieldsAsync(connection, repositoryId, cancellationToken));
        if (folderFields.Count == 0)
            return null;

        var missing = new List<string>();
        var folderNames = new List<string>();

        foreach (var field in folderFields)
        {
            var segmentName = RepositoryFolderMetadataResolver.ResolveSegmentName(metadata, field);
            if (string.IsNullOrWhiteSpace(segmentName))
            {
                if (field.IsMandatory)
                {
                    missing.Add($"{field.Name} (sql: {field.SqlColumnName}, level: {field.Level})");
                    continue;
                }

                segmentName = RepositoryFolderMetadataResolver.DefaultSegmentForMissingField(field);
            }

            folderNames.Add(segmentName);
        }

        if (missing.Count > 0)
        {
            var receivedKeys = metadata.Count > 0
                ? string.Join(", ", metadata.Keys)
                : "(none — send form field 'metadata' on multipart upload)";

            throw new InvalidOperationException(
                "Repository folder path requires mandatory metadata from repository.RepositoryFields. " +
                $"Missing: {string.Join("; ", missing)}. " +
                $"Received metadata keys: {receivedKeys}. " +
                "Use POST .../items/upload-archive: either one form field metadata = full JSON object, " +
                "or separate form fields per key (Supplier, PoNumber, InvoiceNumber). " +
                "Keys must match field Name or SqlColumnName.");
        }

        Guid? parentId = null;
        string? parentPathId = null;
        var folderChain = new List<Guid>();
        var archivePrefix = $"{RepositoryFilePathHelper.ArchiveRoot}/{RepositoryFilePathHelper.SanitizePathSegment(repositoryName)}";

        for (var i = 0; i < folderFields.Count; i++)
        {
            var field = folderFields[i];
            var segmentName = folderNames[i];

            var folderId = await FindOrCreateFolderAsync(
                connection,
                tenantId,
                repositoryId,
                parentId,
                field.Level,
                segmentName,
                parentPathId,
                userId,
                cancellationToken);

            folderChain.Add(folderId);
            parentId = folderId;
            parentPathId = string.IsNullOrEmpty(parentPathId)
                ? $"{archivePrefix}/{segmentName}"
                : $"{parentPathId}/{segmentName}";
        }

        var leafFolderId = folderChain.Count > 0 ? folderChain[^1] : Guid.Empty;
        return new RepositoryFolderResolveResult(leafFolderId, folderChain, folderNames, repositoryName);    }

    private static async Task<string?> LoadRepositoryNameAsync(
        NpgsqlConnection connection,
        Guid repositoryId,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT "Name"
            FROM repository."Repositories"
            WHERE "Id" = @RepositoryId AND "TenantId" = @TenantId AND "IsDeleted" = false
            LIMIT 1;
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@RepositoryId", repositoryId);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        return (await cmd.ExecuteScalarAsync(cancellationToken)) as string;
    }

    private static async Task<IReadOnlyList<RepositoryFieldDto>> LoadFolderStructureFieldsAsync(
        NpgsqlConnection connection,
        Guid repositoryId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT "Id", "Name", "SqlColumnName", "DataType", "Level", "IsMandatory", "IncludeInFolderStructure", "OptionsJson", "OrderId", "IsReadOnly"
            FROM repository."RepositoryFields"
            WHERE "RepositoryId" = @RepositoryId
              AND "IsDeleted" = false
              AND "IncludeInFolderStructure" = true
            ORDER BY "Level", "OrderId", "Name";
            """;

        var list = new List<RepositoryFieldDto>();
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@RepositoryId", repositoryId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new RepositoryFieldDto(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4),
                reader.GetBoolean(5),
                reader.GetBoolean(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.GetBoolean(9)));
        }

        return list;
    }

    private static async Task<Guid> FindOrCreateFolderAsync(
        NpgsqlConnection connection,
        Guid tenantId,
        Guid repositoryId,
        Guid? parentId,
        int levelId,
        string name,
        string? parentPathId,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        const string findSql = """
            SELECT "Id"
            FROM repository."Folders"
            WHERE "TenantId" = @TenantId
              AND "RepositoryId" = @RepositoryId
              AND "IsDeleted" = false
              AND "LevelId" = @LevelId
              AND "Name" = @Name
              AND (
                    (@ParentId IS NULL AND "ParentId" IS NULL)
                 OR ("ParentId" = @ParentId)
              )
            LIMIT 1;
            """;

        await using (var findCmd = new NpgsqlCommand(findSql, connection))
        {
            findCmd.Parameters.AddWithValue("@TenantId", tenantId);
            findCmd.Parameters.AddWithValue("@RepositoryId", repositoryId);
            findCmd.Parameters.AddWithValue("@LevelId", levelId);
            findCmd.Parameters.AddWithValue("@Name", name);
            // Explicit NpgsqlDbType.Uuid (not AddWithValue): @ParentId is also used in a bare
            // "@ParentId IS NULL" test with no column context at that node, and Postgres can
            // fail to infer the parameter's type from the other, typed usage ("ParentId" =
            // @ParentId) alone -- confirmed empirically elsewhere in this migration (42P08
            // "could not determine data type of parameter"), so this is typed defensively.
            findCmd.Parameters.Add(new NpgsqlParameter("ParentId", NpgsqlDbType.Uuid)
            {
                Value = (object?)parentId ?? DBNull.Value
            });

            var existing = await findCmd.ExecuteScalarAsync(cancellationToken);
            if (existing is Guid g)
                return g;
        }

        var newId = Guid.NewGuid();
        var pathId = string.IsNullOrEmpty(parentPathId)
            ? name
            : $"{parentPathId}/{name}";

        const string insertSql = """
            INSERT INTO repository."Folders"
                ("Id", "TenantId", "RepositoryId", "Name", "ParentId", "LevelId", "PathId", "CreatedAtUtc", "CreatedBy", "IsDeleted")
            VALUES
                (@Id, @TenantId, @RepositoryId, @Name, @ParentId, @LevelId, @PathId, now(), @CreatedBy, false);
            """;

        await using var insertCmd = new NpgsqlCommand(insertSql, connection);
        insertCmd.Parameters.AddWithValue("@Id", newId);
        insertCmd.Parameters.AddWithValue("@TenantId", tenantId);
        insertCmd.Parameters.AddWithValue("@RepositoryId", repositoryId);
        insertCmd.Parameters.AddWithValue("@Name", name);
        insertCmd.Parameters.AddWithValue("@ParentId", (object?)parentId ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("@LevelId", levelId);
        insertCmd.Parameters.AddWithValue("@PathId", pathId);
        insertCmd.Parameters.AddWithValue("@CreatedBy", (object?)userId ?? DBNull.Value);
        await insertCmd.ExecuteNonQueryAsync(cancellationToken);

        return newId;
    }
}
