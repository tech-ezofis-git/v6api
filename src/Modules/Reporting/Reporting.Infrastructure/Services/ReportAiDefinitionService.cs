using Npgsql;
using SaaSApp.MultiTenancy;
using SaaSApp.Reporting.Application.Contracts;
using SaaSApp.Reporting.Infrastructure.Persistence;

namespace SaaSApp.Reporting.Infrastructure.Services;

public sealed class ReportAiDefinitionService : IReportAiDefinitionService
{
    private readonly ITenantConnectionProvider _connectionProvider;

    public ReportAiDefinitionService(ITenantConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider;
    }

    public async Task<ReportAiDefinitionRecord> SaveAsync(
        ReportAiDefinitionRecord record,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var isInsert = record.Id == Guid.Empty;
        if (isInsert)
            record.Id = Guid.NewGuid();
        if (record.CreatedAtUtc == default)
            record.CreatedAtUtc = now;
        record.ModifiedAtUtc = now;

        await using var connection = new NpgsqlConnection(RequireConnection());
        await connection.OpenAsync(cancellationToken);

        if (isInsert)
        {
            const string sql = """
                INSERT INTO reporting."ReportAiDefinitions"
                    ("Id", "TenantId", "OwnerUserId", "SessionId", "Name", "Description", "ReportType",
                     "WorkflowName", "RepositoryName", "ReportPrompt", "ReportSpecJson", "Status",
                     "Runs", "CreatedAtUtc", "ModifiedAtUtc", "CreatedBy", "IsDeleted")
                VALUES
                    (@Id, @TenantId, @OwnerUserId, @SessionId, @Name, @Description, @ReportType,
                     @WorkflowName, @RepositoryName, @ReportPrompt, @ReportSpecJson, @Status,
                     0, @CreatedAtUtc, @ModifiedAtUtc, @OwnerUserId, false);
                """;
            await using var cmd = new NpgsqlCommand(sql, connection);
            BindWrite(cmd, record, isInsert: true);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            return record;
        }

        const string update = """
            UPDATE reporting."ReportAiDefinitions"
            SET "SessionId" = @SessionId,
                "Name" = @Name,
                "Description" = @Description,
                "ReportType" = @ReportType,
                "WorkflowName" = @WorkflowName,
                "RepositoryName" = @RepositoryName,
                "ReportPrompt" = @ReportPrompt,
                "ReportSpecJson" = @ReportSpecJson,
                "ModifiedAtUtc" = @ModifiedAtUtc,
                "ModifiedBy" = @OwnerUserId
            WHERE "Id" = @Id AND "TenantId" = @TenantId AND "IsDeleted" = false;
            """;
        await using (var cmd = new NpgsqlCommand(update, connection))
        {
            BindWrite(cmd, record, isInsert: false);
            var updated = await cmd.ExecuteNonQueryAsync(cancellationToken);
            if (updated == 0)
            {
                const string insertMissing = """
                    INSERT INTO reporting."ReportAiDefinitions"
                        ("Id", "TenantId", "OwnerUserId", "SessionId", "Name", "Description", "ReportType",
                         "WorkflowName", "RepositoryName", "ReportPrompt", "ReportSpecJson", "Status",
                         "Runs", "CreatedAtUtc", "ModifiedAtUtc", "CreatedBy", "IsDeleted")
                    VALUES
                        (@Id, @TenantId, @OwnerUserId, @SessionId, @Name, @Description, @ReportType,
                         @WorkflowName, @RepositoryName, @ReportPrompt, @ReportSpecJson, @Status,
                         0, @CreatedAtUtc, @ModifiedAtUtc, @OwnerUserId, false);
                    """;
                await using var insertCmd = new NpgsqlCommand(insertMissing, connection);
                BindWrite(insertCmd, record, isInsert: true);
                await insertCmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        return record;
    }

    public async Task<ReportAiDefinitionRecord?> GetAsync(
        Guid tenantId,
        Guid reportId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = new NpgsqlConnection(RequireConnection());
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT "Id", "TenantId", "OwnerUserId", "SessionId", "Name", "Description", "ReportType",
                   "WorkflowName", "RepositoryName", "ReportPrompt", "ReportSpecJson", "Status",
                   "Runs", "CreatedAtUtc", "ModifiedAtUtc", "LastRunAtUtc"
            FROM reporting."ReportAiDefinitions"
            WHERE "Id" = @Id AND "TenantId" = @TenantId AND "IsDeleted" = false;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", reportId);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return ReadRow(reader);
    }

    public async Task<IReadOnlyList<ReportAiDefinitionRecord>> ListAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = new NpgsqlConnection(RequireConnection());
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT "Id", "TenantId", "OwnerUserId", "SessionId", "Name", "Description", "ReportType",
                   "WorkflowName", "RepositoryName", "ReportPrompt", "ReportSpecJson", "Status",
                   "Runs", "CreatedAtUtc", "ModifiedAtUtc", "LastRunAtUtc"
            FROM reporting."ReportAiDefinitions"
            WHERE "TenantId" = @TenantId AND "IsDeleted" = false
            ORDER BY COALESCE("ModifiedAtUtc", "CreatedAtUtc") DESC;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        var rows = new List<ReportAiDefinitionRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ReadRow(reader));
        return rows;
    }

