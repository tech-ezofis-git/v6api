using System.Collections.Concurrent;
using Npgsql;
using SaaSApp.Workflow.Application;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Infrastructure.Services;

public sealed class ApAgentJobProgressService : IApAgentJobProgressService
{
    private const string TableName = "workflow.\"ApAgentJobProgress\"";
    private static readonly ConcurrentDictionary<string, byte> TableEnsured = new(StringComparer.Ordinal);

    private readonly ITenantContext _tenantContext;

    public ApAgentJobProgressService(ITenantContext tenantContext)
    {
        _tenantContext = tenantContext;
    }

    public async Task RegisterQueuedAsync(
        string jobId,
        Guid tenantId,
        Guid workflowId,
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        await EnsureTableAsync(cancellationToken);
        var sql = $"""
            INSERT INTO {TableName}
                ("JobId", "TenantId", "WorkflowId", "InstanceId", "HangfireState", "Stage", "Message", "ProgressPercent", "ErrorMessage", "CreatedAtUtc", "UpdatedAtUtc")
            VALUES
                (@JobId, @TenantId, @WorkflowId, @InstanceId, 'Enqueued', 'QUEUED', 'AP Agent job queued', NULL, NULL, now(), now());
            """;

        try
        {
            await ExecuteAsync(sql, cmd =>
            {
                cmd.Parameters.AddWithValue("@JobId", jobId);
                cmd.Parameters.AddWithValue("@TenantId", tenantId);
                cmd.Parameters.AddWithValue("@WorkflowId", workflowId);
                cmd.Parameters.AddWithValue("@InstanceId", instanceId);
            }, cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            TableEnsured.TryRemove(_tenantContext.ConnectionString?.Trim() ?? string.Empty, out _);
            await EnsureTableAsync(cancellationToken, force: true);
            await ExecuteAsync(sql, cmd =>
            {
                cmd.Parameters.AddWithValue("@JobId", jobId);
                cmd.Parameters.AddWithValue("@TenantId", tenantId);
                cmd.Parameters.AddWithValue("@WorkflowId", workflowId);
                cmd.Parameters.AddWithValue("@InstanceId", instanceId);
            }, cancellationToken);
        }
    }

    public Task UpdateProgressAsync(
        string jobId,
        ApAgentJobProgressUpdate update,
        CancellationToken cancellationToken = default) =>
        ApplyProgressUpdateAsync(jobId, update, cancellationToken);

    public async Task UpdateProgressByInstanceAsync(
        Guid workflowId,
        Guid instanceId,
        ApAgentJobProgressUpdate update,
        CancellationToken cancellationToken = default)
    {
        var jobId = await GetLatestActiveJobIdForInstanceAsync(instanceId, cancellationToken);
        if (string.IsNullOrWhiteSpace(jobId))
            throw new InvalidOperationException($"No active AP Agent job found for instance {instanceId:D}.");

        await ApplyProgressUpdateAsync(jobId, update, cancellationToken);
    }

    public async Task UpdateFormDataByInstanceAsync(
        Guid workflowId,
        Guid instanceId,
        string formDataJson,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(formDataJson))
            return;

        var jobId = await GetLatestJobIdForInstanceAsync(workflowId, instanceId, cancellationToken);
        if (string.IsNullOrWhiteSpace(jobId))
            throw new InvalidOperationException(
                $"No AP Agent job found for workflow {workflowId:D} instance {instanceId:D}.");

        await ApplyProgressUpdateAsync(
            jobId,
            new ApAgentJobProgressUpdate(FormData: formDataJson),
            cancellationToken);
    }

    private static bool IsTerminalStage(string? stage) =>
        string.Equals(stage, "COMPLETED", StringComparison.OrdinalIgnoreCase)
        || string.Equals(stage, "FAILED", StringComparison.OrdinalIgnoreCase);

    private static bool IsTerminalHangfireState(string? state) =>
        string.Equals(state, "Succeeded", StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, "Failed", StringComparison.OrdinalIgnoreCase);

