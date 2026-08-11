using System.Collections.Concurrent;
using Npgsql;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>
/// Creates dynamic tables for each workflow.
/// When a workflow is published, creates dedicated tables: comments_x, attachments_x, etc.
///
/// PHASE 4 PORT NOTE: unlike the rest of this migration (Phases 2-3), which quotes and
/// preserves the original PascalCase identifiers throughout, this file's dynamically
/// created per-workflow tables and columns use plain lowercase snake_case, per Decision 1's
/// explicit guidance ("dynamic DDL emits lowercase directly") and the Phase 4 prompt's own
/// emphasis that this specific file is where inconsistent casing does the most damage.
/// This is a deliberate, localized exception: every OTHER file in this migration that reads/
/// writes these same dynamic tables (FormService.cs, WorkflowInstanceStore.cs,
/// DynamicTableRepository.cs, etc. -- ported later in Phase 4) must use the same lowercase
/// names this file establishes. No cross-schema FK exists from these dynamic tables to the
/// PascalCase-quoted static tables from Phase 2/3 (e.g. workflow."Workflows"), so there is no
/// referential-integrity conflict between the two conventions, but any raw SQL elsewhere that
/// joins across both needs to know which convention applies to which table.
///
/// Postgres's native CREATE TABLE IF NOT EXISTS / CREATE INDEX IF NOT EXISTS / ADD COLUMN IF
/// NOT EXISTS replace the SQL Server sys.tables/sys.columns/sys.indexes existence-check
/// ceremony throughout -- no functional change, just far less boilerplate.
/// </summary>
public sealed class WorkflowTableCreator : IWorkflowTableCreator
{
    private static readonly ConcurrentDictionary<string, byte> LegacyTransactionSchemaEnsured = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> LegacyMailboxSchemaEnsured = new(StringComparer.OrdinalIgnoreCase);

