using Npgsql;
using Microsoft.Extensions.Logging;
using SaaSApp.MultiTenancy;
using SaaSApp.Repository.Application.Contracts;
using System.Text;

namespace SaaSApp.Repository.Infrastructure.Services;

/// <summary>
/// PHASE 4 PORT NOTE: this file owns the other half of the "dynamic DDL engine" alongside
/// RepositorySqlHelper.cs/RepositoryItemTableColumns.cs -- it's where the per-repository
/// Items_/Stage tables are actually CREATE TABLE'd. Static tables it also touches
/// (repository.Repositories, repository.RepositoryFields, repository.StorageProviders,
/// users.Users) keep the PascalCase-quoted convention established for the whole static
/// schema in Phase 2/3. The dynamic Items_/Stage tables and their fixed/reserved columns
/// use snake_case, unquoted (system-controlled, matching WorkflowTableCreator.cs). Custom
/// (user-defined) columns are double-quoted with their sanitized-but-original casing
/// preserved -- see RepositorySqlHelper.QuoteCustomColumn's doc comment for why.
///
/// SQL Server's SYSTEM_VERSIONING/PERIOD FOR SYSTEM_TIME on the items table becomes a
/// Decision 2 trigger-based history table, same pattern as workflow.WorkflowInstances in
/// 02_CreateTenantDatabase.sql. Unlike that static table, this history table's column set
/// is DYNAMIC (fixed reserved columns + N user-defined custom columns discovered at
/// runtime), so -- unlike the static SQL script, which could hardcode the trigger
/// function's column list -- the trigger function here is built and CREATE OR REPLACE'd
/// from the live field list, both at table-creation time and whenever
/// SyncRepositoryFieldsAsync adds new custom columns (Postgres's LIKE ... INCLUDING
/// DEFAULTS is a one-time snapshot, not a live mirror, so both the history table's own
/// columns and the trigger function's explicit column list must be kept in sync by hand
/// on every schema change -- the same obligation SQL Server itself placed on schema
/// changes to a system-versioned table).
/// </summary>
public sealed class StaticRepositoryProvisioner : IStaticRepositoryProvisioner
{
    // Ordered to match the CREATE TABLE column order emitted by BuildItemsTableScript --
    // reused by the trigger-function builder for its explicit INSERT column list.
    private static readonly string[] ReservedItemColumnsOrdered =
    {
        "id", "tenant_id", "repository_id", "folder_id", "storage_provider_id",
        "file_path", "file_name", "file_type", "file_size", "total_pages",
        "is_verified", "status", "ocr_score", "ai_status", "ocr_text", "ocr_json", "summary_json",
        "workflow_instance_id", "active_item", "created_at_utc", "modified_at_utc",
        "created_by", "modified_by", "is_deleted", "file_version"
    };

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

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            const string insertRepo = """
                INSERT INTO repository."Repositories"
                ("Id", "TenantId", "Name", "Description", "FieldsType", "StorageProviderId", "StorageDrive", "ItemsTableName", "StageTableName", "IsDefaultRepository", "CreatedBy")
                VALUES (@Id, @TenantId, @Name, @Description, 'STATIC', @StorageProviderId, @StorageDrive, @ItemsTableName, @StageTableName, @IsDefaultRepository, @CreatedBy);
                """;

