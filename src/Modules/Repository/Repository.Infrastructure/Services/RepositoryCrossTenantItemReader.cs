using Npgsql;
using SaaSApp.Repository.Application.Contracts;

namespace SaaSApp.Repository.Infrastructure.Services;

/// <summary>Reads repository items from an explicit tenant connection (cross-tenant share).</summary>
internal static class RepositoryCrossTenantItemReader
{
    public static async Task<RepositoryDetailDto?> GetRepositoryAsync(
        string connectionString,
        Guid tenantId,
        Guid repositoryId,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT
                r."Id",
                r."Name",
                r."Description",
                r."StorageProviderId",
                r."StorageDrive",
                r."ItemsTableName",
                r."StageTableName",
                r."IsDefaultRepository",
                r."IsDeleted",
                sp."Code" AS "StorageProviderCode",
                sp."Name" AS "StorageProviderName"
            FROM repository."Repositories" r
            LEFT JOIN repository."StorageProviders" sp ON sp."Id" = r."StorageProviderId" AND sp."IsDeleted" = false
            WHERE r."Id" = @Id AND r."TenantId" = @TenantId;
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", repositoryId);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        Guid id;
        string name;
        string? description;
        Guid storageProviderId;
        string? storageDrive;
        string itemsTableName;
        string stageTableName;
        bool isDefaultRepository;
        bool isDeleted;
        string? storageProviderCode;
        string? storageProviderName;

        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            id = reader.GetGuid(0);
            name = reader.GetString(1);
            description = reader.IsDBNull(2) ? null : reader.GetString(2);
            storageProviderId = reader.GetGuid(3);
            storageDrive = reader.IsDBNull(4) ? null : reader.GetString(4);
            itemsTableName = reader.GetString(5);
            stageTableName = reader.GetString(6);
            isDefaultRepository = !reader.IsDBNull(7) && reader.GetBoolean(7);
            isDeleted = !reader.IsDBNull(8) && reader.GetBoolean(8);
            storageProviderCode = reader.IsDBNull(9) ? null : reader.GetString(9);
            storageProviderName = reader.IsDBNull(10) ? null : reader.GetString(10);
        }

        var fields = await LoadFieldsAsync(connection, repositoryId, cancellationToken);
        var fileCount = await CountItemsAsync(connection, itemsTableName, cancellationToken);

        return new RepositoryDetailDto(
            id,
            name,
            description,
            storageProviderId,
            storageDrive,
            itemsTableName,
            stageTableName,
            isDefaultRepository,
            fields,
            fileCount,
            Status: isDeleted ? "Inactive" : "Active",
            StorageProviderCode: storageProviderCode,
            StorageProviderName: storageProviderName);
    }

    private static async Task<int> CountItemsAsync(
        NpgsqlConnection connection,
        string itemsTableName,
        CancellationToken cancellationToken)
    {
        if (!RepositorySqlHelper.IsValidItemsTableName(itemsTableName))
            return 0;

        const string existsSql = """
            SELECT 1 FROM information_schema.tables
            WHERE table_name = @Name AND table_schema = 'repository';
            """;
        await using (var existsCmd = new NpgsqlCommand(existsSql, connection))
        {
            existsCmd.Parameters.AddWithValue("@Name", itemsTableName);
            if (await existsCmd.ExecuteScalarAsync(cancellationToken) is null)
                return 0;
        }

        var table = RepositorySqlHelper.QualifiedItemsTable(itemsTableName);
        var sql = $"SELECT COUNT(*) FROM {table} WHERE is_deleted = false;";
        await using var cmd = new NpgsqlCommand(sql, connection);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        if (result == null || result == DBNull.Value)
            return 0;

        var count = Convert.ToInt64(result);
        return count > int.MaxValue ? int.MaxValue : (int)count;
    }

    public static async Task<RepositoryItemDetailDto?> GetItemAsync(
        string connectionString,
        RepositoryDetailDto repository,
        Guid repositoryId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var table = RepositorySqlHelper.QualifiedItemsTable(repository.ItemsTableName);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var tableColumns = await RepositoryItemTableColumns.LoadAsync(connection, repository.ItemsTableName, cancellationToken);
        // Explicit aliased select instead of `i.*` -- see RepositoryItemQueryService.GetItemAsync
        // for why (reserved physical columns are lowercase snake_case now).
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

        return new RepositoryItemDetailDto(
            itemId,
            fields.TryGetValue("FileName", out var fn) ? fn?.ToString() : null,
            fields.TryGetValue("FilePath", out var fp) ? fp?.ToString() : null,
            fields.TryGetValue("FileType", out var ft) ? ft?.ToString() : null,
            fields.TryGetValue("FileSize", out var fs) && fs != null ? Convert.ToInt32(fs) : null,
            reader.GetGuid(reader.GetOrdinal("StorageProviderId")),
            providerCode,
            fields);
    }

    public static async Task TryResolveCreatedByEmailAsync(
        string connectionString,
        IDictionary<string, object?> fields,
        CancellationToken cancellationToken)
    {
        if (!fields.TryGetValue("CreatedBy", out var createdByRaw)
            || !RepositoryUserNameResolver.TryParseUserId(createdByRaw, out var createdById))
        {
            return;
        }

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
            fields["CreatedBy"] = createdById;
            fields["CreatedByName"] = email;
        }
    }

    private static async Task<IReadOnlyList<RepositoryFieldDto>> LoadFieldsAsync(
        NpgsqlConnection connection,
        Guid repositoryId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT "Id", "Name", "SqlColumnName", "DataType", "Level", "IsMandatory", "IncludeInFolderStructure", "OptionsJson", "OrderId", "IsReadOnly"
            FROM repository."RepositoryFields"
            WHERE "RepositoryId" = @RepositoryId AND "IsDeleted" = false
            ORDER BY "OrderId", "Name";
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
}