    public async Task SetHangfireStateAsync(
        string jobId,
        string hangfireState,
        string? message = null,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsTerminalHangfireState(hangfireState))
        {
            var existing = await GetByJobIdAsync(jobId, cancellationToken);
            if (existing != null && IsTerminalHangfireState(existing.HangfireState))
                return;
        }

        await EnsureTableAsync(cancellationToken);
        var sql = $"""
            UPDATE {TableName}
            SET "HangfireState" = @HangfireState,
                "Message" = COALESCE(@Message, "Message"),
                "ErrorMessage" = @ErrorMessage,
                "Stage" = CASE
                    WHEN @HangfireState = 'Succeeded' THEN COALESCE("Stage", 'COMPLETED')
                    WHEN @HangfireState = 'Failed' THEN COALESCE("Stage", 'FAILED')
                    ELSE "Stage"
                END,
                "UpdatedAtUtc" = now()
            WHERE "JobId" = @JobId;
            """;

        await ExecuteAsync(sql, cmd =>
        {
            cmd.Parameters.AddWithValue("@JobId", jobId);
            cmd.Parameters.AddWithValue("@HangfireState", hangfireState);
            cmd.Parameters.AddWithValue("@Message", (object?)message ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ErrorMessage", (object?)errorMessage ?? DBNull.Value);
        }, cancellationToken);
    }

    public Task EnsureProgressTableAsync(CancellationToken cancellationToken = default) =>
        EnsureTableAsync(cancellationToken);

    public async Task<ApAgentJobProgressRow?> GetByJobIdAsync(string jobId, CancellationToken cancellationToken = default)
    {
        await EnsureTableAsync(cancellationToken);
        var sql = $"""
            SELECT "JobId", "TenantId", "WorkflowId", "InstanceId", "HangfireState", "Stage", "Message", "ProgressPercent", "ErrorMessage", "FormData", "UpdatedAtUtc"
            FROM {TableName}
            WHERE "JobId" = @JobId
            LIMIT 1;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@JobId", jobId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return MapRow(reader);
    }

    public async Task<string?> GetLatestActiveJobIdForInstanceAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        await EnsureTableAsync(cancellationToken);
        var sql = $"""
            SELECT "JobId"
            FROM {TableName}
            WHERE "InstanceId" = @InstanceId
              AND "HangfireState" IN ('Enqueued', 'Processing')
            ORDER BY "UpdatedAtUtc" DESC
            LIMIT 1;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@InstanceId", instanceId);
        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return value == null || value == DBNull.Value ? null : Convert.ToString(value);
    }