            await using (var cmd = new NpgsqlCommand(insertRepo, connection, tx))
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
                    INSERT INTO repository."RepositoryFields"
                    ("Id", "RepositoryId", "Name", "SqlColumnName", "DataType", "Level", "IsMandatory", "IncludeInFolderStructure", "OptionsJson", "OrderId", "IsReadOnly", "CreatedBy")
                    VALUES (gen_random_uuid(), @RepositoryId, @Name, @SqlColumnName, @DataType, @Level, @IsMandatory, @IncludeInFolderStructure, @OptionsJson, @OrderId, @IsReadOnly, @CreatedBy);
                    """;
                await using var fcmd = new NpgsqlCommand(insertField, connection, tx);
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
            await using (var itemsCmd = new NpgsqlCommand(itemsDdl, connection, tx) { CommandTimeout = 300 })
            {
                await itemsCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var stageDdl = BuildStageTableScript(repoId, stageTable, fields);
            await using (var stageCmd = new NpgsqlCommand(stageDdl, connection, tx) { CommandTimeout = 300 })
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
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT
                r."Id",
                r."Name",
                r."Description",
                r."StorageProviderId",
                r."ItemsTableName",
                r."CreatedAtUtc",
                r."IsDefaultRepository",
                r."IsDeleted",
                sp."Code" AS "StorageProviderCode",
                sp."Name" AS "StorageProviderName",
                r."CreatedBy",
                r."ModifiedBy",
                cb."Email" AS "CreatedByName",
                COALESCE(mb."Email", cb."Email") AS "ModifiedByName"
            FROM repository."Repositories" r
            LEFT JOIN repository."StorageProviders" sp ON sp."Id" = r."StorageProviderId" AND sp."IsDeleted" = false
            LEFT JOIN users."Users" cb ON cb."Id" = r."CreatedBy" AND cb."IsDeleted" = false
            LEFT JOIN users."Users" mb ON mb."Id" = r."ModifiedBy" AND mb."IsDeleted" = false
            WHERE r."TenantId" = @TenantId
            ORDER BY r."IsDeleted", r."Name";
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

        await using (var cmd = new NpgsqlCommand(sql, connection))
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
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT "ItemsTableName", "StageTableName"
            FROM repository."Repositories"
            WHERE "Id" = @Id AND "TenantId" = @TenantId AND "IsDeleted" = false;
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
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
            await using var itemsCmd = new NpgsqlCommand(itemsDdl, connection) { CommandTimeout = 300 };
            await itemsCmd.ExecuteNonQueryAsync(cancellationToken);
            _logger.LogInformation("Provisioned items table {ItemsTable} for repository {RepositoryId}", itemsTable, repositoryId);
        }

        if (!await TableExistsAsync(connection, stageTable, cancellationToken))
        {
            var stageDdl = BuildStageTableScript(repositoryId, stageTable, fields);
            await using var stageCmd = new NpgsqlCommand(stageDdl, connection) { CommandTimeout = 300 };
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

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string loadSql = """
            SELECT "ItemsTableName", "StageTableName", "StorageProviderId"
            FROM repository."Repositories"
            WHERE "Id" = @Id AND "TenantId" = @TenantId AND "IsDeleted" = false;
            """;

        await using (var loadCmd = new NpgsqlCommand(loadSql, connection))
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

            await using var tx = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                const string updateRepo = """
                    UPDATE repository."Repositories"
                    SET "Name" = COALESCE(@Name, "Name"),
                        "Description" = COALESCE(@Description, "Description"),
                        "StorageProviderId" = COALESCE(@StorageProviderId, "StorageProviderId"),
                        "StorageDrive" = COALESCE(@StorageDrive, "StorageDrive"),
                        "ModifiedAtUtc" = now(),
                        "ModifiedBy" = @ModifiedBy
                    WHERE "Id" = @Id AND "TenantId" = @TenantId AND "IsDeleted" = false;
                    """;

                await using (var cmd = new NpgsqlCommand(updateRepo, connection, tx))
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
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
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
                await using var itemsCmd = new NpgsqlCommand(alterItems, connection, tx) { CommandTimeout = 300 };
                await itemsCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Keep the trigger-based history table (Decision 2) in sync: ALTER it with the
            // same new columns, then rebuild the trigger function's explicit column list.
            // tableColumns now holds the full post-alter column set (BuildAddCustomColumnsScript
            // mutates it via existingColumns.Add as a side effect), which is exactly what the
            // rebuilt trigger function needs to enumerate.
            var historyTable = RepositorySqlHelper.HistoryTableName(repositoryId);
            var historySyncSql = BuildItemsHistorySyncScript(itemsTable, historyTable, newFields, tableColumns);
            if (historySyncSql.Length > 0)
            {
                await using var historyCmd = new NpgsqlCommand(historySyncSql, connection, tx) { CommandTimeout = 300 };
                await historyCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var stageColumns = await RepositoryItemTableColumns.LoadAsync(connection, stageTable, tx, cancellationToken);
            var alterStage = BuildAddCustomColumnsScript(stageTable, newFields, stageColumns);
            if (alterStage.Length > 0)
            {
                await using var stageCmd = new NpgsqlCommand(alterStage, connection, tx) { CommandTimeout = 300 };
                await stageCmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        // Keep related/browse indexes in sync when folder-structure fields are added or toggled.
        var folderIndexSql = new StringBuilder();
        AppendFolderStructureIndexScripts(folderIndexSql, itemsTable, fields);
        if (folderIndexSql.Length > 0)
        {
            await using var idxCmd = new NpgsqlCommand(folderIndexSql.ToString(), connection, tx) { CommandTimeout = 300 };
            await idxCmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Preserves the original SQL Server code's behavior of using the folder-structure-aware
    /// type mapping (<see cref="MapItemFieldColumnSql"/>) for ALTER on both the items and
    /// stage tables, even though the stage table's own CREATE uses the plain
    /// <see cref="RepositorySqlHelper.MapDataTypeToSql"/> -- an existing asymmetry in the
    /// pre-port code, kept as-is for behavioral parity rather than "fixed" during migration.
    /// </summary>
    private static string BuildAddCustomColumnsScript(
        string tableName,
        IReadOnlyList<RepositoryFieldDefinitionDto> fields,
        HashSet<string> existingColumns)
    {
        var sb = new StringBuilder();

        foreach (var field in fields)
        {
            var col = RepositoryFieldAliases.Canonicalize(field.Name);
            if (RepositorySqlHelper.ReservedItemColumns.Contains(col) || !existingColumns.Add(col))
                continue;

            sb.AppendLine($"ALTER TABLE repository.{tableName} ADD COLUMN IF NOT EXISTS {RepositorySqlHelper.QuoteCustomColumn(col)} {MapItemFieldColumnSql(field)};");
        }

        return sb.ToString();
    }

    /// <summary>ALTERs the history table with the same new custom columns, then rebuilds the trigger function/trigger.</summary>
    private static string BuildItemsHistorySyncScript(
        string itemsTable,
        string historyTable,
        IReadOnlyList<RepositoryFieldDefinitionDto> newFields,
        HashSet<string> allItemsColumns)
    {
        var sb = new StringBuilder();
        var addedAny = false;

        foreach (var field in newFields)
        {
            var col = RepositoryFieldAliases.Canonicalize(field.Name);
            if (RepositorySqlHelper.ReservedItemColumns.Contains(col))
                continue;

            sb.AppendLine($"ALTER TABLE repository.{historyTable} ADD COLUMN IF NOT EXISTS {RepositorySqlHelper.QuoteCustomColumn(col)} {MapItemFieldColumnSql(field)};");
            addedAny = true;
        }

        if (!addedAny)
            return string.Empty;

        var customCols = allItemsColumns
            .Where(c => !RepositorySqlHelper.ReservedItemColumns.Contains(c))
            .Select(RepositorySqlHelper.QuoteCustomColumn)
            .ToList();

        sb.Append(BuildItemsTriggerFunctionScript(itemsTable, historyTable, customCols));
        return sb.ToString();
    }

    /// <summary>
    /// CREATE OR REPLACEs the AFTER UPDATE/DELETE trigger function that copies the prior row
    /// image into the history table -- the Decision 2 replacement for SYSTEM_VERSIONING.
    /// Explicit column list (reserved + custom), same reasoning as
    /// workflow.fn_workflow_instances_history in 02_CreateTenantDatabase.sql: `DEFAULT` is not
    /// valid inside a SELECT list, so a bare `INSERT ... SELECT OLD.*` cannot be used against
    /// a table with its own GENERATED ALWAYS AS IDENTITY column (history_id).
    /// </summary>
    private static string BuildItemsTriggerFunctionScript(string itemsTable, string historyTable, IReadOnlyList<string> quotedCustomColumns)
    {
        var allCols = ReservedItemColumnsOrdered.Concat(quotedCustomColumns).ToList();
        var colList = string.Join(", ", allCols);
        var oldColList = string.Join(", ", allCols.Select(c => $"OLD.{c}"));
        var fnName = $"repository.fn_{itemsTable}_history";
        var triggerName = $"trg_{itemsTable}_history";

        var sb = new StringBuilder();
        sb.AppendLine($"CREATE OR REPLACE FUNCTION {fnName}() RETURNS trigger AS $body$");
        sb.AppendLine("BEGIN");
        sb.AppendLine("    IF (TG_OP = 'UPDATE') THEN");
        sb.AppendLine($"        INSERT INTO repository.{historyTable} ({colList}, history_operation, history_recorded_at)");
        sb.AppendLine($"        SELECT {oldColList}, 'U', now();");
        sb.AppendLine("        RETURN NEW;");
        sb.AppendLine("    ELSIF (TG_OP = 'DELETE') THEN");
        sb.AppendLine($"        INSERT INTO repository.{historyTable} ({colList}, history_operation, history_recorded_at)");
        sb.AppendLine($"        SELECT {oldColList}, 'D', now();");
        sb.AppendLine("        RETURN OLD;");
        sb.AppendLine("    END IF;");
        sb.AppendLine("    RETURN NULL;");
        sb.AppendLine("END;");
        sb.AppendLine("$body$ LANGUAGE plpgsql;");
        sb.AppendLine($"DROP TRIGGER IF EXISTS {triggerName} ON repository.{itemsTable};");
        sb.AppendLine($"CREATE TRIGGER {triggerName}");
        sb.AppendLine($"    AFTER UPDATE OR DELETE ON repository.{itemsTable}");
        sb.AppendLine($"    FOR EACH ROW EXECUTE FUNCTION {fnName}();");
        return sb.ToString();
    }

    private sealed record RepositoryFieldRow(Guid Id, string Name, string SqlColumnName);

    private static async Task<IReadOnlyList<RepositoryFieldRow>> LoadFieldRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? tx,
        Guid repositoryId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT "Id", "Name", "SqlColumnName"
            FROM repository."RepositoryFields"
            WHERE "RepositoryId" = @RepositoryId AND "IsDeleted" = false;
            """;

