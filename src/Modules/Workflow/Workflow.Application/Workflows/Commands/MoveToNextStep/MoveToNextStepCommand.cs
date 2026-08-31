using MediatR;

namespace SaaSApp.Workflow.Application.Workflows.Commands.MoveToNextStep;

/// <summary>Move-next by activityId; optional review completes the step and opens the next.</summary>
public record MoveToNextStepCommand(
    Guid WorkflowInstanceId,
    string ActivityId,
    string? Review = null,
    string? Comments = null,
    Guid? ActivityUserId = null,
    MoveToNextStepApAgentPayload? ApAgent = null,
    string? FormId = null,
    Guid? FormEntryId = null,
    IReadOnlyDictionary<string, string>? FormDataFields = null,
    string? FormLineItemsJson = null,
    string? SubmittedFormDataJson = null
) : IRequest<MoveToNextStepCommandResult>;

public record MoveToNextStepCommandResult(
    bool Success,
    string Message,
    Guid? NextStepInstanceId,
    string? NextStepName,
    int? NextStepOrder,
    bool WorkflowCompleted,
    Guid? LegacyWorkflowInstanceId = null,
    int? LegacyCompletedTransactionId = null,
    int? LegacyNextTransactionId = null,
    Guid? LegacyNextTransactionGuid = null
);
