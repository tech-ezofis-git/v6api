using System.Text.Json;
using Npgsql;
using Microsoft.Extensions.Logging;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Application.Workflows.Commands.CreateWorkflow;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>Sets up auto-initiation for workflows (Master Form, Email, etc.).</summary>
public sealed class WorkflowInitiationService : IWorkflowInitiationService
{
    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUserProvider _currentUserProvider;
    private readonly ILogger<WorkflowInitiationService> _logger;

    public WorkflowInitiationService(
        ITenantContext tenantContext,
        ICurrentUserProvider currentUserProvider,
        ILogger<WorkflowInitiationService> logger)
    {
        _tenantContext = tenantContext;
        _currentUserProvider = currentUserProvider;
        _logger = logger;
    }

    public async Task SetupAutoInitiationAsync(
        Guid workflowId,
        List<WorkflowBlockDto> blocks,
        WorkflowInitiateUsingDto initiateUsing,
        List<WorkflowConnectionDto>? rules,
        CancellationToken cancellationToken = default)
    {
        var connectionString = _tenantContext.ConnectionString;
        if (string.IsNullOrEmpty(connectionString))
            return;

        var userId = _currentUserProvider.GetUserId();
        if (userId == null)
            return;

        var tenantId = _tenantContext.TenantId;
        if (tenantId == null)
            return;

        var startBlock = blocks?.FirstOrDefault(b => b.Type == "START");
        if (startBlock == null || startBlock.Settings == null)
            return;

        var currentTime = DateTime.UtcNow;

        await EnsureWorkflowInitiateInfoTableAsync(connectionString, cancellationToken);

        // Determine review action from rules
        string? review = null;
        if (rules != null && startBlock != null)
        {
            var rule = rules.FirstOrDefault(r => r.FromBlockId == startBlock.Id);
            review = rule?.ProceedAction;
        }

        // Handle Master Form initiation
        if (startBlock.Settings?.InitiateBy != null &&
            (startBlock.Settings.InitiateBy.Contains("MASTER_FORM") || startBlock.Settings.InitiateBy.Contains("DOCUMENT")))
        {
            var initiateInfo = new
            {
                formId = initiateUsing.FormId?.LegacyInt,
                formGuid = initiateUsing.FormId?.Guid,
                repositoryId = initiateUsing.RepositoryId?.LegacyInt,
                repositoryGuid = initiateUsing.RepositoryId?.Guid,
                masterFormId = startBlock.Settings.MasterFormId,
                review = review,
                activityId = startBlock.Id,
                stageType = startBlock.Type,
                stage = startBlock.Settings.Label,
                condition = BuildConditionString(startBlock.Settings.Conditions)
            };

            var inputJson = JsonSerializer.Serialize(initiateInfo);

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            const string sql = """
                INSERT INTO workflow."WorkflowInitiateInfo"
                ("TenantId", "WorkflowId", "InputType", "InputJson", "Status", "Remarks", "CreatedBy", "CreatedAtUtc", "RepositoryId")
                VALUES (@TenantId, @WorkflowId, @InputType, @InputJson, 0, '', @CreatedBy, @CreatedAt, @RepositoryId)
                """;

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("@TenantId", tenantId.Value);
            command.Parameters.AddWithValue("@WorkflowId", workflowId);
            command.Parameters.AddWithValue("@InputType", string.Join(",", startBlock.Settings.InitiateBy ?? Array.Empty<string>()));
            command.Parameters.AddWithValue("@InputJson", inputJson);
            command.Parameters.AddWithValue("@CreatedBy", userId.Value);
            command.Parameters.AddWithValue("@CreatedAt", currentTime);
            command.Parameters.AddWithValue("@RepositoryId", initiateUsing.RepositoryId?.LegacyInt ?? (object)DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);

            _logger.LogInformation("Auto-initiation setup for Master Form, workflow {WorkflowId}", workflowId);
        }

        // Handle Email initiation
        if (startBlock.Settings.InitiateBy != null && startBlock.Settings.InitiateBy.Contains("EMAIL"))
        {
            var mailInitiate = startBlock.Settings.MailInitiate;
            if (mailInitiate != null)
            {
                mailInitiate = mailInitiate with { Review = review };
                var inputJson = JsonSerializer.Serialize(mailInitiate);

                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);

                const string sql = """
                    INSERT INTO workflow."WorkflowInitiateInfo"
                    ("TenantId", "WorkflowId", "InputType", "InputJson", "Status", "Remarks", "CreatedBy", "CreatedAtUtc", "RepositoryId")
                    VALUES (@TenantId, @WorkflowId, @InputType, @InputJson, 0, '', @CreatedBy, @CreatedAt, @RepositoryId)
                    """;

                await using var command = new NpgsqlCommand(sql, connection);
                command.Parameters.AddWithValue("@TenantId", tenantId.Value);
                command.Parameters.AddWithValue("@WorkflowId", workflowId);
                command.Parameters.AddWithValue("@InputType", string.Join(",", startBlock.Settings.InitiateBy));
                command.Parameters.AddWithValue("@InputJson", inputJson);
                command.Parameters.AddWithValue("@CreatedBy", userId.Value);
                command.Parameters.AddWithValue("@CreatedAt", currentTime);
                command.Parameters.AddWithValue("@RepositoryId", initiateUsing.RepositoryId?.LegacyInt ?? (object)DBNull.Value);
                await command.ExecuteNonQueryAsync(cancellationToken);

                _logger.LogInformation("Auto-initiation setup for Email, workflow {WorkflowId}", workflowId);
            }
        }
    }

    private static async Task EnsureWorkflowInitiateInfoTableAsync(string connectionString, CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE SCHEMA IF NOT EXISTS workflow;
            CREATE TABLE IF NOT EXISTS workflow."WorkflowInitiateInfo" (
                "Id" bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                "TenantId" uuid NOT NULL,
                "WorkflowId" uuid NOT NULL,
                "InputType" varchar(256) NOT NULL,
                "InputJson" text NULL,
                "Status" integer NOT NULL DEFAULT 0,
                "Remarks" varchar(2000) NOT NULL DEFAULT '',
                "CreatedBy" uuid NOT NULL,
                "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
                "RepositoryId" integer NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_WorkflowInitiateInfo_WorkflowId" ON workflow."WorkflowInitiateInfo" ("WorkflowId");
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private string? BuildConditionString(WorkflowConditionsDto? conditions)
    {
        if (conditions == null || conditions.Condition == null || conditions.Condition.Count == 0)
            return null;

        // TODO: Build SQL condition string from conditions
        // This should match the logic from source: getconditionNew()
        return null;
    }
}