        var list = new List<RepositoryFieldRow>();
        await using var cmd = new NpgsqlCommand(sql, connection, tx);
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
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid repositoryId,
        RepositoryFieldDefinitionDto field,
        string sqlCol,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        const string insertField = """
            INSERT INTO repository."RepositoryFields"
            ("Id", "RepositoryId", "Name", "SqlColumnName", "DataType", "Level", "IsMandatory", "IncludeInFolderStructure", "OptionsJson", "OrderId", "IsReadOnly", "CreatedBy")
            VALUES (gen_random_uuid(), @RepositoryId, @Name, @SqlColumnName, @DataType, @Level, @IsMandatory, @IncludeInFolderStructure, @OptionsJson, @OrderId, @IsReadOnly, @CreatedBy);
            """;

        await using var cmd = new NpgsqlCommand(insertField, connection, tx);
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
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid repositoryId,
        Guid fieldId,
        RepositoryFieldDefinitionDto field,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE repository."RepositoryFields"
            SET "Name" = @Name,
                "DataType" = @DataType,
                "Level" = @Level,
                "IsMandatory" = @IsMandatory,
                "IncludeInFolderStructure" = @IncludeInFolderStructure,
                "OptionsJson" = @OptionsJson,
                "OrderId" = @OrderId,
                "IsReadOnly" = @IsReadOnly,
                "ModifiedAtUtc" = now(),
                "ModifiedBy" = @ModifiedBy
            WHERE "Id" = @Id AND "RepositoryId" = @RepositoryId AND "IsDeleted" = false;
            """;

        await using var cmd = new NpgsqlCommand(sql, connection, tx);
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
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid repositoryId,
        Guid fieldId,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE repository."RepositoryFields"
            SET "IsDeleted" = true, "ModifiedAtUtc" = now(), "ModifiedBy" = @ModifiedBy
            WHERE "Id" = @Id AND "RepositoryId" = @RepositoryId AND "IsDeleted" = false;
            """;

