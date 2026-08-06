using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SaaSApp.MultiTenancy;
using SaaSApp.Repository.Application.Contracts;
using System.Text;

namespace SaaSApp.Repository.Infrastructure.Services;

public sealed class StaticRepositoryProvisioner : IStaticRepositoryProvisioner
{
    private readonly ITenantConnectionProvider _connectionProvider;
    private readonly IRepositorySchemaService _schemaService;
    private readonly IRepositoryStorageSeedService _storageSeed;
    private readonly ILogger<StaticRepositoryProvisioner> _logger;

    public StaticRepositoryProvisioner(
        ITenantConnectionProvider connectionProvider,
        IRepositorySchemaService schemaService,
        IRepositoryStorageSeedService storageSeed,
        ILogger<StaticRepositoryProvisioner> logger)
    {
        _connectionProvider = connectionProvider;
        _schemaService = schemaService;
        _storageSeed = storageSeed;
        _logger = logger;
    }

    public async Task<CreateRepositoryResult> CreateRepositoryAsync(
        CreateRepositoryRequest request,
        Guid tenantId,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await _schemaService.ApplyBaseSchemaAsync(connectionString, cancellationToken);

        var storageProviderId = await _storageSeed.ResolveStorageProviderIdAsync(
            tenantId, request.StorageProviderId, request.StorageProviderCode, cancellationToken);

        var repoId = Guid.NewGuid();
        var itemsTable = RepositorySqlHelper.ItemsTableName(repoId);
        var stageTable = RepositorySqlHelper.StageTableName(repoId);
        var fields = NormalizeFields(request.Fields ?? Array.Empty<RepositoryFieldDefinitionDto>());

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            const string insertRepo = """
                INSERT INTO repository.Repositories
                (Id, TenantId, Name, Description, FieldsType, StorageProviderId, StorageDrive, ItemsTableName, StageTableName, IsDefaultRepository, CreatedBy)
                VALUES (@Id, @TenantId, @Name, @Description, 'STATIC', @StorageProviderId, @StorageDrive, @ItemsTableName, @StageTableName, @IsDefaultRepository, @CreatedBy);
                """;

            await using (var cmd = new SqlCommand(insertRepo, connection, tx))
            {
                cmd.Parameters.AddWithValue("@Id", repoId);
                cmd.Parameters.AddWithValue("@TenantId", tenantId);
                cmd.Parameters.AddWithValue("@Name", request.Name.Trim());
                cmd.Parameters.AddWithValue("@Description", (object?)request.Description ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@StorageProviderId", storageProviderId);
                cmd.Parameters.AddWithValue("@StorageDrive", (object?)request.StorageDrive ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ItemsTableName", itemsTable);
                cmd.Parameters.AddWithValue("@StageTableName", stageTable);
                cmd.Parameters.AddWithValue("@IsDefaultRepository", request.IsDefaultRepository);
                cmd.Parameters.AddWithValue("@CreatedBy", (object?)userId ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var field in fields)
            {
                var sqlCol = RepositoryFieldAliases.Canonicalize(field.Name);
                const string insertField = """
                    INSERT INTO repository.RepositoryFields
                    (Id, RepositoryId, Name, SqlColumnName, DataType, Level, IsMandatory, IncludeInFolderStructure, OptionsJson, OrderId, IsReadOnly, CreatedBy)
                    VALUES (NEWID(), @RepositoryId, @Name, @SqlColumnName, @DataType, @Level, @IsMandatory, @IncludeInFolderStructure, @OptionsJson, @OrderId, @IsReadOnly, @CreatedBy);
                    """;
                await using var fcmd = new SqlCommand(insertField, connection, tx);
                fcmd.Parameters.AddWithValue("@RepositoryId", repoId);
                fcmd.Parameters.AddWithValue("@Name", field.Name.Trim());
                fcmd.Parameters.AddWithValue("@SqlColumnName", sqlCol);
                fcmd.Parameters.AddWithValue("@DataType", (object?)field.DataType ?? DBNull.Value);
                fcmd.Parameters.AddWithValue("@Level", field.Level);
                fcmd.Parameters.AddWithValue("@IsMandatory", field.IsMandatory);
                fcmd.Parameters.AddWithValue("@IncludeInFolderStructure", field.IncludeInFolderStructure);
                fcmd.Parameters.AddWithValue("@OptionsJson", (object?)field.OptionsJson ?? DBNull.Value);
                fcmd.Parameters.AddWithValue("@OrderId", (object?)field.OrderId ?? DBNull.Value);
                fcmd.Parameters.AddWithValue("@IsReadOnly", field.IsReadOnly);
                fcmd.Parameters.AddWithValue("@CreatedBy", (object?)userId ?? DBNull.Value);
                await fcmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var itemsDdl = BuildItemsTableScript(repoId, itemsTable, fields);
            await using (var itemsCmd = new SqlCommand(itemsDdl, connection, tx) { CommandTimeout = 300 })
            {
                await itemsCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var stageDdl = BuildStageTableScript(repoId, stageTable, fields);
            await using (var stageCmd = new SqlCommand(stageDdl, connection, tx) { CommandTimeout = 300 })
            {
                await stageCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
            _logger.LogInformation(
                "Created repository {RepositoryId} with tables {ItemsTable} and {StageTable}",
                repoId, itemsTable, stageTable);
            return new CreateRepositoryResult(repoId, itemsTable, stageTable);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<RepositoryDetailDto?> GetRepositoryAsync(Guid repositoryId, Guid tenantId, CancellationToken cancellationToken = default)
    {
        var connectionString = RequireConnectionString();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT
                r.Id,
                r.Name,
                r.Description,
                r.StorageProviderId,
                r.StorageDrive,
                r.ItemsTableName,
                r.StageTableName,
                r.IsDefaultRepository,
                r.IsDeleted,
                sp.Code AS StorageProviderCode,
                sp.Name AS StorageProviderName
            FROM repository.Repositories r
            LEFT JOIN repository.StorageProviders sp ON sp.Id = r.StorageProviderId AND sp.IsDeleted = 0
            WHERE r.Id = @Id AND r.TenantId = @TenantId;
            """;

        await using var cmd = new SqlCommand(sql, connection);
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
            id, name, description, storageProviderId, storageDrive, itemsTableName, stageTableName,
            isDefaultRepository,
            fields,
            fileCount,
            Status: isDeleted ? "Inactive" : "Active",
            StorageProviderCode: storageProviderCode,
            StorageProviderName: storageProviderName);
    }

    public async Task<IReadOnlyList<RepositorySummaryDto>> ListRepositoriesAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var connectionString = RequireConnectionString();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT
                r.Id,
                r.Name,
                r.Description,
                r.StorageProviderId,
                r.ItemsTableName,
                r.CreatedAtUtc,
                r.IsDefaultRepository,
                r.IsDeleted,
                sp.Code AS StorageProviderCode,
                sp.Name AS StorageProviderName,
                r.CreatedBy,
                r.ModifiedBy,
                cb.Email AS CreatedByName,
                COALESCE(mb.Email, cb.Email) AS ModifiedByName
            FROM repository.Repositories r
            LEFT JOIN repository.StorageProviders sp ON sp.Id = r.StorageProviderId AND sp.IsDeleted = 0
            LEFT JOIN users.Users cb ON cb.Id = r.CreatedBy AND cb.IsDeleted = 0
            LEFT JOIN users.Users mb ON mb.Id = r.ModifiedBy AND mb.IsDeleted = 0
            WHERE r.TenantId = @TenantId
            ORDER BY r.IsDeleted, r.Name;
            """;

        var rows = new List<(
            Guid Id,
            string Name,
            string? Description,
            Guid StorageProviderId,
            string ItemsTableName,
            DateTime CreatedAtUtc,
            bool IsDefaultRepository,
            bool IsDeleted,
            string? StorageProviderCode,
            string? StorageProviderName,
            Guid? CreatedBy,
            Guid? ModifiedBy,
            string? CreatedByName,
            string? ModifiedByName)>();

        await using (var cmd = new SqlCommand(sql, connection))
        {
            cmd.Parameters.AddWithValue("@TenantId", tenantId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetGuid(3),
                    reader.GetString(4),
                    reader.GetDateTime(5),
                    !reader.IsDBNull(6) && reader.GetBoolean(6),
                    !reader.IsDBNull(7) && reader.GetBoolean(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetGuid(10),
                    reader.IsDBNull(11) ? null : reader.GetGuid(11),
                    reader.IsDBNull(12) ? null : reader.GetString(12),
                    reader.IsDBNull(13) ? null : reader.GetString(13)));
            }
        }

        var list = new List<RepositorySummaryDto>(rows.Count);
        foreach (var row in rows)
        {
            var fileCount = await CountItemsAsync(connection, row.ItemsTableName, cancellationToken);
            list.Add(new RepositorySummaryDto(
                row.Id,
                row.Name,
                row.Description,
                row.StorageProviderId,
                row.ItemsTableName,
                row.CreatedAtUtc,
                row.IsDefaultRepository,
                fileCount,
                Status: row.IsDeleted ? "Inactive" : "Active",
                StorageProviderCode: row.StorageProviderCode,
                StorageProviderName: row.StorageProviderName,
                CreatedBy: row.CreatedBy,
                ModifiedBy: row.ModifiedBy,
                CreatedByName: row.CreatedByName,
                ModifiedByName: row.ModifiedByName));
        }

        return list;
    }

    public async Task EnsureRepositoryTablesAsync(Guid repositoryId, Guid tenantId, CancellationToken cancellationToken = default)
    {
        var connectionString = RequireConnectionString();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT ItemsTableName, StageTableName
            FROM repository.Repositories
            WHERE Id = @Id AND TenantId = @TenantId AND IsDeleted = 0;
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", repositoryId);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Repository not found.");

        var itemsTable = reader.GetString(0);
        var stageTable = reader.GetString(1);
        await reader.CloseAsync();

        if (!RepositorySqlHelper.IsValidItemsTableName(itemsTable) || !RepositorySqlHelper.IsValidStageTableName(stageTable))
            throw new InvalidOperationException("Invalid repository table names.");

        var fields = await LoadFieldDefinitionsAsync(connection, repositoryId, cancellationToken);

        if (!await TableExistsAsync(connection, itemsTable, cancellationToken))
        {
            var itemsDdl = BuildItemsTableScript(repositoryId, itemsTable, fields);
            await using var itemsCmd = new SqlCommand(itemsDdl, connection) { CommandTimeout = 300 };
            await itemsCmd.ExecuteNonQueryAsync(cancellationToken);
            _logger.LogInformation("Provisioned items table {ItemsTable} for repository {RepositoryId}", itemsTable, repositoryId);
        }

        if (!await TableExistsAsync(connection, stageTable, cancellationToken))
        {
            var stageDdl = BuildStageTableScript(repositoryId, stageTable, fields);
            await using var stageCmd = new SqlCommand(stageDdl, connection) { CommandTimeout = 300 };
            await stageCmd.ExecuteNonQueryAsync(cancellationToken);
            _logger.LogInformation("Provisioned stage table {StageTable} for repository {RepositoryId}", stageTable, repositoryId);
        }
    }

    public async Task<RepositoryDetailDto?> UpdateRepositoryAsync(
        Guid repositoryId,
        Guid tenantId,
        UpdateRepositoryRequest request,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        var connectionString = RequireConnectionString();
        await _schemaService.ApplyBaseSchemaAsync(connectionString, cancellationToken);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string loadSql = """
            SELECT ItemsTableName, StageTableName, StorageProviderId
            FROM repository.Repositories
            WHERE Id = @Id AND TenantId = @TenantId AND IsDeleted = 0;
            """;

        await using (var loadCmd = new SqlCommand(loadSql, connection))
        {
            loadCmd.Parameters.AddWithValue("@Id", repositoryId);
            loadCmd.Parameters.AddWithValue("@TenantId", tenantId);
            await using var reader = await loadCmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            var itemsTable = reader.GetString(0);
            var stageTable = reader.GetString(1);
            var currentStorageProviderId = reader.GetGuid(2);
            await reader.CloseAsync();

            Guid? storageProviderId = null;
            if (request.StorageProviderId is Guid spId || !string.IsNullOrWhiteSpace(request.StorageProviderCode))
            {
                storageProviderId = await _storageSeed.ResolveStorageProviderIdAsync(
                    tenantId, request.StorageProviderId ?? currentStorageProviderId, request.StorageProviderCode, cancellationToken);
            }

            await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                const string updateRepo = """
                    UPDATE repository.Repositories
                    SET Name = COALESCE(@Name, Name),
                        Description = COALESCE(@Description, Description),
                        StorageProviderId = COALESCE(@StorageProviderId, StorageProviderId),
                        StorageDrive = COALESCE(@StorageDrive, StorageDrive),
                        ModifiedAtUtc = SYSUTCDATETIME(),
                        ModifiedBy = @ModifiedBy
                    WHERE Id = @Id AND TenantId = @TenantId AND IsDeleted = 0;
                    """;

                await using (var cmd = new SqlCommand(updateRepo, connection, tx))
                {
                    cmd.Parameters.AddWithValue("@Id", repositoryId);
                    cmd.Parameters.AddWithValue("@TenantId", tenantId);
                    cmd.Parameters.AddWithValue("@Name", (object?)request.Name?.Trim() ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@Description", (object?)request.Description ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@StorageProviderId", (object?)storageProviderId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@StorageDrive", (object?)request.StorageDrive ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@ModifiedBy", (object?)userId ?? DBNull.Value);
                    if (await cmd.ExecuteNonQueryAsync(cancellationToken) == 0)
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return null;
                    }
                }

                if (request.Fields != null)
                {
                    var fields = NormalizeFields(request.Fields);
                    await SyncRepositoryFieldsAsync(
                        connection, tx, repositoryId, itemsTable, stageTable, fields, userId, cancellationToken);
                }

                await tx.CommitAsync(cancellationToken);
                _logger.LogInformation("Updated repository {RepositoryId}", repositoryId);
            }
            catch
            {
                await tx.RollbackAsync(cancellationToken);
                throw;
            }
        }

        return await GetRepositoryAsync(repositoryId, tenantId, cancellationToken);
    }

    private async Task SyncRepositoryFieldsAsync(
        SqlConnection connection,
        SqlTransaction tx,
        Guid repositoryId,
        string itemsTable,
        string stageTable,
        IReadOnlyList<RepositoryFieldDefinitionDto> fields,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        var existing = await LoadFieldRowsAsync(connection, tx, repositoryId, cancellationToken);
        var keepIds = fields.Where(f => f.Id is Guid id && id != Guid.Empty).Select(f => f.Id!.Value).ToHashSet();

        foreach (var row in existing)
        {
            if (!keepIds.Contains(row.Id))
                await SoftDeleteFieldAsync(connection, tx, repositoryId, row.Id, userId, cancellationToken);
        }

        var tableColumns = await RepositoryItemTableColumns.LoadAsync(connection, itemsTable, tx, cancellationToken);
        var newFields = new List<RepositoryFieldDefinitionDto>();

        foreach (var field in fields)
        {
            var sqlCol = RepositoryFieldAliases.Canonicalize(field.Name);
            if (field.Id is Guid fieldId && fieldId != Guid.Empty)
            {
                var row = existing.FirstOrDefault(e => e.Id == fieldId)
                    ?? throw new ArgumentException($"Field id {fieldId} not found on this repository.");

                if (!string.Equals(row.SqlColumnName, sqlCol, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        $"Cannot rename SQL column for field '{row.Name}' ({row.SqlColumnName} -> {sqlCol}). Add a new field instead.");
                }

                await UpdateFieldRowAsync(connection, tx, repositoryId, fieldId, field, userId, cancellationToken);
            }
            else
            {
                if (existing.Any(e => string.Equals(e.SqlColumnName, sqlCol, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException($"A field with column '{sqlCol}' already exists on this repository.");

                await InsertFieldRowAsync(connection, tx, repositoryId, field, sqlCol, userId, cancellationToken);
                newFields.Add(field);
            }
        }

        if (newFields.Count > 0)
        {
            var alterItems = BuildAddCustomColumnsScript(itemsTable, newFields, tableColumns);
            if (alterItems.Length > 0)
            {
                await using var itemsCmd = new SqlCommand(alterItems, connection, tx) { CommandTimeout = 300 };
                await itemsCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var stageColumns = await RepositoryItemTableColumns.LoadAsync(connection, stageTable, tx, cancellationToken);
            var alterStage = BuildAddCustomColumnsScript(stageTable, newFields, stageColumns);
            if (alterStage.Length > 0)
            {
                await using var stageCmd = new SqlCommand(alterStage, connection, tx) { CommandTimeout = 300 };
                await stageCmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        // Keep related/browse indexes in sync when folder-structure fields are added or toggled.
        var folderIndexSql = new StringBuilder();
        AppendFolderStructureIndexScripts(folderIndexSql, itemsTable, fields);
        if (folderIndexSql.Length > 0)
        {
            await using var idxCmd = new SqlCommand(folderIndexSql.ToString(), connection, tx) { CommandTimeout = 300 };
            await idxCmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static string BuildAddCustomColumnsScript(
        string tableName,
        IReadOnlyList<RepositoryFieldDefinitionDto> fields,
        HashSet<string> existingColumns)
    {
        var sb = new StringBuilder();
        var tableQualified = $"repository.{tableName}";

        foreach (var field in fields)
        {
            var col = RepositoryFieldAliases.Canonicalize(field.Name);
            if (RepositorySqlHelper.ReservedItemColumns.Contains(col) || !existingColumns.Add(col))
                continue;

            sb.AppendLine($"IF COL_LENGTH('{tableQualified}', '{col}') IS NULL");
            sb.AppendLine($"    ALTER TABLE repository.[{tableName}] ADD [{col}] {MapItemFieldColumnSql(field)};");
        }

        return sb.ToString();
    }

    private sealed record RepositoryFieldRow(Guid Id, string Name, string SqlColumnName);

    private static async Task<IReadOnlyList<RepositoryFieldRow>> LoadFieldRowsAsync(
        SqlConnection connection,
        SqlTransaction? tx,
        Guid repositoryId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT Id, Name, SqlColumnName
            FROM repository.RepositoryFields
            WHERE RepositoryId = @RepositoryId AND IsDeleted = 0;
            """;

        var list = new List<RepositoryFieldRow>();
        await using var cmd = new SqlCommand(sql, connection, tx);
        cmd.Parameters.AddWithValue("@RepositoryId", repositoryId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new RepositoryFieldRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2)));
        }

        return list;
    }

    private static async Task InsertFieldRowAsync(
        SqlConnection connection,
        SqlTransaction tx,
        Guid repositoryId,
        RepositoryFieldDefinitionDto field,
        string sqlCol,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        const string insertField = """
            INSERT INTO repository.RepositoryFields
            (Id, RepositoryId, Name, SqlColumnName, DataType, Level, IsMandatory, IncludeInFolderStructure, OptionsJson, OrderId, IsReadOnly, CreatedBy)
            VALUES (NEWID(), @RepositoryId, @Name, @SqlColumnName, @DataType, @Level, @IsMandatory, @IncludeInFolderStructure, @OptionsJson, @OrderId, @IsReadOnly, @CreatedBy);
            """;

        await using var cmd = new SqlCommand(insertField, connection, tx);
        cmd.Parameters.AddWithValue("@RepositoryId", repositoryId);
        cmd.Parameters.AddWithValue("@Name", field.Name.Trim());
        cmd.Parameters.AddWithValue("@SqlColumnName", sqlCol);
        cmd.Parameters.AddWithValue("@DataType", (object?)field.DataType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Level", field.Level);
        cmd.Parameters.AddWithValue("@IsMandatory", field.IsMandatory);
        cmd.Parameters.AddWithValue("@IncludeInFolderStructure", field.IncludeInFolderStructure);
        cmd.Parameters.AddWithValue("@OptionsJson", (object?)field.OptionsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@OrderId", (object?)field.OrderId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@IsReadOnly", field.IsReadOnly);
        cmd.Parameters.AddWithValue("@CreatedBy", (object?)userId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateFieldRowAsync(
        SqlConnection connection,
        SqlTransaction tx,
        Guid repositoryId,
        Guid fieldId,
        RepositoryFieldDefinitionDto field,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE repository.RepositoryFields
            SET Name = @Name,
                DataType = @DataType,
                Level = @Level,
                IsMandatory = @IsMandatory,
                IncludeInFolderStructure = @IncludeInFolderStructure,
                OptionsJson = @OptionsJson,
                OrderId = @OrderId,
                IsReadOnly = @IsReadOnly,
                ModifiedAtUtc = SYSUTCDATETIME(),
                ModifiedBy = @ModifiedBy
            WHERE Id = @Id AND RepositoryId = @RepositoryId AND IsDeleted = 0;
            """;

        await using var cmd = new SqlCommand(sql, connection, tx);
        cmd.Parameters.AddWithValue("@Id", fieldId);
        cmd.Parameters.AddWithValue("@RepositoryId", repositoryId);
        cmd.Parameters.AddWithValue("@Name", field.Name.Trim());
        cmd.Parameters.AddWithValue("@DataType", (object?)field.DataType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Level", field.Level);
        cmd.Parameters.AddWithValue("@IsMandatory", field.IsMandatory);
        cmd.Parameters.AddWithValue("@IncludeInFolderStructure", field.IncludeInFolderStructure);
        cmd.Parameters.AddWithValue("@OptionsJson", (object?)field.OptionsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@OrderId", (object?)field.OrderId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@IsReadOnly", field.IsReadOnly);
        cmd.Parameters.AddWithValue("@ModifiedBy", (object?)userId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SoftDeleteFieldAsync(
        SqlConnection connection,
        SqlTransaction tx,
        Guid repositoryId,
        Guid fieldId,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE repository.RepositoryFields
            SET IsDeleted = 1, ModifiedAtUtc = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy
            WHERE Id = @Id AND RepositoryId = @RepositoryId AND IsDeleted = 0;
            """;

        await using var cmd = new SqlCommand(sql, connection, tx);
        cmd.Parameters.AddWithValue("@Id", fieldId);
        cmd.Parameters.AddWithValue("@RepositoryId", repositoryId);
        cmd.Parameters.AddWithValue("@ModifiedBy", (object?)userId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Deduplicates by sanitized SQL column; preserves display <see cref="RepositoryFieldDefinitionDto.Name"/> as submitted.</summary>
    private static IReadOnlyList<RepositoryFieldDefinitionDto> NormalizeFields(IReadOnlyList<RepositoryFieldDefinitionDto> fields)
    {
        var merged = new List<RepositoryFieldDefinitionDto>();
        var seenSqlColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in fields)
        {
            var sqlCol = RepositoryFieldAliases.Canonicalize(field.Name);
            if (!seenSqlColumns.Add(sqlCol))
                continue;

            merged.Add(field);
        }

        return merged;
    }

    private static string BuildItemsTableScript(Guid repoId, string itemsTable, IReadOnlyList<RepositoryFieldDefinitionDto> fields)
    {
        var historyTable = RepositorySqlHelper.HistoryTableName(repoId);
        var sb = new StringBuilder();
        var customCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        sb.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = '{itemsTable}' AND schema_id = SCHEMA_ID('repository'))");
        sb.AppendLine("BEGIN");
        sb.AppendLine($"CREATE TABLE repository.[{itemsTable}] (");
        sb.AppendLine("    Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_" + itemsTable + " PRIMARY KEY DEFAULT NEWID(),");
        sb.AppendLine("    TenantId UNIQUEIDENTIFIER NOT NULL,");
        sb.AppendLine("    RepositoryId UNIQUEIDENTIFIER NOT NULL,");
        sb.AppendLine("    FolderId UNIQUEIDENTIFIER NULL,");
        sb.AppendLine("    StorageProviderId UNIQUEIDENTIFIER NOT NULL,");
        sb.AppendLine("    FilePath NVARCHAR(2000) NULL,");
        sb.AppendLine("    FileName NVARCHAR(512) NULL,");
        sb.AppendLine("    FileType NVARCHAR(64) NULL,");
        sb.AppendLine("    FileSize INT NULL,");
        sb.AppendLine("    TotalPages INT NULL,");
        sb.AppendLine("    IsVerified BIT NOT NULL CONSTRAINT DF_" + itemsTable + "_IsVerified DEFAULT (0),");
        sb.AppendLine("    Status NVARCHAR(64) NULL,");
        sb.AppendLine("    OcrScore TINYINT NULL,");
        sb.AppendLine("    AiStatus NVARCHAR(32) NULL,");
        sb.AppendLine("    OcrText NVARCHAR(MAX) NULL,");
        sb.AppendLine("    OcrJson NVARCHAR(MAX) NULL,");
        sb.AppendLine("    SummaryJson NVARCHAR(MAX) NULL,");
        sb.AppendLine("    WorkflowInstanceId UNIQUEIDENTIFIER NULL,");
        sb.AppendLine("    ActiveItem BIT NOT NULL CONSTRAINT DF_" + itemsTable + "_ActiveItem DEFAULT (1),");
        sb.AppendLine("    CreatedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_" + itemsTable + "_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),");
        sb.AppendLine("    ModifiedAtUtc DATETIME2(3) NULL,");
        sb.AppendLine("    CreatedBy UNIQUEIDENTIFIER NULL,");
        sb.AppendLine("    ModifiedBy UNIQUEIDENTIFIER NULL,");
        sb.AppendLine("    IsDeleted BIT NOT NULL CONSTRAINT DF_" + itemsTable + "_IsDeleted DEFAULT (0),");
        sb.AppendLine("    FileVersion INT NOT NULL CONSTRAINT DF_" + itemsTable + "_FileVersion DEFAULT (1),");

        foreach (var field in fields)
        {
            var col = RepositoryFieldAliases.Canonicalize(field.Name);
            if (RepositorySqlHelper.ReservedItemColumns.Contains(col) || !customCols.Add(col))
                continue;
            sb.AppendLine($"    [{col}] {MapItemFieldColumnSql(field)},");
        }

        sb.AppendLine("    ValidFrom DATETIME2(7) GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,");
        sb.AppendLine("    ValidTo DATETIME2(7) GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,");
        sb.AppendLine("    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo),");
        sb.AppendLine($"    CONSTRAINT FK_{itemsTable}_Repository FOREIGN KEY (RepositoryId) REFERENCES repository.Repositories (Id)");
        sb.AppendLine(")");
        sb.AppendLine($"WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = repository.[" + historyTable + "]));");
        sb.AppendLine("END");

        var idx = itemsTable.Replace("-", "_");
        sb.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_{idx}_Status_Created')");
        sb.AppendLine($"CREATE INDEX IX_{idx}_Status_Created ON repository.[{itemsTable}] (RepositoryId, IsDeleted, Status, CreatedAtUtc DESC)");
        sb.AppendLine($"INCLUDE (FileName, OcrScore, AiStatus, StorageProviderId, FilePath);");

        sb.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_{idx}_FileName')");
        sb.AppendLine($"CREATE INDEX IX_{idx}_FileName ON repository.[{itemsTable}] (RepositoryId, IsDeleted, FileName);");

        AppendFolderStructureIndexScripts(sb, itemsTable, fields);

        return sb.ToString();
    }

    /// <summary>
    /// Folder-structure columns use indexable types (NVARCHAR(450) etc.) so related-doc
    /// lookups on Vendor/PO/Invoice can seek efficiently.
    /// </summary>
    private static string MapItemFieldColumnSql(RepositoryFieldDefinitionDto field)
    {
        if (!field.IncludeInFolderStructure)
            return RepositorySqlHelper.MapDataTypeToSql(field.DataType);

        var dt = (field.DataType ?? string.Empty).Trim().ToUpperInvariant();
        return dt switch
        {
            "DATE" or "DATETIME" => "DATE NULL",
            "CURRENCY_AMOUNT" or "AMOUNT" or "NUMBER" or "DECIMAL" => "DECIMAL(18,2) NULL",
            "INT" or "INTEGER" => "INT NULL",
            "BIT" or "BOOL" or "BOOLEAN" => "BIT NULL",
            "LONG_TEXT" or "DYNAMIC_TABLE" or "TABLE" or "JSON" or "FILE" or "ATTACHMENT"
                => RepositorySqlHelper.MapDataTypeToSql(field.DataType),
            _ => "NVARCHAR(450) NULL"
        };
    }

    private static bool IsFolderStructureIndexable(RepositoryFieldDefinitionDto field)
    {
        if (!field.IncludeInFolderStructure)
            return false;

        var dt = (field.DataType ?? string.Empty).Trim().ToUpperInvariant();
        return dt is not ("LONG_TEXT" or "DYNAMIC_TABLE" or "TABLE" or "JSON" or "FILE" or "ATTACHMENT");
    }

    /// <summary>
    /// Per folder-structure column: (RepositoryId, IsDeleted, FolderCol) for related/browse filters.
    /// </summary>
    private static void AppendFolderStructureIndexScripts(
        StringBuilder sb,
        string itemsTable,
        IReadOnlyList<RepositoryFieldDefinitionDto> fields)
    {
        var idx = itemsTable.Replace("-", "_");
        var folderCols = fields
            .Where(IsFolderStructureIndexable)
            .OrderBy(f => f.Level)
            .ThenBy(f => f.OrderId ?? int.MaxValue)
            .Select(f => RepositoryFieldAliases.Canonicalize(f.Name))
            .Where(col => !RepositorySqlHelper.ReservedItemColumns.Contains(col))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var col in folderCols)
        {
            var indexName = $"IX_{idx}_Folder_{col}";
            if (indexName.Length > 128)
                indexName = indexName[..128];

            var safeIndex = EscapeSqlLiteral(indexName);
            var safeCol = EscapeSqlLiteral(col);

            // Skip NVARCHAR(MAX)/VARCHAR(MAX)/VARBINARY(MAX) — not valid index keys.
            sb.AppendLine($"""
                IF COL_LENGTH(N'repository.[{itemsTable}]', N'{safeCol}') IS NOT NULL
                   AND NOT EXISTS (
                        SELECT 1
                        FROM sys.columns c
                        INNER JOIN sys.types t ON t.user_type_id = c.user_type_id
                        WHERE c.object_id = OBJECT_ID(N'repository.[{itemsTable}]')
                          AND c.name = N'{safeCol}'
                          AND t.name IN (N'nvarchar', N'varchar', N'varbinary')
                          AND c.max_length = -1)
                   AND NOT EXISTS (
                        SELECT 1 FROM sys.indexes
                        WHERE name = N'{safeIndex}'
                          AND object_id = OBJECT_ID(N'repository.[{itemsTable}]'))
                CREATE INDEX [{indexName}] ON repository.[{itemsTable}] (RepositoryId, IsDeleted, [{col}])
                INCLUDE (FileName, FileType, FileSize, CreatedAtUtc);
                """);
        }
    }

    private static string EscapeSqlLiteral(string value) => value.Replace("'", "''");

    private static string BuildStageTableScript(Guid repoId, string stageTable, IReadOnlyList<RepositoryFieldDefinitionDto> fields)
    {
        var sb = new StringBuilder();
        var customCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        sb.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = '{stageTable}' AND schema_id = SCHEMA_ID('repository'))");
        sb.AppendLine("BEGIN");
        sb.AppendLine($"CREATE TABLE repository.[{stageTable}] (");
        sb.AppendLine("    Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_" + stageTable + " PRIMARY KEY DEFAULT NEWID(),");
        sb.AppendLine("    TenantId UNIQUEIDENTIFIER NOT NULL,");
        sb.AppendLine("    RepositoryId UNIQUEIDENTIFIER NOT NULL,");
        sb.AppendLine("    FolderId UNIQUEIDENTIFIER NULL,");
        sb.AppendLine("    StorageProviderId UNIQUEIDENTIFIER NOT NULL,");
        sb.AppendLine("    FilePath NVARCHAR(2000) NULL,");
        sb.AppendLine("    FileName NVARCHAR(512) NULL,");
        sb.AppendLine("    FileType NVARCHAR(64) NULL,");
        sb.AppendLine("    FileSize INT NULL,");
        sb.AppendLine("    TotalPages INT NULL,");
        sb.AppendLine("    StageStatus NVARCHAR(64) NOT NULL CONSTRAINT DF_" + stageTable + "_StageStatus DEFAULT ('Pending'),");
        sb.AppendLine("    Status NVARCHAR(64) NULL,");
        sb.AppendLine("    MailId UNIQUEIDENTIFIER NULL,");
        sb.AppendLine("    OcrScore TINYINT NULL,");
        sb.AppendLine("    AiStatus NVARCHAR(32) NULL,");
        sb.AppendLine("    OcrText NVARCHAR(MAX) NULL,");
        sb.AppendLine("    OcrJson NVARCHAR(MAX) NULL,");
        sb.AppendLine("    SummaryJson NVARCHAR(MAX) NULL,");
        sb.AppendLine("    PromotedItemId UNIQUEIDENTIFIER NULL,");
        sb.AppendLine("    CreatedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_" + stageTable + "_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),");
        sb.AppendLine("    ModifiedAtUtc DATETIME2(3) NULL,");
        sb.AppendLine("    CreatedBy UNIQUEIDENTIFIER NULL,");
        sb.AppendLine("    ModifiedBy UNIQUEIDENTIFIER NULL,");
        sb.AppendLine("    IsDeleted BIT NOT NULL CONSTRAINT DF_" + stageTable + "_IsDeleted DEFAULT (0),");

        foreach (var field in fields)
        {
            var col = RepositoryFieldAliases.Canonicalize(field.Name);
            if (RepositorySqlHelper.ReservedItemColumns.Contains(col) || !customCols.Add(col))
                continue;
            sb.AppendLine($"    [{col}] {RepositorySqlHelper.MapDataTypeToSql(field.DataType)},");
        }

        sb.AppendLine($"    CONSTRAINT FK_{stageTable}_Repository FOREIGN KEY (RepositoryId) REFERENCES repository.Repositories (Id)");
        sb.AppendLine(");");
        sb.AppendLine("END");

        var idx = stageTable.Replace("-", "_");
        sb.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_{idx}_StageStatus_Created')");
        sb.AppendLine($"CREATE INDEX IX_{idx}_StageStatus_Created ON repository.[{stageTable}] (RepositoryId, IsDeleted, StageStatus, CreatedAtUtc DESC)");
        sb.AppendLine($"INCLUDE (FileName, PromotedItemId, StorageProviderId, FilePath);");

        sb.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_{idx}_MailId')");
        sb.AppendLine($"CREATE INDEX IX_{idx}_MailId ON repository.[{stageTable}] (RepositoryId, IsDeleted, MailId) WHERE MailId IS NOT NULL;");

        return sb.ToString();
    }

    private static async Task<bool> TableExistsAsync(SqlConnection connection, string tableName, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT 1 FROM sys.tables t
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE t.name = @Name AND s.name = 'repository';
            """;
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Name", tableName);
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <summary>Counts non-deleted items in the repository items table (0 if table missing/invalid).</summary>
    private static async Task<int> CountItemsAsync(
        SqlConnection connection,
        string itemsTableName,
        CancellationToken cancellationToken)
    {
        if (!RepositorySqlHelper.IsValidItemsTableName(itemsTableName))
            return 0;

        if (!await TableExistsAsync(connection, itemsTableName, cancellationToken))
            return 0;

        var table = RepositorySqlHelper.QualifiedItemsTable(itemsTableName);
        var sql = $"SELECT COUNT_BIG(1) FROM {table} WHERE IsDeleted = 0;";
        await using var cmd = new SqlCommand(sql, connection);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        if (result == null || result == DBNull.Value)
            return 0;

        var count = Convert.ToInt64(result);
        return count > int.MaxValue ? int.MaxValue : (int)count;
    }

    private async Task<IReadOnlyList<RepositoryFieldDefinitionDto>> LoadFieldDefinitionsAsync(
        SqlConnection connection,
        Guid repositoryId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT Name, DataType, Level, IsMandatory, IncludeInFolderStructure, OptionsJson, OrderId, IsReadOnly
            FROM repository.RepositoryFields
            WHERE RepositoryId = @RepositoryId AND IsDeleted = 0
            ORDER BY OrderId, Name;
            """;

        var list = new List<RepositoryFieldDefinitionDto>();
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@RepositoryId", repositoryId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new RepositoryFieldDefinitionDto(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt32(2),
                reader.GetBoolean(3),
                reader.GetBoolean(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.GetBoolean(7)));
        }

        return NormalizeFields(list);
    }

    private async Task<IReadOnlyList<RepositoryFieldDto>> LoadFieldsAsync(SqlConnection connection, Guid repositoryId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT Id, Name, SqlColumnName, DataType, Level, IsMandatory, IncludeInFolderStructure, OptionsJson, OrderId, IsReadOnly
            FROM repository.RepositoryFields
            WHERE RepositoryId = @RepositoryId AND IsDeleted = 0
            ORDER BY OrderId, Name;
            """;

        var list = new List<RepositoryFieldDto>();
        await using var cmd = new SqlCommand(sql, connection);
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

    private string RequireConnectionString() =>
        _connectionProvider.ConnectionString
        ?? throw new InvalidOperationException("Tenant connection string not resolved.");
}
