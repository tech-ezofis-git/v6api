using System.Text.Json;
using Npgsql;
using Microsoft.Extensions.Logging;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Application.Workflows.Commands.CreateWorkflow;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>
/// Creates ML prediction models for workflows.
///
/// PHASE 4 FINDING: workflow."MlModelPrediction" is referenced ONLY by this one file and is
/// created NOWHERE -- not in any SQL Server or Postgres script, not in any EF migration. The
/// method's own `// TODO: Map predictionFields...` comment (kept below) already flags the
/// predictionFields->form-field mapping as unimplemented, so this looks like pre-existing
/// dead/incomplete code on SQL Server too (INSERTing into a table that was never created
/// would already fail there with "Invalid object name"). Ported the syntax faithfully rather
/// than inventing a table shape from the single INSERT's column list -- that would be
/// speculative for a feature that was never finished. Preserves the same "relation does not
/// exist" failure mode as the SQL Server original's "Invalid object name" if this code path
/// is ever actually exercised.
/// </summary>
public sealed class WorkflowMlService : IWorkflowMlService
{
    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUserProvider _currentUserProvider;
    private readonly ILogger<WorkflowMlService> _logger;

    public WorkflowMlService(
        ITenantContext tenantContext,
        ICurrentUserProvider currentUserProvider,
        ILogger<WorkflowMlService> logger)
    {
        _tenantContext = tenantContext;
        _currentUserProvider = currentUserProvider;
        _logger = logger;
    }

    public async Task CreateMlPredictionsAsync(
        Guid workflowId,
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

        // Filter blocks that have AI prediction settings
        var mlBlocks = blocks?
            .Where(b => b.Type != "START" && b.Type != "END")
            .Where(b => b.Settings.AiPrediction != null &&
                       b.Settings.AiPrediction.PredictionFields != null &&
                       b.Settings.AiPrediction.PredictionFields.Length > 0)
            .ToList();

        if (mlBlocks == null || mlBlocks.Count == 0)
            return;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        foreach (var block in mlBlocks)
        {
            // Build prediction columns
            var baseColumns = new[] { "levelId", "activityId", "activityUserId", "review" };
            var predictionFields = block.Settings?.AiPrediction?.PredictionFields ?? Array.Empty<string>();

            // TODO: Map predictionFields (JSON IDs) to actual form field names
            // This requires querying wFormControls table
            var predictionColumns = baseColumns.Concat(predictionFields ?? Array.Empty<string>()).ToArray();
            var conditionColumnsJson = JsonSerializer.Serialize(predictionColumns);

            const string sql = """
                INSERT INTO workflow."MlModelPrediction"
                ("WorkflowId", "ActivityId", "HasModel", "Remarks", "TrainedCount", "JsonTriggerCount", "ConditionColumns", "CreatedAtUtc", "CreatedBy")
                VALUES (@WorkflowId, @ActivityId, false, '', 0, 50, @ConditionColumns, @CreatedAt, @CreatedBy)
                """;

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("@WorkflowId", workflowId);
            command.Parameters.AddWithValue("@ActivityId", block.Id);
            command.Parameters.AddWithValue("@ConditionColumns", conditionColumnsJson);
            command.Parameters.AddWithValue("@CreatedAt", currentTime);
            command.Parameters.AddWithValue("@CreatedBy", userId.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        _logger.LogInformation("ML predictions created for workflow {WorkflowId}", workflowId);
    }
}
