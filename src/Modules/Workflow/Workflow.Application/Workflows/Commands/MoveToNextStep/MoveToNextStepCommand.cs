using MediatR;
using SaaSApp.Workflow.Application.Contracts;

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
    string? SubmittedFormDataJson = null,
    bool EndWorkflow = false,
    /// <summary>Optional author for the move comment only (e.g. the Pilot/agent user).
    /// Step transition still uses <see cref="ActivityUserId"/>.</summary>
    Guid? CommentCreatedBy = null,
    string? CommentAuthorName = null
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
    Guid? LegacyNextTransactionGuid = null,
    Guid? GeneratedPdfAttachmentId = null,
    string? GeneratedPdfFileName = null,
    /// <summary>Python PDF request input (formData / fileName / metadata / templateJson) for cross-check.</summary>
    WorkflowPdfPythonRequestDto? GeneratedPdfInput = null,
    /// <summary>Hangfire job id when the next step is qualify, quote, or document generate. Frontend polls this like AP Agent.</summary>
    string? ApAgentJobId = null,
    /// <summary>Status URL for <see cref="ApAgentJobId"/> when ApAgent:ApiBaseUrl is configured.</summary>
    string? ApAgentJobStatusUrl = null
);