        await using var cmd = new NpgsqlCommand(sql, connection, tx);
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
        var customColRefs = new List<string>();

        sb.AppendLine($"CREATE TABLE IF NOT EXISTS repository.{itemsTable} (");
        sb.AppendLine("    id uuid NOT NULL DEFAULT gen_random_uuid(),");
        sb.AppendLine("    tenant_id uuid NOT NULL,");
        sb.AppendLine("    repository_id uuid NOT NULL,");
        sb.AppendLine("    folder_id uuid NULL,");
        sb.AppendLine("    storage_provider_id uuid NOT NULL,");
        sb.AppendLine("    file_path varchar(2000) NULL,");
        sb.AppendLine("    file_name varchar(512) NULL,");
        sb.AppendLine("    file_type varchar(64) NULL,");
        sb.AppendLine("    file_size integer NULL,");
        sb.AppendLine("    total_pages integer NULL,");
        sb.AppendLine("    is_verified boolean NOT NULL DEFAULT false,");
        sb.AppendLine("    status varchar(64) NULL,");
        sb.AppendLine("    ocr_score smallint NULL,");
        sb.AppendLine("    ai_status varchar(32) NULL,");
        sb.AppendLine("    ocr_text text NULL,");
        sb.AppendLine("    ocr_json text NULL,");
        sb.AppendLine("    summary_json text NULL,");
        sb.AppendLine("    workflow_instance_id uuid NULL,");
        sb.AppendLine("    active_item boolean NOT NULL DEFAULT true,");
        sb.AppendLine("    created_at_utc timestamptz NOT NULL DEFAULT now(),");
        sb.AppendLine("    modified_at_utc timestamptz NULL,");
        sb.AppendLine("    created_by uuid NULL,");
        sb.AppendLine("    modified_by uuid NULL,");
        sb.AppendLine("    is_deleted boolean NOT NULL DEFAULT false,");
        sb.AppendLine("    file_version integer NOT NULL DEFAULT 1,");