    public async Task<bool> WorkflowCoreTablesExistAsync(
        Guid workflowId,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        var suffix = workflowId.ToString("N")[..8];
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT EXISTS (
                SELECT 1 FROM information_schema.tables
                WHERE table_schema = 'workflow' AND table_name = @TableName
            );
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TableName", $"workflow_instances_{suffix}");
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// Fast path for workflow start: skip full DDL when tables already exist (created at publish).
    /// </summary>
    public async Task EnsureWorkflowTablesForStartAsync(
        Guid workflowId,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        if (await WorkflowCoreTablesExistAsync(workflowId, connectionString, cancellationToken))
        {
            await EnsureLegacyTransactionTableAsync(workflowId, connectionString, cancellationToken);
            await EnsureLegacyMailboxTablesAsync(workflowId, connectionString, cancellationToken);
            await EnsureAgentDataValidationTableAsync(workflowId, connectionString, cancellationToken);
            return;
        }

        await CreateWorkflowTablesAsync(workflowId, connectionString, cancellationToken);
    }

    public async Task CreateWorkflowTablesAsync(Guid workflowId, string connectionString, CancellationToken cancellationToken = default)
    {
        var workflowIdStr = workflowId.ToString("N"); // Remove hyphens for table names
        var workflowIdDashed = workflowId.ToString("D");
        var suffix = workflowIdStr.Substring(0, 8); // Use first 8 chars as suffix

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var schemaCmd = new NpgsqlCommand("CREATE SCHEMA IF NOT EXISTS workflow;", connection))
        {
            schemaCmd.CommandTimeout = 120;
            await schemaCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var tables = new[]
        {
            GenerateWorkflowInstancesTableScript(suffix),
            GenerateWorkflowStepInstancesTableScript(suffix),
            GenerateWorkflowInstanceSlasTableScript(suffix),
            GenerateWorkflowInstanceUserStateTableScript(suffix),
            GenerateLegacyTransactionTableScript(suffix),
            GenerateProcessFormTableScript(suffix),
            GenerateProcessAddonTableScript(suffix),
            GenerateCommentsTableScript(suffix),
            GenerateAttachmentsTableScript(suffix),
            GenerateFormsTableScript(suffix),
            GenerateTasksTableScript(suffix),
            GenerateSignaturesTableScript(suffix),
            GenerateDocumentsTableScript(suffix),
            GenerateEmailsTableScript(suffix),
            GenerateAiValidationsTableScript(suffix),
            GenerateAgentDataValidationTableScript(suffix),
            GeneratePdfAnnotationsTableScript(suffix)
        };

        foreach (var script in tables)
        {
            await using var command = new NpgsqlCommand(script, connection);
            command.CommandTimeout = 120;
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (PostgresException ex)
            {
                throw new InvalidOperationException(
                    $"Failed creating workflow tables for suffix '{suffix}' (workflow {workflowIdDashed}): {ex.Message}",
                    ex);
            }
        }

        await EnsureLegacyTransactionTableAsync(connection, suffix, cancellationToken);
        await EnsureLegacyMailboxTablesAsync(connection, suffix, cancellationToken);

        LegacyTransactionSchemaEnsured.TryAdd(suffix, 0);
        LegacyMailboxSchemaEnsured.TryAdd(suffix, 0);
    }

    /// <summary>Creates or upgrades workflow.transaction_{suffix} (transaction_guid column + index).</summary>
    public async Task EnsureLegacyTransactionTableAsync(
        Guid workflowId,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        var suffix = workflowId.ToString("N")[..8];
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureLegacyTransactionTableAsync(connection, suffix, cancellationToken);
    }

    public async Task EnsureLegacyMailboxTablesAsync(
        Guid workflowId,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        var suffix = workflowId.ToString("N")[..8];
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureLegacyMailboxTablesAsync(connection, suffix, cancellationToken);
    }

    public async Task EnsureAgentDataValidationTableAsync(
        Guid workflowId,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        var suffix = workflowId.ToString("N")[..8];
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(GenerateAgentDataValidationTableScript(suffix), connection)
        {
            CommandTimeout = 120
        };
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureLegacyTransactionTableAsync(
        NpgsqlConnection connection,
        string suffix,
        CancellationToken cancellationToken)
    {
        if (LegacyTransactionSchemaEnsured.ContainsKey(suffix))
            return;

        await using (var cmd = new NpgsqlCommand(GenerateLegacyTransactionTableScript(suffix), connection))
        {
            cmd.CommandTimeout = 120;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var tableName = $"transaction_{suffix}";
        var alterSql = $@"ALTER TABLE workflow.{tableName} ADD COLUMN IF NOT EXISTS transaction_guid uuid NULL;";
        await using (var alterCmd = new NpgsqlCommand(alterSql, connection))
        {
            alterCmd.CommandTimeout = 120;
            await alterCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var idx = suffix.Replace("-", "_");
        var indexSql = $@"CREATE UNIQUE INDEX IF NOT EXISTS ix_transaction_{idx}_transaction_guid
            ON workflow.{tableName} (transaction_guid)
            WHERE transaction_guid IS NOT NULL;";
        await using (var indexCmd = new NpgsqlCommand(indexSql, connection))
        {
            indexCmd.CommandTimeout = 120;
            await indexCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await MigrateTransactionWorkflowInstanceIdAsync(connection, suffix, cancellationToken);
        LegacyTransactionSchemaEnsured.TryAdd(suffix, 0);
    }

    private static async Task MigrateTransactionWorkflowInstanceIdAsync(
        NpgsqlConnection connection,
        string suffix,
        CancellationToken cancellationToken)
    {
        var tableName = $"transaction_{suffix}";
        var idx = suffix.Replace("-", "_");

        // ADD COLUMN IF NOT EXISTS is idempotent -- the SQL Server COL_LENGTH-guarded
        // ALTER ADD becomes a single unconditional statement.
        await using (var cmd = new NpgsqlCommand(
            $"ALTER TABLE workflow.{tableName} ADD COLUMN IF NOT EXISTS workflow_instance_id uuid NULL;",
            connection) { CommandTimeout = 120 })
            await cmd.ExecuteNonQueryAsync(cancellationToken);

        // Drop the legacy process_id column and its index, if present (one-time migration
        // step -- checked defensively via information_schema since Postgres errors on
        // DROP COLUMN/DROP INDEX against a column/index that doesn't exist, unlike SQL
        // Server's COL_LENGTH/sys.indexes checks which just evaluate to false).
        // Note: the existence checks run as plain parameterized queries in C#, not inside a
        // DO $do$ ... $do$ block -- Npgsql treats dollar-quoted text as a literal string and
        // never rewrites @-parameters that appear inside it, so a parameter referenced only
        // inside a DO block reaches Postgres as the literal text "@TableName", which Postgres
        // parses as the "@" (absolute value) prefix operator applied to an identifier, not as
        // a bind variable (confirmed empirically: caused "column tablename does not exist").
        var indexName = $"ix_transaction_{idx}_process_id_is_deleted";

        async Task<bool> HasColumnAsync(string columnName)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'workflow' AND table_name = @TableName AND column_name = @ColumnName);",
                connection);
            cmd.Parameters.AddWithValue("TableName", tableName);
            cmd.Parameters.AddWithValue("ColumnName", columnName);
            return (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!;
        }

        if (await HasColumnAsync("process_id"))
        {
            await using (var idxCmd = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'workflow' AND indexname = @IdxName);",
                connection))
            {
                idxCmd.Parameters.AddWithValue("IdxName", indexName);
                var hasIndex = (bool)(await idxCmd.ExecuteScalarAsync(cancellationToken))!;
                if (hasIndex)
                {
                    await using var dropIdxCmd = new NpgsqlCommand($"DROP INDEX workflow.{indexName};", connection) { CommandTimeout = 120 };
                    await dropIdxCmd.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            if (await HasColumnAsync("workflow_instance_id"))
            {
                await using var dropColCmd = new NpgsqlCommand($"ALTER TABLE workflow.{tableName} DROP COLUMN process_id;", connection) { CommandTimeout = 120 };
                await dropColCmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        var indexSql = $@"CREATE INDEX IF NOT EXISTS ix_transaction_{idx}_workflow_instance_id_is_deleted
            ON workflow.{tableName} (workflow_instance_id, is_deleted);";
        await using var indexCmd = new NpgsqlCommand(indexSql, connection) { CommandTimeout = 120 };
        await indexCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DropWorkflowTablesAsync(Guid workflowId, string connectionString, CancellationToken cancellationToken = default)
    {
        var workflowIdStr = workflowId.ToString("N");
        var suffix = workflowIdStr.Substring(0, 8);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // Remove lookup rows for this workflow's instances before dropping tables
        const string deleteLookup = "DELETE FROM workflow.\"WorkflowInstanceLookup\" WHERE \"WorkflowId\" = @WorkflowId";
        await using (var cmd = new NpgsqlCommand(deleteLookup, connection))
        {
            cmd.Parameters.AddWithValue("@WorkflowId", workflowId);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var tableNames = new[]
        {
            $"workflow_instance_slas_{suffix}",
            $"workflow_step_instances_{suffix}",
            $"workflow_instances_{suffix}",
            $"workflow_instance_user_state_{suffix}",
            $"transaction_{suffix}",
            $"process_form_{suffix}",
            $"process_addon_{suffix}",
            $"inbox_{suffix}",
            $"sent_{suffix}",
            $"completed_{suffix}",
            $"inbox_{workflowIdStr}",
            $"sent_{workflowIdStr}",
            $"completed_{workflowIdStr}",
            $"workflow_comments_{suffix}",
            $"workflow_attachments_{suffix}",
            $"workflow_forms_{suffix}",
            $"workflow_tasks_{suffix}",
            $"workflow_signatures_{suffix}",
            $"workflow_documents_{suffix}",
            $"workflow_emails_{suffix}",
            $"workflow_ai_validations_{suffix}",
            $"agent_data_validation_{suffix}",
            $"workflow_pdf_annotations_{suffix}"
        };

        foreach (var tableName in tableNames)
        {
            var script = $"DROP TABLE IF EXISTS workflow.{tableName};";

            await using var command = new NpgsqlCommand(script, connection);
            command.CommandTimeout = 120;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task EnsureLegacyMailboxTablesAsync(
        NpgsqlConnection connection,
        string workflowKey,
        CancellationToken cancellationToken)
    {
        if (!LegacyMailboxSchemaEnsured.ContainsKey(workflowKey))
        {
            var inbox = GenerateLegacyMailboxTableScript("inbox", workflowKey);
            var sent = GenerateLegacyMailboxTableScript("sent", workflowKey);
            var completed = GenerateLegacyMailboxTableScript("completed", workflowKey);

            foreach (var script in new[] { inbox, sent, completed })
            {
                await using var cmd = new NpgsqlCommand(script, connection);
                cmd.CommandTimeout = 120;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await EnsureLegacyMailboxIndexesAsync(connection, workflowKey, cancellationToken);
            await EnsureLegacyTransactionMailboxIndexesAsync(connection, workflowKey, cancellationToken);
            LegacyMailboxSchemaEnsured.TryAdd(workflowKey, 0);
        }

        // Always run idempotent column migrates (e.g. action) so existing DBs pick up new columns.
        await MigrateLegacyMailboxInstanceColumnsAsync(connection, workflowKey, cancellationToken);
    }

    private static async Task EnsureLegacyTransactionMailboxIndexesAsync(
        NpgsqlConnection connection,
        string workflowKey,
        CancellationToken cancellationToken)
    {
        var table = $"transaction_{workflowKey}";
        var idx = $"ix_{table}_instance_status";
        var sql = $@"
DO $do$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'workflow' AND table_name = '{table}') THEN
        CREATE INDEX IF NOT EXISTS {idx}
            ON workflow.{table} (workflow_instance_id, is_deleted, action_status)
            INCLUDE (activity_user_id, created_by, stage_type, activity_group_id);
    END IF;
END
$do$;";
        await using var cmd = new NpgsqlCommand(sql, connection) { CommandTimeout = 120 };
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureLegacyMailboxIndexesAsync(
        NpgsqlConnection connection,
        string workflowKey,
        CancellationToken cancellationToken)
    {
        foreach (var prefix in new[] { "inbox", "sent", "completed" })
        {
            var table = $"{prefix}_{workflowKey}";
            var idxInstance = $"ix_{table}_instance_created";
            var idxUser = $"ix_{table}_user_created";
            var sql = $@"
DO $do$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'workflow' AND table_name = '{table}') THEN
        CREATE INDEX IF NOT EXISTS {idxInstance}
            ON workflow.{table} (workflow_instance_id, transaction_created_at DESC, id DESC)
            INCLUDE (user_id, transaction_created_by, transaction_id, name, reference_number, stage, review);
        CREATE INDEX IF NOT EXISTS {idxUser}
            ON workflow.{table} (user_id, transaction_created_at DESC, id DESC)
            INCLUDE (transaction_created_by, workflow_instance_id, transaction_id, name, reference_number);
    END IF;
END
$do$;";
            await using var cmd = new NpgsqlCommand(sql, connection) { CommandTimeout = 120 };
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task MigrateLegacyMailboxInstanceColumnsAsync(
        NpgsqlConnection connection,
        string workflowKey,
        CancellationToken cancellationToken)
    {
        foreach (var prefix in new[] { "inbox", "sent", "completed" })
        {
            var table = $"{prefix}_{workflowKey}";
            var sql = $@"
DO $do$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'workflow' AND table_name = '{table}') THEN
        ALTER TABLE workflow.{table} ADD COLUMN IF NOT EXISTS workflow_instance_id varchar(255) NULL;
        ALTER TABLE workflow.{table} ADD COLUMN IF NOT EXISTS reference_number varchar(128) NULL;
        ALTER TABLE workflow.{table} ADD COLUMN IF NOT EXISTS created_at_utc timestamptz NULL;
        ALTER TABLE workflow.{table} ADD COLUMN IF NOT EXISTS started_at_utc timestamptz NULL;
        ALTER TABLE workflow.{table} ADD COLUMN IF NOT EXISTS completed_at_utc timestamptz NULL;
        ALTER TABLE workflow.{table} ADD COLUMN IF NOT EXISTS context varchar(4000) NULL;
        ALTER TABLE workflow.{table} ADD COLUMN IF NOT EXISTS repository_id varchar(255) NULL;
        ALTER TABLE workflow.{table} ADD COLUMN IF NOT EXISTS item_id varchar(255) NULL;
        ALTER TABLE workflow.{table} ADD COLUMN IF NOT EXISTS form_id varchar(255) NULL;
        ALTER TABLE workflow.{table} ADD COLUMN IF NOT EXISTS form_entry_id varchar(255) NULL;
        -- SQL Server's original also ALTERed formData from a shorter type to nvarchar(max)
        -- if some minimal bootstrap path had created it smaller; text has no length variant
        -- to migrate away from on Postgres, so ADD COLUMN IF NOT EXISTS alone covers both cases.
        ALTER TABLE workflow.{table} ADD COLUMN IF NOT EXISTS form_data text NULL;
        ALTER TABLE workflow.{table} ADD COLUMN IF NOT EXISTS ""action"" integer NOT NULL DEFAULT 1;
    END IF;
END
$do$;";
            await using var cmd = new NpgsqlCommand(sql, connection) { CommandTimeout = 120 };
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static string GenerateLegacyMailboxTableScript(string prefix, string workflowKey)
    {
        // Based on provided schema; kept identical across inbox/sent/completed.
        // "action" is a reserved-adjacent word in Postgres, quoted defensively (it isn't
        // actually reserved, but quoting avoids any doubt).
        return $@"
CREATE TABLE IF NOT EXISTS workflow.{prefix}_{workflowKey} (
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id varchar(100) NULL,
    group_id integer NULL,
    workflow_id varchar(255) NULL,
    name varchar(255) NULL,
    workflow_instance_id varchar(255) NULL,
    reference_number varchar(128) NULL,
    created_at_utc timestamptz NULL,
    started_at_utc timestamptz NULL,
    completed_at_utc timestamptz NULL,
    context varchar(4000) NULL,
    transaction_id varchar(255) NULL,
    activity_id varchar(255) NULL,
    rule_id varchar(255) NULL,
    stage_type varchar(255) NULL,
    stage varchar(255) NULL,
    review varchar(255) NULL,
    transaction_created_at timestamptz NULL,
    transaction_created_by varchar(255) NULL,
    transaction_created_by_email varchar(255) NULL,
    repository_id varchar(255) NULL,
    item_id varchar(255) NULL,
    form_id varchar(255) NULL,
    form_entry_id varchar(255) NULL,
    ml_prediction varchar(255) NULL,
    transaction_modified_at timestamptz NULL,
    transaction_modified_by varchar(255) NULL,
    ml_condition varchar(255) NULL,
    user_type varchar(255) NULL,
    jira_issue_json text NULL,
    created_by_name varchar(255) NULL,
    jira_issue_info text NULL,
    last_action_stage_type varchar(50) NULL,
    last_action_stage_name varchar(50) NULL,
    last_action varchar(50) NULL,
    form_data text NULL,
    comments_count integer NULL,
    attachment_count integer NULL,
    item_data text NULL,
    activity_user_email text NULL,
    activity_group_name text NULL,
    ""action"" integer NOT NULL DEFAULT 1
);";
    }

    private static string GenerateWorkflowInstancesTableScript(string suffix)
    {
        return $@"
CREATE TABLE IF NOT EXISTS workflow.workflow_instances_{suffix} (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    workflow_id uuid NOT NULL,
    workflow_name varchar(256) NOT NULL,
    workflow_version integer NOT NULL,
    status integer NOT NULL,
    current_step_instance_id uuid NULL,
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    started_at_utc timestamptz NULL,
    completed_at_utc timestamptz NULL,
    started_by uuid NOT NULL,
    context varchar(4000) NULL,
    error_message varchar(2000) NULL,
    reference_number varchar(128) NULL,
    customer_name varchar(256) NULL,
    customer_email varchar(256) NULL,
    customer_phone varchar(64) NULL,
    department varchar(128) NULL,
    category varchar(128) NULL,
    priority integer NOT NULL DEFAULT 1,
    tags varchar(1000) NULL,
    custom_fields_json varchar(4000) NULL,
    assigned_to_user_id uuid NULL,
    assigned_to_group_id uuid NULL,
    last_activity_at_utc timestamptz NULL,
    view_count integer NOT NULL DEFAULT 0,
    is_archived boolean NOT NULL DEFAULT false,
    archived_at_utc timestamptz NULL,
    source_type varchar(64) NULL,
    source_id varchar(256) NULL,
    last_viewed_at_utc timestamptz NULL,
    last_viewed_by uuid NULL
);
CREATE INDEX IF NOT EXISTS ix_workflow_instances_{suffix}_tenant_id_workflow_id ON workflow.workflow_instances_{suffix} (tenant_id, workflow_id);
CREATE INDEX IF NOT EXISTS ix_workflow_instances_{suffix}_tenant_id_status_is_archived ON workflow.workflow_instances_{suffix} (tenant_id, status, is_archived);
CREATE INDEX IF NOT EXISTS ix_workflow_instances_{suffix}_reference_number ON workflow.workflow_instances_{suffix} (reference_number);";
    }

    private static string GenerateWorkflowStepInstancesTableScript(string suffix)
    {
        return $@"
CREATE TABLE IF NOT EXISTS workflow.workflow_step_instances_{suffix} (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    workflow_instance_id uuid NOT NULL REFERENCES workflow.workflow_instances_{suffix} (id) ON DELETE CASCADE,
    workflow_step_id uuid NOT NULL,
    step_name varchar(256) NOT NULL,
    step_type integer NOT NULL,
    ""order"" integer NOT NULL,
    status integer NOT NULL,
    assigned_to_user_id uuid NULL,
    assigned_to_role varchar(64) NULL,
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    started_at_utc timestamptz NULL,
    completed_at_utc timestamptz NULL,
    completed_by uuid NULL,
    result varchar(4000) NULL,
    error_message varchar(2000) NULL,
    activity_id varchar(128) NULL,
    stage_type varchar(64) NULL
);
CREATE INDEX IF NOT EXISTS ix_workflow_step_instances_{suffix}_workflow_instance_id_order ON workflow.workflow_step_instances_{suffix} (workflow_instance_id, ""order"");
ALTER TABLE workflow.workflow_step_instances_{suffix} ADD COLUMN IF NOT EXISTS activity_id varchar(128) NULL;
ALTER TABLE workflow.workflow_step_instances_{suffix} ADD COLUMN IF NOT EXISTS stage_type varchar(64) NULL;";
    }

    private static string GenerateWorkflowInstanceSlasTableScript(string suffix)
    {
        return $@"
CREATE TABLE IF NOT EXISTS workflow.workflow_instance_slas_{suffix} (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    workflow_instance_id uuid NOT NULL REFERENCES workflow.workflow_instances_{suffix} (id) ON DELETE CASCADE,
    priority integer NOT NULL,
    response_deadline timestamptz NOT NULL,
    resolution_deadline timestamptz NOT NULL,
    escalation_deadline timestamptz NULL,
    response_achieved_at timestamptz NULL,
    resolution_achieved_at timestamptz NULL,
    response_status integer NOT NULL,
    resolution_status integer NOT NULL,
    is_escalated boolean NOT NULL DEFAULT false,
    escalated_at timestamptz NULL,
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_workflow_instance_slas_{suffix}_workflow_instance_id UNIQUE (workflow_instance_id)
);
CREATE INDEX IF NOT EXISTS ix_workflow_instance_slas_{suffix}_response_status_resolution_status ON workflow.workflow_instance_slas_{suffix} (response_status, resolution_status);";
    }

    private static string GenerateWorkflowInstanceUserStateTableScript(string suffix)
    {
        return $@"
CREATE TABLE IF NOT EXISTS workflow.workflow_instance_user_state_{suffix} (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    workflow_instance_id uuid NOT NULL,
    user_id uuid NOT NULL,
    is_read boolean NOT NULL DEFAULT false,
    read_at_utc timestamptz NULL,
    is_actioned boolean NOT NULL DEFAULT false,
    actioned_at_utc timestamptz NULL,
    created_at_utc timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_workflow_instance_user_state_{suffix}_workflow_instance_id_user_id ON workflow.workflow_instance_user_state_{suffix} (workflow_instance_id, user_id);";
    }

    private static string GenerateLegacyTransactionTableScript(string suffix)
    {
        var idx = suffix.Replace("-", "_");
        return $@"
CREATE TABLE IF NOT EXISTS workflow.transaction_{suffix} (
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    transaction_guid uuid NULL,
    workflow_instance_id uuid NOT NULL,
    activity_id varchar(128) NULL,
    rule_id varchar(128) NULL,
    stage_type varchar(64) NULL,
    stage_name varchar(256) NULL,
    review varchar(64) NULL,
    action_status integer NOT NULL DEFAULT 0,
    activity_user_id uuid NULL,
    activity_group_id integer NULL,
    user_ids text NULL,        -- v5 parity (comma-separated legacy ids)
    group_ids text NULL,       -- v5 parity (comma-separated legacy ids)
    sla_transaction_id integer NULL,  -- v5 parity
    input_from varchar(64) NULL,
    level_id integer NULL,
    user_type varchar(64) NULL,
    jira_issue_json text NULL,
    ml_prediction text NULL,
    ml_condition text NULL,
    ticket_locked_by uuid NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    created_by uuid NULL,
    modified_at timestamptz NULL,
    modified_by uuid NULL,
    is_deleted boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS ix_transaction_{idx}_workflow_instance_id_is_deleted ON workflow.transaction_{suffix} (workflow_instance_id, is_deleted);
CREATE INDEX IF NOT EXISTS ix_transaction_{idx}_activity_user_action_status ON workflow.transaction_{suffix} (activity_user_id, action_status);
CREATE INDEX IF NOT EXISTS ix_transaction_{idx}_instance_status ON workflow.transaction_{suffix} (workflow_instance_id, is_deleted, action_status) INCLUDE (activity_user_id, created_by, stage_type, activity_group_id);";
    }

    private static string GenerateCommentsTableScript(string suffix)
    {
        return $@"
CREATE TABLE IF NOT EXISTS workflow.workflow_comments_{suffix} (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    workflow_instance_id uuid NOT NULL,
    step_instance_id uuid NULL,
    comments varchar(4000) NOT NULL,
    external_comments_by varchar(256) NULL,
    show_to integer NOT NULL DEFAULT 0,
    embed_json varchar(4000) NULL,
    embed_status boolean NOT NULL DEFAULT false,
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    modified_at_utc timestamptz NULL,
    created_by uuid NOT NULL,
    modified_by uuid NULL,
    is_deleted boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS ix_workflow_comments_{suffix}_tenant_id_workflow_instance_id ON workflow.workflow_comments_{suffix} (tenant_id, workflow_instance_id, is_deleted);";
    }

    private static string GenerateAttachmentsTableScript(string suffix)
    {
        var tableName = $"workflow_attachments_{suffix}";

        return $@"
CREATE TABLE IF NOT EXISTS workflow.{tableName} (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    workflow_instance_id uuid NOT NULL,
    step_instance_id uuid NULL,
    repository_id uuid NULL,
    item_id uuid NULL,
    form_json_id varchar(128) NULL,
    file_name varchar(512) NULL,
    file_path varchar(1024) NULL,
    file_size bigint NULL,
    content_type varchar(128) NULL,
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    modified_at_utc timestamptz NULL,
    created_by uuid NOT NULL,
    modified_by uuid NULL,
    is_deleted boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS ix_{tableName}_tenant_id_workflow_instance_id ON workflow.{tableName} (tenant_id, workflow_instance_id, is_deleted);
-- Legacy INT->GUID upgrade path (repository_guid/item_guid mistaken columns, repository_id/
-- item_id typed int) is not carried over: this table is always created with uuid columns
-- from the start on Postgres (Phase 3 confirmed no Postgres tenant will ever have the
-- legacy int-typed shape), so the defensive ALTER/DROP dance the SQL Server version needed
-- has nothing to do here.";
    }

    /// <summary>Legacy v5 process_form_{suffix}: workflow_instance_id + ezfb itemId as form_entry_id (no process_id).</summary>
    private static string GenerateProcessFormTableScript(string suffix)
    {
        return $@"
CREATE TABLE IF NOT EXISTS workflow.process_form_{suffix} (
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    workflow_instance_id uuid NOT NULL,
    w_form_id varchar(64) NOT NULL,
    form_entry_id integer NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    created_by uuid NOT NULL,
    is_deleted boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS ix_process_form_{suffix}_workflow_instance_id_is_deleted ON workflow.process_form_{suffix} (workflow_instance_id, is_deleted);";
    }

    /// <summary>Legacy v5 process_addon_{suffix}: process (instance) -> repository item link for attachments.</summary>
    private static string GenerateProcessAddonTableScript(string suffix)
    {
        return $@"
CREATE TABLE IF NOT EXISTS workflow.process_addon_{suffix} (
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    process_id uuid NOT NULL,
    repository_id uuid NOT NULL,
    item_id uuid NOT NULL,
    file_name varchar(512) NULL,
    transaction_id integer NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    modified_at timestamptz NULL,
    created_by uuid NOT NULL,
    modified_by uuid NULL,
    is_deleted boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS ix_process_addon_{suffix}_process_id_is_deleted ON workflow.process_addon_{suffix} (process_id, is_deleted);";
    }

    private static string GenerateFormsTableScript(string suffix)
    {
        return $@"
CREATE TABLE IF NOT EXISTS workflow.workflow_forms_{suffix} (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    workflow_instance_id uuid NOT NULL,
    step_instance_id uuid NULL,
    w_form_id integer NOT NULL,
    form_entry_id integer NOT NULL,
    form_data varchar(4000) NULL,
    has_form_pdf boolean NOT NULL DEFAULT false,
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    modified_at_utc timestamptz NULL,
    created_by uuid NOT NULL,
    modified_by uuid NULL,
    is_deleted boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS ix_workflow_forms_{suffix}_tenant_id_workflow_instance_id ON workflow.workflow_forms_{suffix} (tenant_id, workflow_instance_id, is_deleted);";
    }

    private static string GenerateTasksTableScript(string suffix)
    {
        return $@"
CREATE TABLE IF NOT EXISTS workflow.workflow_tasks_{suffix} (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    workflow_instance_id uuid NOT NULL,
    step_instance_id uuid NULL,
    w_form_id integer NOT NULL,
    form_entry_id integer NOT NULL,
    task_name varchar(256) NULL,
    task_description varchar(2000) NULL,
    assigned_to_user_id uuid NULL,
    due_date timestamptz NULL,
    is_completed boolean NOT NULL DEFAULT false,
    completed_at timestamptz NULL,
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    modified_at_utc timestamptz NULL,
    created_by uuid NOT NULL,
    modified_by uuid NULL,
    is_deleted boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS ix_workflow_tasks_{suffix}_tenant_id_workflow_instance_id ON workflow.workflow_tasks_{suffix} (tenant_id, workflow_instance_id, is_deleted);
CREATE INDEX IF NOT EXISTS ix_workflow_tasks_{suffix}_tenant_id_assigned_to_user_id ON workflow.workflow_tasks_{suffix} (tenant_id, assigned_to_user_id, is_completed);";
    }

    private static string GenerateSignaturesTableScript(string suffix)
    {
        return $@"
CREATE TABLE IF NOT EXISTS workflow.workflow_signatures_{suffix} (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    workflow_instance_id uuid NOT NULL,
    step_instance_id uuid NULL,
    file_name varchar(512) NOT NULL,
    file_path varchar(1024) NULL,
    signed_by uuid NOT NULL,
    signed_at_utc timestamptz NOT NULL DEFAULT now(),
    signature_data varchar(4000) NULL,
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    modified_at_utc timestamptz NULL,
    created_by uuid NOT NULL,
    modified_by uuid NULL,
    is_deleted boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS ix_workflow_signatures_{suffix}_tenant_id_workflow_instance_id ON workflow.workflow_signatures_{suffix} (tenant_id, workflow_instance_id, is_deleted);";
    }

    private static string GenerateDocumentsTableScript(string suffix)
    {
        return $@"
CREATE TABLE IF NOT EXISTS workflow.workflow_documents_{suffix} (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    workflow_id uuid NOT NULL,
    workflow_instance_id uuid NULL,
    file_name varchar(512) NOT NULL,
    description varchar(2000) NULL,
    type varchar(64) NULL,
    status integer NOT NULL DEFAULT 0,
    is_mandatory boolean NOT NULL DEFAULT false,
    file_path varchar(1024) NULL,
    uploaded_at timestamptz NULL,
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    modified_at_utc timestamptz NULL,
    created_by uuid NOT NULL,
    modified_by uuid NULL,
    is_deleted boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS ix_workflow_documents_{suffix}_tenant_id_workflow_id ON workflow.workflow_documents_{suffix} (tenant_id, workflow_id, is_deleted);
CREATE INDEX IF NOT EXISTS ix_workflow_documents_{suffix}_tenant_id_workflow_instance_id ON workflow.workflow_documents_{suffix} (tenant_id, workflow_instance_id, is_deleted);";
    }

    private static string GenerateEmailsTableScript(string suffix)
    {
        return $@"
CREATE TABLE IF NOT EXISTS workflow.workflow_emails_{suffix} (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    workflow_instance_id uuid NOT NULL,
    step_instance_id uuid NULL,
    email_type varchar(64) NOT NULL,
    ez_mail_id integer NULL,
    msg_file_name varchar(512) NULL,
    email varchar(256) NOT NULL,
    subject varchar(512) NULL,
    body varchar(4000) NULL,
    attachment_count integer NOT NULL DEFAULT 0,
    attachment_json varchar(4000) NULL,
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    modified_at_utc timestamptz NULL,
    created_by uuid NOT NULL,
    modified_by uuid NULL,
    is_deleted boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS ix_workflow_emails_{suffix}_tenant_id_workflow_instance_id ON workflow.workflow_emails_{suffix} (tenant_id, workflow_instance_id, is_deleted);";
    }

    /// <summary>Legacy AP agent OCR/validation store (agent_data_validation_{workflow8}).</summary>
    private static string GenerateAgentDataValidationTableScript(string suffix)
    {
        return $@"
CREATE TABLE IF NOT EXISTS workflow.agent_data_validation_{suffix} (
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    workflow_id uuid NOT NULL,
    process_id uuid NOT NULL,
    transaction_id varchar(256) NULL,
    type varchar(64) NULL,
    agent_response text NULL,
    agent_html_response text NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    modified_at timestamptz NULL,
    created_by uuid NOT NULL,
    modified_by uuid NULL,
    is_deleted boolean NOT NULL DEFAULT false
);";
    }

    private static string GenerateAiValidationsTableScript(string suffix)
    {
        return $@"
CREATE TABLE IF NOT EXISTS workflow.workflow_ai_validations_{suffix} (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    workflow_instance_id uuid NOT NULL,
    step_instance_id uuid NULL,
    type varchar(64) NOT NULL,
    agent_response varchar(4000) NULL,
    field_name varchar(256) NULL,
    form_value varchar(1000) NULL,
    ocr_value varchar(1000) NULL,
    validation_status varchar(64) NULL,
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    modified_at_utc timestamptz NULL,
    created_by uuid NOT NULL,
    modified_by uuid NULL,
    is_deleted boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS ix_workflow_ai_validations_{suffix}_tenant_id_workflow_instance_id ON workflow.workflow_ai_validations_{suffix} (tenant_id, workflow_instance_id, is_deleted);";
    }

    private static string GeneratePdfAnnotationsTableScript(string suffix)
    {
        return $@"
CREATE TABLE IF NOT EXISTS workflow.workflow_pdf_annotations_{suffix} (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    workflow_instance_id uuid NOT NULL,
    step_instance_id uuid NULL,
    repository_id integer NULL,
    item_id integer NULL,
    annotation_status integer NOT NULL DEFAULT 0,
    settings_json varchar(4000) NULL,
    created_at_utc timestamptz NOT NULL DEFAULT now(),
    modified_at_utc timestamptz NULL,
    created_by uuid NOT NULL,
    modified_by uuid NULL,
    is_deleted boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS ix_workflow_pdf_annotations_{suffix}_tenant_id_workflow_instance_id ON workflow.workflow_pdf_annotations_{suffix} (tenant_id, workflow_instance_id, is_deleted);";
    }
}
