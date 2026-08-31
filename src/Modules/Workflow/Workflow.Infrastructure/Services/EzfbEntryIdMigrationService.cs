using System.Globalization;
using Npgsql;
using Microsoft.Extensions.Logging;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>
/// One-time tenant migration: ezfb_*_items.item_id integer → uuid and workflow form_entry_id → uuid.
/// Run on each existing tenant DB before deploying the Guid-only API.
/// </summary>
public sealed class EzfbEntryIdMigrationService : IEzfbEntryIdMigrationService
{
    private readonly ILogger<EzfbEntryIdMigrationService> _logger;

    public EzfbEntryIdMigrationService(ILogger<EzfbEntryIdMigrationService> logger)
    {
        _logger = logger;
    }

    public async Task<EzfbEntryIdMigrationResult> MigrateTenantAsync(
        string tenantConnectionString,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantConnectionString))
            throw new ArgumentException("Tenant connection string is required.", nameof(tenantConnectionString));

        var messages = new List<string>();
        var tablesMigrated = 0;
        var rowsMapped = 0;
        var processFormUpdated = 0;
        var workflowFormsUpdated = 0;

        await using var connection = new NpgsqlConnection(tenantConnectionString);
        await connection.OpenAsync(cancellationToken);

        await EnsureMappingTableAsync(connection, cancellationToken);
        var formIdsByEzfbTable = await LoadFormIdsByEzfbTableAsync(connection, cancellationToken);
        var ezfbTables = await ListIntegerItemIdTablesAsync(connection, cancellationToken);

        if (ezfbTables.Count == 0)
        {
            messages.Add("No ezfb tables with integer item_id found; tenant may already be migrated.");
            await MigrateWorkflowFormEntryIdColumnsAsync(connection, cancellationToken);
            return new EzfbEntryIdMigrationResult(0, 0, 0, 0, messages);
        }

        foreach (var tableName in ezfbTables)
        {
            var mapped = await PopulateMappingAsync(connection, tableName, cancellationToken);
            if (mapped == 0)
                continue;

            rowsMapped += mapped;
            await AddAndBackfillUuidColumnAsync(connection, tableName, cancellationToken);
            await SwapEzfbPrimaryKeyAsync(connection, tableName, cancellationToken);
            await MigrateHistoryTableAsync(connection, tableName, cancellationToken);

            tablesMigrated++;
            messages.Add($"Migrated dbo.{tableName}: {mapped} row(s).");
            _logger.LogInformation("ezfb migration completed for {Table} ({Rows} rows)", tableName, mapped);
        }

        foreach (var tableName in ezfbTables)
        {
            formIdsByEzfbTable.TryGetValue(tableName, out var wFormIds);
            wFormIds ??= [];

            processFormUpdated += await UpdateProcessFormReferencesAsync(
                connection, tableName, wFormIds, cancellationToken);
            workflowFormsUpdated += await UpdateWorkflowFormsReferencesAsync(
                connection, tableName, wFormIds, cancellationToken);
            await UpdateWorkflowTasksReferencesAsync(connection, tableName, wFormIds, cancellationToken);
        }

        await FinalizeAllWorkflowFormEntryColumnsAsync(connection, cancellationToken);
        await MigrateWorkflowFormEntryIdColumnsAsync(connection, cancellationToken);
        messages.Add($"Updated process_form rows: {processFormUpdated}, workflow_forms rows: {workflowFormsUpdated}.");

        return new EzfbEntryIdMigrationResult(
            tablesMigrated,
            rowsMapped,
            processFormUpdated,
            workflowFormsUpdated,
            messages);
    }

    private static async Task EnsureMappingTableAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS dbo._ezfb_item_id_migration (
                ezfb_table_name text NOT NULL,
                old_item_id integer NOT NULL,
                new_item_id uuid NOT NULL,
                PRIMARY KEY (ezfb_table_name, old_item_id)
            );
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<string>> ListIntegerItemIdTablesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT c.table_name
            FROM information_schema.columns c
            WHERE c.table_schema = 'dbo'
              AND c.table_name LIKE 'ezfb\_%\_items' ESCAPE '\'
              AND c.column_name = 'item_id'
              AND c.data_type = 'integer'
            ORDER BY c.table_name;
            """;
        var list = new List<string>();
        await using var cmd = new NpgsqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            list.Add(reader.GetString(0));
        return list;
    }

    private static async Task<Dictionary<string, List<string>>> LoadFormIdsByEzfbTableAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """SELECT id::text FROM dbo."wForm" WHERE is_deleted = false OR is_deleted IS NULL;""";
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = new NpgsqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var formId = reader.GetString(0);
            string suffix;
            try
            {
                suffix = FormIdNaming.GetEzfbTableSuffix(FormIdNaming.NormalizeFormId(formId));
            }
            catch
            {
                continue;
            }

            var tableName = $"ezfb_{suffix}_items";
            if (!map.TryGetValue(tableName, out var ids))
            {
                ids = [];
                map[tableName] = ids;
            }

            ids.Add(formId);
        }

        return map;
    }

    private static async Task<int> PopulateMappingAsync(
        NpgsqlConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO dbo._ezfb_item_id_migration (ezfb_table_name, old_item_id, new_item_id)
            SELECT @TableName, item_id, gen_random_uuid()
            FROM dbo."{tableName}"
            ON CONFLICT (ezfb_table_name, old_item_id) DO NOTHING;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TableName", tableName);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AddAndBackfillUuidColumnAsync(
        NpgsqlConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            ALTER TABLE dbo."{tableName}" ADD COLUMN IF NOT EXISTS item_id_uuid uuid;
            UPDATE dbo."{tableName}" e
            SET item_id_uuid = m.new_item_id
            FROM dbo._ezfb_item_id_migration m
            WHERE m.ezfb_table_name = @TableName
              AND m.old_item_id = e.item_id
              AND e.item_id_uuid IS NULL;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection) { CommandTimeout = 300 };
        cmd.Parameters.AddWithValue("@TableName", tableName);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> UpdateProcessFormReferencesAsync(
        NpgsqlConnection connection,
        string ezfbTableName,
        IReadOnlyList<string> wFormIds,
        CancellationToken cancellationToken)
    {
        if (wFormIds.Count == 0)
            return 0;

        var tables = await ListWorkflowTablesAsync(connection, "process_form_%", cancellationToken);
        var total = 0;
        foreach (var wfTable in tables)
        {
            await EnsureUuidFormEntryColumnAsync(connection, wfTable, cancellationToken);
            var sql = $"""
                UPDATE {wfTable} pf
                SET form_entry_id_uuid = m.new_item_id
                FROM dbo._ezfb_item_id_migration m
                WHERE m.ezfb_table_name = @EzfbTable
                  AND pf.form_entry_id::text = m.old_item_id::text
                  AND pf.w_form_id = ANY(@WFormIds)
                  AND pf.form_entry_id_uuid IS NULL;
                """;
            await using var cmd = new NpgsqlCommand(sql, connection) { CommandTimeout = 120 };
            cmd.Parameters.AddWithValue("@EzfbTable", ezfbTableName);
            cmd.Parameters.AddWithValue("@WFormIds", wFormIds.ToArray());
            try
            {
                total += await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (PostgresException)
            {
            }
        }

        return total;
    }

    private static async Task<int> UpdateWorkflowFormsReferencesAsync(
        NpgsqlConnection connection,
        string ezfbTableName,
        IReadOnlyList<string> wFormIds,
        CancellationToken cancellationToken)
    {
        var tables = await ListWorkflowTablesAsync(connection, "workflow_forms_%", cancellationToken);
        var total = 0;
        foreach (var wfTable in tables)
        {
            await EnsureUuidFormEntryColumnAsync(connection, wfTable, cancellationToken);
            var sql = $"""
                UPDATE {wfTable} wf
                SET form_entry_id_uuid = m.new_item_id
                FROM dbo._ezfb_item_id_migration m
                WHERE m.ezfb_table_name = @EzfbTable
                  AND wf.form_entry_id::text = m.old_item_id::text
                  AND wf.form_entry_id_uuid IS NULL;
                """;
            await using var cmd = new NpgsqlCommand(sql, connection) { CommandTimeout = 120 };
            cmd.Parameters.AddWithValue("@EzfbTable", ezfbTableName);
            try
            {
                total += await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (PostgresException)
            {
            }
        }

        return total;
    }

    private static async Task UpdateWorkflowTasksReferencesAsync(
        NpgsqlConnection connection,
        string ezfbTableName,
        IReadOnlyList<string> wFormIds,
        CancellationToken cancellationToken)
    {
        var tables = await ListWorkflowTablesAsync(connection, "workflow_tasks_%", cancellationToken);
        foreach (var wfTable in tables)
        {
            await EnsureUuidFormEntryColumnAsync(connection, wfTable, cancellationToken);
            var sql = $"""
                UPDATE {wfTable} wt
                SET form_entry_id_uuid = m.new_item_id
                FROM dbo._ezfb_item_id_migration m
                WHERE m.ezfb_table_name = @EzfbTable
                  AND wt.form_entry_id::text = m.old_item_id::text
                  AND wt.form_entry_id_uuid IS NULL;
                """;
            await using var cmd = new NpgsqlCommand(sql, connection) { CommandTimeout = 120 };
            cmd.Parameters.AddWithValue("@EzfbTable", ezfbTableName);
            try
            {
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (PostgresException)
            {
            }
        }
    }

    private static async Task FinalizeAllWorkflowFormEntryColumnsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        foreach (var prefix in new[] { "process_form_%", "workflow_forms_%", "workflow_tasks_%" })
        {
            var tables = await ListWorkflowTablesAsync(connection, prefix, cancellationToken);
            foreach (var wfTable in tables)
                await FinalizeFormEntryColumnAsync(connection, wfTable, cancellationToken);
        }
    }

    private static async Task EnsureUuidFormEntryColumnAsync(
        NpgsqlConnection connection,
        string qualifiedTable,
        CancellationToken cancellationToken)
    {
        var tableName = qualifiedTable.Split('.')[1];
        const string sql = """
            SELECT data_type FROM information_schema.columns
            WHERE table_schema = 'workflow' AND table_name = @Table AND column_name = 'form_entry_id';
            """;
        await using var check = new NpgsqlCommand(sql, connection);
        check.Parameters.AddWithValue("@Table", tableName);
        var dataType = Convert.ToString(await check.ExecuteScalarAsync(cancellationToken));
        if (string.Equals(dataType, "uuid", StringComparison.OrdinalIgnoreCase))
            return;

        var alter = $"""
            ALTER TABLE {qualifiedTable} ADD COLUMN IF NOT EXISTS form_entry_id_uuid uuid;
            """;
        await using var cmd = new NpgsqlCommand(alter, connection);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task FinalizeFormEntryColumnAsync(
        NpgsqlConnection connection,
        string qualifiedTable,
        CancellationToken cancellationToken)
    {
        var tableName = qualifiedTable.Split('.')[1];
        const string hasUuidCol = """
            SELECT COUNT(1) FROM information_schema.columns
            WHERE table_schema = 'workflow' AND table_name = @Table AND column_name = 'form_entry_id_uuid';
            """;
        await using var check = new NpgsqlCommand(hasUuidCol, connection);
        check.Parameters.AddWithValue("@Table", tableName);
        if (Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 0)
            return;

        var sql = $"""
            ALTER TABLE {qualifiedTable} DROP COLUMN IF EXISTS form_entry_id;
            ALTER TABLE {qualifiedTable} RENAME COLUMN form_entry_id_uuid TO form_entry_id;
            ALTER TABLE {qualifiedTable} ALTER COLUMN form_entry_id SET NOT NULL;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection) { CommandTimeout = 120 };
        try
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException)
        {
        }
    }

    private static async Task SwapEzfbPrimaryKeyAsync(
        NpgsqlConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            ALTER TABLE dbo."{tableName}" DROP CONSTRAINT IF EXISTS "{tableName}_pkey";
            ALTER TABLE dbo."{tableName}" DROP COLUMN IF EXISTS item_id;
            ALTER TABLE dbo."{tableName}" RENAME COLUMN item_id_uuid TO item_id;
            ALTER TABLE dbo."{tableName}" ALTER COLUMN item_id SET NOT NULL;
            ALTER TABLE dbo."{tableName}" ADD PRIMARY KEY (item_id);
            """;
        await using var cmd = new NpgsqlCommand(sql, connection) { CommandTimeout = 300 };
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MigrateHistoryTableAsync(
        NpgsqlConnection connection,
        string itemsTableName,
        CancellationToken cancellationToken)
    {
        if (!itemsTableName.EndsWith("_items", StringComparison.Ordinal))
            return;

        var historyTable = itemsTableName[..^"_items".Length] + "_history";
        const string existsSql = """
            SELECT 1 FROM information_schema.tables
            WHERE table_schema = 'dbo' AND table_name = @TableName;
            """;
        await using (var existsCmd = new NpgsqlCommand(existsSql, connection))
        {
            existsCmd.Parameters.AddWithValue("@TableName", historyTable);
            var exists = await existsCmd.ExecuteScalarAsync(cancellationToken);
            if (exists == null || exists == DBNull.Value)
                return;
        }

        var sql = $"""
            ALTER TABLE dbo."{historyTable}" ADD COLUMN IF NOT EXISTS item_id_uuid uuid;
            UPDATE dbo."{historyTable}" h
            SET item_id_uuid = m.new_item_id
            FROM dbo._ezfb_item_id_migration m
            WHERE m.ezfb_table_name = @ItemsTable
              AND h.item_id = m.old_item_id
              AND h.item_id_uuid IS NULL;
            ALTER TABLE dbo."{historyTable}" DROP COLUMN IF EXISTS item_id;
            ALTER TABLE dbo."{historyTable}" RENAME COLUMN item_id_uuid TO item_id;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection) { CommandTimeout = 300 };
        cmd.Parameters.AddWithValue("@ItemsTable", itemsTableName);
        try
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException)
        {
            // History column may already be uuid.
        }
    }

    private static async Task MigrateWorkflowFormEntryIdColumnsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        foreach (var prefix in new[] { "process_form_%", "workflow_forms_%", "workflow_tasks_%" })
        {
            var tables = await ListWorkflowTablesAsync(connection, prefix, cancellationToken);
            foreach (var table in tables)
            {
                var alterSql = $"""
                    DO $body$
                    BEGIN
                        IF EXISTS (
                            SELECT 1 FROM information_schema.columns
                            WHERE table_schema = 'workflow'
                              AND table_name = '{table.Split('.')[1]}'
                              AND column_name = 'form_entry_id'
                              AND data_type = 'integer'
                        ) THEN
                            EXECUTE 'ALTER TABLE {table} ALTER COLUMN form_entry_id TYPE uuid USING form_entry_id::text::uuid';
                        END IF;
                    END $body$;
                    """;
                await using var cmd = new NpgsqlCommand(alterSql, connection) { CommandTimeout = 120 };
                try
                {
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (PostgresException)
                {
                }
            }
        }
    }

    private static async Task<List<string>> ListWorkflowTablesAsync(
        NpgsqlConnection connection,
        string namePattern,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = 'workflow' AND table_name LIKE @Pattern
            ORDER BY table_name;
            """;
        var list = new List<string>();
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Pattern", namePattern);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            list.Add($"workflow.{reader.GetString(0)}");
        return list;
    }
}