        foreach (var field in fields)
        {
            var col = RepositoryFieldAliases.Canonicalize(field.Name);
            if (RepositorySqlHelper.ReservedItemColumns.Contains(col) || !customCols.Add(col))
                continue;
            var quoted = RepositorySqlHelper.QuoteCustomColumn(col);
            sb.AppendLine($"    {quoted} {MapItemFieldColumnSql(field)},");
            customColRefs.Add(quoted);
        }

        sb.AppendLine($"    CONSTRAINT pk_{itemsTable} PRIMARY KEY (id),");
        sb.AppendLine($"    CONSTRAINT fk_{itemsTable}_repository FOREIGN KEY (repository_id) REFERENCES repository.\"Repositories\" (\"Id\")");
        sb.AppendLine(");");

        var idx = itemsTable;
        sb.AppendLine($"CREATE INDEX IF NOT EXISTS ix_{idx}_status_created ON repository.{itemsTable} (repository_id, is_deleted, status, created_at_utc DESC) INCLUDE (file_name, ocr_score, ai_status, storage_provider_id, file_path);");
        sb.AppendLine($"CREATE INDEX IF NOT EXISTS ix_{idx}_file_name ON repository.{itemsTable} (repository_id, is_deleted, file_name);");

        // Decision 2 trigger-based history, replacing SYSTEM_VERSIONING. LIKE snapshot is a
        // one-time copy; see BuildItemsHistorySyncScript for how new custom columns get
        // propagated here after initial creation.
        sb.AppendLine($"CREATE TABLE IF NOT EXISTS repository.{historyTable} (");
        sb.AppendLine($"    LIKE repository.{itemsTable} INCLUDING DEFAULTS,");
        sb.AppendLine("    history_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,");
        sb.AppendLine("    history_operation varchar(1) NOT NULL,");
        sb.AppendLine("    history_recorded_at timestamptz NOT NULL DEFAULT now()");
        sb.AppendLine(");");
        sb.AppendLine($"CREATE INDEX IF NOT EXISTS ix_{historyTable}_id ON repository.{historyTable} (id);");
        sb.Append(BuildItemsTriggerFunctionScript(itemsTable, historyTable, customColRefs));

