namespace SaaSApp.Workflow.Application.Workflows.Commands.MoveToNextStep;

/// <summary>AP agent engine payload for move-next (OCR / validation).</summary>
public sealed record MoveToNextStepApAgentPayload(
    string? TransactionId,
    /// <summary>Workflow instance id (engine sends instanceId; stored in agentDataValidation.ProcessId).</summary>
    Guid? InstanceId,
    string? AiAgentResponseJson,
    string? AiAgentHtml,
    Guid? RepositoryItemId,
    Guid? RepositoryId,
    string? FormId,
    Guid? FormEntryId);
