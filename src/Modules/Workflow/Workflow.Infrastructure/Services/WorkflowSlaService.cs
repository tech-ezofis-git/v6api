using System.Text.Json;
using Npgsql;
using Microsoft.Extensions.Logging;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Application.Workflows.Commands.CreateWorkflow;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>Creates SLA rules for workflows.</summary>
public sealed class WorkflowSlaService : IWorkflowSlaService
{
    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUserProvider _currentUserProvider;
    private readonly ILogger<WorkflowSlaService> _logger;

    public WorkflowSlaService(
        ITenantContext tenantContext,
        ICurrentUserProvider currentUserProvider,
        ILogger<WorkflowSlaService> logger)
    {
        _tenantContext = tenantContext;
        _currentUserProvider = currentUserProvider;
        _logger = logger;
    }

    public async Task CreateSlaRulesAsync(
        Guid workflowId,
        List<WorkflowSlaRuleDto>? generalSlaRules,
        List<WorkflowBlockDto> blocks,
        CancellationToken cancellationToken = default)
    {
        var connectionString = _tenantContext.ConnectionString;
        if (string.IsNullOrEmpty(connectionString))
            return;

        var userId = _currentUserProvider.GetUserId();
        if (userId == null)
            return;

        var currentTime = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var workflowIdStr = workflowId.ToString("N").Substring(0, 8);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // Create sla_response table if needed
        var checkTableSql = """
            SELECT table_name FROM information_schema.tables
            WHERE table_schema = 'workflow' AND table_name = @TableName;
            """;

        await using var checkCommand = new NpgsqlCommand(checkTableSql, connection);
        checkCommand.Parameters.AddWithValue("@TableName", $"sla_response_{workflowIdStr}");
        var tableExists = await checkCommand.ExecuteScalarAsync(cancellationToken) != null;

        if (!tableExists)
        {
            var createTableSql = $"""
                CREATE TABLE workflow.sla_response_{workflowIdStr} (
                    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    activity_id varchar(500) NULL,
                    name varchar(500) NULL,
                    duration varchar(500) NULL,
                    duration_type varchar(500) NULL,
                    level integer NULL,
                    settings_json text NULL,
                    master_form_id varchar(500) NULL,
                    field_id varchar(500) NULL,
                    created_at_utc varchar(50) NULL,
                    modified_at_utc varchar(50) NULL,
                    created_by uuid NOT NULL,
                    modified_by uuid NULL,
                    is_deleted boolean NOT NULL DEFAULT false
                );
                """;

            await using var createCommand = new NpgsqlCommand(createTableSql, connection) { CommandTimeout = 120 };
            await createCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        // Insert block-level SLA rules
        if (blocks != null)
        {
            foreach (var block in blocks)
            {
                if (block.Settings.SlaRules != null && block.Settings.SlaRules.Count > 0)
                {
                    foreach (var sla in block.Settings.SlaRules)
                    {
                        if (sla.Id == 0) // New SLA rule
                        {
                            var insertSql = $"""
                                INSERT INTO workflow.sla_response_{workflowIdStr}
                                (activity_id, name, duration, duration_type, level, settings_json, master_form_id, field_id, created_at_utc, created_by, modified_at_utc, modified_by, is_deleted)
                                VALUES (@ActivityId, @Name, @Duration, @DurationType, @Level, @SettingsJson, @MasterFormId, @FieldId, @CreatedAt, @CreatedBy, '', @CreatedBy, false);
                                """;

                            await using var insertCommand = new NpgsqlCommand(insertSql, connection);
                            insertCommand.Parameters.AddWithValue("@ActivityId", block.Id);
                            insertCommand.Parameters.AddWithValue("@Name", sla.Name ?? "");
                            insertCommand.Parameters.AddWithValue("@Duration", sla.Duration?.ToString() ?? "");
                            insertCommand.Parameters.AddWithValue("@DurationType", sla.DurationType ?? "");
                            insertCommand.Parameters.AddWithValue("@Level", sla.Level ?? 0);
                            insertCommand.Parameters.AddWithValue("@SettingsJson", sla.SettingsJson ?? "");
                            insertCommand.Parameters.AddWithValue("@MasterFormId", sla.MasterFormId ?? "");
                            insertCommand.Parameters.AddWithValue("@FieldId", sla.FieldId ?? "");
                            insertCommand.Parameters.AddWithValue("@CreatedAt", currentTime);
                            insertCommand.Parameters.AddWithValue("@CreatedBy", userId.Value);
                            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
                        }
                    }
                }
            }
        }

        // Insert general SLA rules (sla_resolution table)
        if (generalSlaRules != null && generalSlaRules.Count > 0)
        {
            // Create sla_resolution table if needed
            var checkResTableSql = """
                SELECT table_name FROM information_schema.tables
                WHERE table_schema = 'workflow' AND table_name = @TableName;
                """;

            await using var checkResCommand = new NpgsqlCommand(checkResTableSql, connection);
            checkResCommand.Parameters.AddWithValue("@TableName", $"sla_resolution_{workflowIdStr}");
            var resTableExists = await checkResCommand.ExecuteScalarAsync(cancellationToken) != null;

            if (!resTableExists)
            {
                var createResTableSql = $"""
                    CREATE TABLE workflow.sla_resolution_{workflowIdStr} (
                        id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                        name varchar(500) NULL,
                        duration varchar(500) NULL,
                        duration_type varchar(500) NULL,
                        level integer NULL,
                        users varchar(500) NULL,
                        action varchar(500) NULL,
                        master_form_id varchar(500) NULL,
                        field_id varchar(500) NULL,
                        created_at_utc varchar(50) NULL,
                        modified_at_utc varchar(50) NULL,
                        created_by uuid NOT NULL,
                        modified_by uuid NULL,
                        is_deleted boolean NOT NULL DEFAULT false
                    );
                    """;

                await using var createResCommand = new NpgsqlCommand(createResTableSql, connection) { CommandTimeout = 120 };
                await createResCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var sla in generalSlaRules)
            {
                if (sla.Id == 0)
                {
                    var usersJson = sla.Users != null ? JsonSerializer.Serialize(sla.Users) : "0";

                    var insertSql = $"""
                        INSERT INTO workflow.sla_resolution_{workflowIdStr}
                        (name, duration, duration_type, level, users, action, master_form_id, field_id, created_at_utc, created_by, modified_at_utc, modified_by, is_deleted)
                        VALUES (@Name, @Duration, @DurationType, @Level, @Users, @Action, @MasterFormId, @FieldId, @CreatedAt, @CreatedBy, '', @CreatedBy, false);
                        """;

                    await using var insertCommand = new NpgsqlCommand(insertSql, connection);
                    insertCommand.Parameters.AddWithValue("@Name", sla.Name ?? "");
                    insertCommand.Parameters.AddWithValue("@Duration", sla.Duration?.ToString() ?? "");
                    insertCommand.Parameters.AddWithValue("@DurationType", sla.DurationType ?? "");
                    insertCommand.Parameters.AddWithValue("@Level", sla.Level ?? 0);
                    insertCommand.Parameters.AddWithValue("@Users", usersJson);
                    insertCommand.Parameters.AddWithValue("@Action", sla.Action ?? "");
                    insertCommand.Parameters.AddWithValue("@MasterFormId", sla.MasterFormId ?? "");
                    insertCommand.Parameters.AddWithValue("@FieldId", sla.FieldId ?? "");
                    insertCommand.Parameters.AddWithValue("@CreatedAt", currentTime);
                    insertCommand.Parameters.AddWithValue("@CreatedBy", userId.Value);
                    await insertCommand.ExecuteNonQueryAsync(cancellationToken);
                }
            }
        }

        _logger.LogInformation("SLA rules created for workflow {WorkflowId}", workflowId);
    }
}