        AppendFolderStructureIndexScripts(sb, itemsTable, fields);

        return sb.ToString();
    }

    /// <summary>
    /// Folder-structure columns use indexable types (varchar(450) etc.) so related-doc
    /// lookups on Vendor/PO/Invoice can seek efficiently.
    /// </summary>
    private static string MapItemFieldColumnSql(RepositoryFieldDefinitionDto field)
    {
        if (!field.IncludeInFolderStructure)
            return RepositorySqlHelper.MapDataTypeToSql(field.DataType);

        var dt = (field.DataType ?? string.Empty).Trim().ToUpperInvariant();
        return dt switch
        {
            "DATE" or "DATETIME" => "date NULL",
            "CURRENCY_AMOUNT" or "AMOUNT" or "NUMBER" or "DECIMAL" => "decimal(18,2) NULL",
            "INT" or "INTEGER" => "integer NULL",
            "BIT" or "BOOL" or "BOOLEAN" => "boolean NULL",
            "LONG_TEXT" or "DYNAMIC_TABLE" or "TABLE" or "JSON" or "FILE" or "ATTACHMENT"
                => RepositorySqlHelper.MapDataTypeToSql(field.DataType),
            _ => "varchar(450) NULL"
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
    /// Per folder-structure column: (repository_id, is_deleted, FolderCol) for related/browse
    /// filters. Existence + type checked dynamically (the field could have just been added
    /// in the same sync pass), executed via a DO block since CREATE INDEX cannot appear as a
    /// bare statement inside PL/pgSQL -- it needs EXECUTE with dynamic SQL.
    /// </summary>
    private static void AppendFolderStructureIndexScripts(
        StringBuilder sb,
        string itemsTable,
        IReadOnlyList<RepositoryFieldDefinitionDto> fields)
    {
        var idx = itemsTable;
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
            var indexName = $"ix_{idx}_folder_{col}".ToLowerInvariant();
            if (indexName.Length > 63)
                indexName = indexName[..63];

            var quotedCol = RepositorySqlHelper.QuoteCustomColumn(col);
            var safeCol = EscapeSqlLiteral(col);
            var safeTable = EscapeSqlLiteral(itemsTable);

            // Skip text (unbounded) columns -- not valid btree index keys at scale, same
            // reasoning as the original's NVARCHAR(MAX)/VARCHAR(MAX) exclusion.
            sb.AppendLine($"""
                DO $do$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'repository' AND table_name = '{safeTable}'
                          AND column_name = '{safeCol}' AND data_type <> 'text'
                    ) THEN
                        EXECUTE 'CREATE INDEX IF NOT EXISTS {indexName} ON repository.{itemsTable} (repository_id, is_deleted, {quotedCol}) INCLUDE (file_name, file_type, file_size, created_at_utc)';
                    END IF;
                END
                $do$;
                """);
        }
    }

    private static string EscapeSqlLiteral(string value) => value.Replace("'", "''");

    private static string BuildStageTableScript(Guid repoId, string stageTable, IReadOnlyList<RepositoryFieldDefinitionDto> fields)
    {
        var sb = new StringBuilder();
        var customCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        sb.AppendLine($"CREATE TABLE IF NOT EXISTS repository.{stageTable} (");
        sb.AppendLine("    id uuid NOT NULL DEFAULT gen_random_uuid(),");
        sb.AppendLine("    tenant_id uuid NOT NULL,");
        sb.AppendLine("    repository_id uuid NOT NULL,");
        sb.AppendLine("    folder_id uuid NULL,");
        sb.AppendLine("    storage_provider_id uuid NOT NULL,");
        sb.AppendLine("    file_path varchar(2000) NULL,");
        sb.AppendLine("    file_name varchar(512) NULL,");
        sb.AppendLine("    file_type varchar(64) NULL,");
        sb.AppendLine("    file_size integer NULL,");
        sb.AppendLine("    total_pages integer NULL,");
        sb.AppendLine("    stage_status varchar(64) NOT NULL DEFAULT 'Pending',");
        sb.AppendLine("    status varchar(64) NULL,");
        sb.AppendLine("    mail_id uuid NULL,");
        sb.AppendLine("    ocr_score smallint NULL,");
        sb.AppendLine("    ai_status varchar(32) NULL,");
        sb.AppendLine("    ocr_text text NULL,");
        sb.AppendLine("    ocr_json text NULL,");
        sb.AppendLine("    summary_json text NULL,");
        sb.AppendLine("    promoted_item_id uuid NULL,");
        sb.AppendLine("    created_at_utc timestamptz NOT NULL DEFAULT now(),");
        sb.AppendLine("    modified_at_utc timestamptz NULL,");
        sb.AppendLine("    created_by uuid NULL,");
        sb.AppendLine("    modified_by uuid NULL,");
        sb.AppendLine("    is_deleted boolean NOT NULL DEFAULT false,");

        foreach (var field in fields)
        {
            var col = RepositoryFieldAliases.Canonicalize(field.Name);
            if (RepositorySqlHelper.ReservedItemColumns.Contains(col) || !customCols.Add(col))
                continue;
            sb.AppendLine($"    {RepositorySqlHelper.QuoteCustomColumn(col)} {RepositorySqlHelper.MapDataTypeToSql(field.DataType)},");
        }

        sb.AppendLine($"    CONSTRAINT pk_{stageTable} PRIMARY KEY (id),");
        sb.AppendLine($"    CONSTRAINT fk_{stageTable}_repository FOREIGN KEY (repository_id) REFERENCES repository.\"Repositories\" (\"Id\")");
        sb.AppendLine(");");

        var idx = stageTable;
        sb.AppendLine($"CREATE INDEX IF NOT EXISTS ix_{idx}_status_created ON repository.{stageTable} (repository_id, is_deleted, stage_status, created_at_utc DESC) INCLUDE (file_name, promoted_item_id, storage_provider_id, file_path);");
        sb.AppendLine($"CREATE INDEX IF NOT EXISTS ix_{idx}_mail_id ON repository.{stageTable} (repository_id, is_deleted, mail_id) WHERE mail_id IS NOT NULL;");

        return sb.ToString();
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, string tableName, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT 1 FROM information_schema.tables
            WHERE table_name = @Name AND table_schema = 'repository';
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Name", tableName);
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <summary>Counts non-deleted items in the repository items table (0 if table missing/invalid).</summary>
    private static async Task<int> CountItemsAsync(
        NpgsqlConnection connection,
        string itemsTableName,
        CancellationToken cancellationToken)
    {
        if (!RepositorySqlHelper.IsValidItemsTableName(itemsTableName))
            return 0;

        if (!await TableExistsAsync(connection, itemsTableName, cancellationToken))
            return 0;

        var table = RepositorySqlHelper.QualifiedItemsTable(itemsTableName);
        var sql = $"SELECT COUNT(*) FROM {table} WHERE is_deleted = false;";
        await using var cmd = new NpgsqlCommand(sql, connection);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        if (result == null || result == DBNull.Value)
            return 0;

        var count = Convert.ToInt64(result);
        return count > int.MaxValue ? int.MaxValue : (int)count;
    }

    private async Task<IReadOnlyList<RepositoryFieldDefinitionDto>> LoadFieldDefinitionsAsync(
        NpgsqlConnection connection,
        Guid repositoryId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT "Name", "DataType", "Level", "IsMandatory", "IncludeInFolderStructure", "OptionsJson", "OrderId", "IsReadOnly"
            FROM repository."RepositoryFields"
            WHERE "RepositoryId" = @RepositoryId AND "IsDeleted" = false
            ORDER BY "OrderId", "Name";
            """;

        var list = new List<RepositoryFieldDefinitionDto>();
        await using var cmd = new NpgsqlCommand(sql, connection);
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

    private async Task<IReadOnlyList<RepositoryFieldDto>> LoadFieldsAsync(NpgsqlConnection connection, Guid repositoryId, CancellationToken cancellationToken)
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

    private string RequireConnectionString() =>
        _connectionProvider.ConnectionString
        ?? throw new InvalidOperationException("Tenant connection string not resolved.");
}