    public async Task<string?> GetLatestJobIdForInstanceAsync(
        Guid workflowId,
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        await EnsureTableAsync(cancellationToken);
        var sql = $"""
            SELECT "JobId"
            FROM {TableName}
            WHERE "WorkflowId" = @WorkflowId
              AND "InstanceId" = @InstanceId
            ORDER BY "UpdatedAtUtc" DESC
            LIMIT 1;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@WorkflowId", workflowId);
        cmd.Parameters.AddWithValue("@InstanceId", instanceId);
        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return value == null || value == DBNull.Value ? null : Convert.ToString(value);
    }

    private async Task ApplyProgressUpdateAsync(
        string jobId,
        ApAgentJobProgressUpdate update,
        CancellationToken cancellationToken)
    {
        await EnsureTableAsync(cancellationToken);

        var existing = await GetByJobIdAsync(jobId, cancellationToken);
        if (existing != null
            && (IsTerminalStage(existing.Stage) || IsTerminalHangfireState(existing.HangfireState))
            && !string.IsNullOrWhiteSpace(update.Stage)
            && !IsTerminalStage(update.Stage))
        {
            // Ignore late non-terminal updates after COMPLETED/FAILED (e.g. duplicate PROCESSING PATCH).
            update = update with { Stage = null, Message = null, Percent = null };
            if (update.FormData == null)
                return;
        }

        var sql = $"""
            UPDATE {TableName}
            SET "Stage" = COALESCE(@Stage, "Stage"),
                "Message" = COALESCE(@Message, "Message"),
                "ProgressPercent" = COALESCE(@ProgressPercent, "ProgressPercent"),
                "FormData" = COALESCE(@FormData, "FormData"),
                "UpdatedAtUtc" = now()
            WHERE "JobId" = @JobId;
            """;

        await ExecuteAsync(sql, cmd =>
        {
            cmd.Parameters.AddWithValue("@JobId", jobId);
            cmd.Parameters.AddWithValue("@Stage", (object?)update.Stage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Message", (object?)update.Message ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ProgressPercent", (object?)update.Percent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@FormData", (object?)update.FormData ?? DBNull.Value);
        }, cancellationToken);

        if (string.IsNullOrWhiteSpace(update.Stage))
            return;

        if (string.Equals(update.Stage, "COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            await SetHangfireStateAsync(
                jobId,
                "Succeeded",
                update.Message ?? "AP Agent finished successfully",
                cancellationToken: cancellationToken);
        }
        else if (string.Equals(update.Stage, "FAILED", StringComparison.OrdinalIgnoreCase))
        {
            await SetHangfireStateAsync(
                jobId,
                "Failed",
                update.Message ?? "AP Agent failed",
                update.Message,
                cancellationToken);
        }
    }

    private async Task EnsureTableAsync(CancellationToken cancellationToken, bool force = false)
    {
        var connectionString = _tenantContext.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Tenant connection string not resolved.");

        var cacheKey = connectionString.Trim();
        if (!force && TableEnsured.ContainsKey(cacheKey) && await TableExistsAsync(cancellationToken))
            return;

        TableEnsured.TryRemove(cacheKey, out _);

        const string ensureSchemaSql = "CREATE SCHEMA IF NOT EXISTS workflow;";

        const string dropLegacySql = """
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'workflow' AND table_name = 'ApAgentJobProgress')
                   AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'workflow' AND table_name = 'ApAgentJobProgress' AND column_name = 'ProgressPercent')
                THEN
                    DROP TABLE workflow."ApAgentJobProgress";
                END IF;
            END $$;
            """;

        const string createTableSql = """
            CREATE TABLE IF NOT EXISTS workflow."ApAgentJobProgress" (
                "JobId" varchar(64) NOT NULL PRIMARY KEY,
                "TenantId" uuid NOT NULL,
                "WorkflowId" uuid NOT NULL,
                "InstanceId" uuid NOT NULL,
                "HangfireState" varchar(32) NULL,
                "Stage" varchar(64) NULL,
                "Message" varchar(2000) NULL,
                "ProgressPercent" integer NULL,
                "ErrorMessage" text NULL,
                "FormData" text NULL,
                "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
                "UpdatedAtUtc" timestamptz NOT NULL DEFAULT now()
            );
            """;

        const string addFormDataColumnSql = """
            ALTER TABLE IF EXISTS workflow."ApAgentJobProgress" ADD COLUMN IF NOT EXISTS "FormData" text NULL;
            """;

        const string createIndexSql = """
            CREATE INDEX IF NOT EXISTS "IX_ApAgentJobProgress_InstanceId_Updated"
                ON workflow."ApAgentJobProgress" ("InstanceId", "UpdatedAtUtc" DESC);
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        foreach (var batch in new[] { ensureSchemaSql, dropLegacySql, createTableSql, addFormDataColumnSql, createIndexSql })
        {
            await using var cmd = new NpgsqlCommand(batch, connection) { CommandTimeout = 120 };
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!await TableExistsAsync(cancellationToken))
            throw new InvalidOperationException("Failed to create workflow.\"ApAgentJobProgress\" table.");

        TableEnsured.TryAdd(cacheKey, 0);
    }

    private async Task<bool> TableExistsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(1)
            FROM information_schema.tables
            WHERE table_schema = 'workflow' AND table_name = 'ApAgentJobProgress';
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connectionString = _tenantContext.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Tenant connection string not resolved.");

        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private async Task ExecuteAsync(
        string sql,
        Action<NpgsqlCommand> configure,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, connection);
        configure(cmd);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ApAgentJobProgressRow MapRow(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetGuid(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.GetDateTime(10));
}