    public async Task<ReportAiDefinitionRecord?> UpdateAsync(
        Guid tenantId,
        Guid reportId,
        ReportAiUpdateRequest patch,
        CancellationToken cancellationToken = default)
    {
        var existing = await GetAsync(tenantId, reportId, cancellationToken);
        if (existing is null)
            return null;

        if (!string.IsNullOrWhiteSpace(patch.Name))
            existing.Name = patch.Name.Trim();
        if (patch.Description is not null)
            existing.Description = patch.Description;
        if (!string.IsNullOrWhiteSpace(patch.SessionId))
            existing.SessionId = patch.SessionId.Trim();
        if (patch.ReportPrompt is not null)
            existing.ReportPrompt = patch.ReportPrompt;
        if (patch.ReportSpec.ValueKind is System.Text.Json.JsonValueKind.Object
            or System.Text.Json.JsonValueKind.Array)
            existing.ReportSpecJson = ReportAgentChatMapper.SpecJson(patch.ReportSpec);

        return await SaveAsync(existing, cancellationToken);
    }

    public async Task IncrementRunsAsync(
        Guid tenantId,
        Guid reportId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(RequireConnection());
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            UPDATE reporting."ReportAiDefinitions"
            SET "Runs" = "Runs" + 1,
                "LastRunAtUtc" = @Now,
                "ModifiedAtUtc" = @Now
            WHERE "Id" = @Id AND "TenantId" = @TenantId AND "IsDeleted" = false;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", reportId);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(RequireConnection());
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(ReportSchemaSql.Ensure, connection) { CommandTimeout = 120 };
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private string RequireConnection() =>
        _connectionProvider.ConnectionString
        ?? throw new InvalidOperationException("Tenant connection string not resolved.");

    private static void BindWrite(NpgsqlCommand cmd, ReportAiDefinitionRecord record, bool isInsert)
    {
        cmd.Parameters.AddWithValue("@Id", record.Id);
        cmd.Parameters.AddWithValue("@TenantId", record.TenantId);
        cmd.Parameters.AddWithValue("@OwnerUserId", record.OwnerUserId);
        cmd.Parameters.AddWithValue("@SessionId", record.SessionId ?? "");
        cmd.Parameters.AddWithValue("@Name", string.IsNullOrWhiteSpace(record.Name) ? "Report" : record.Name);
        cmd.Parameters.AddWithValue("@Description", record.Description ?? "");
        cmd.Parameters.AddWithValue("@ReportType", record.ReportType ?? "all_workflows");
        cmd.Parameters.AddWithValue("@WorkflowName", (object?)record.WorkflowName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@RepositoryName", (object?)record.RepositoryName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ReportPrompt", record.ReportPrompt ?? "");
        cmd.Parameters.AddWithValue("@ReportSpecJson", string.IsNullOrWhiteSpace(record.ReportSpecJson) ? "{}" : record.ReportSpecJson);
        cmd.Parameters.AddWithValue("@ModifiedAtUtc", record.ModifiedAtUtc ?? DateTime.UtcNow);
        if (isInsert)
        {
            cmd.Parameters.AddWithValue("@Status", string.IsNullOrWhiteSpace(record.Status) ? "Draft" : record.Status);
            cmd.Parameters.AddWithValue("@CreatedAtUtc", record.CreatedAtUtc);
        }
    }

    private static ReportAiDefinitionRecord ReadRow(NpgsqlDataReader reader) =>
        new()
        {
            Id = reader.GetGuid(0),
            TenantId = reader.GetGuid(1),
            OwnerUserId = reader.GetGuid(2),
            SessionId = reader.IsDBNull(3) ? "" : reader.GetString(3),
            Name = reader.GetString(4),
            Description = reader.IsDBNull(5) ? "" : reader.GetString(5),
            ReportType = reader.GetString(6),
            WorkflowName = reader.IsDBNull(7) ? null : reader.GetString(7),
            RepositoryName = reader.IsDBNull(8) ? null : reader.GetString(8),
            ReportPrompt = reader.IsDBNull(9) ? "" : reader.GetString(9),
            ReportSpecJson = reader.IsDBNull(10) ? "{}" : reader.GetString(10),
            Status = reader.IsDBNull(11) ? "Draft" : reader.GetString(11),
            Runs = reader.IsDBNull(12) ? 0 : reader.GetInt32(12),
            CreatedAtUtc = reader.GetDateTime(13),
            ModifiedAtUtc = reader.IsDBNull(14) ? null : reader.GetDateTime(14),
            LastRunAtUtc = reader.IsDBNull(15) ? null : reader.GetDateTime(15)
        };
}
