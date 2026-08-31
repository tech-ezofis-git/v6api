using MediatR;

namespace SaaSApp.Workflow.Application.Workflows.Commands.StartWorkflow;

/// <summary>Start a workflow instance (create and begin execution).</summary>
public record StartWorkflowCommand(
    Guid WorkflowId,
    string? Context = null,
    string? EnvType = null,
    StartWorkflowAttachmentPayload? Attachment = null,
    bool TriggerApAgentPythonJob = false,
    IReadOnlyList<string>? Skills = null) : IRequest<StartWorkflowCommandResult>;

/// <summary>Optional file uploaded during start (also supported via multipart on the API).</summary>
public record StartWorkflowAttachmentPayload(
    byte[] Content,
    string FileName,
    string? ContentType);

/// <summary>Result of StartWorkflow including bootstrap payload JSON.</summary>
public record StartWorkflowCommandResult(
    Guid InstanceId,
    int? FirstTransactionId = null,
    int? CurrentTransactionId = null,
    Guid? FormEntryId = null,
    Guid? ApAgentStepInstanceId = null,
    string? FormDataJson = null,
    string? FormDataBlobPath = null,
    IReadOnlyDictionary<string, object?>? StartPayload = null,
    string? ApAgentJobId = null,
    IReadOnlyList<string>? Skills = null,
    /// <summary>Exact body POSTed to AP Agent <c>/chat</c> (null when no AP job was enqueued).</summary>
    object? PythonInput = null);
