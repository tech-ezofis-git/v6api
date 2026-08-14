using SaaSApp.Workflow.Domain.Entities;

namespace SaaSApp.Workflow.Application.Contracts;

/// <summary>
/// After workflow instance creation: auto-submit start step, advance to AP agent, form entry, and payload JSON.
/// </summary>
public interface IWorkflowStartBootstrapService
{
    Task<WorkflowStartBootstrapResult> RunAsync(
        WorkflowStartBootstrapRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record StartWorkflowStagedFileRef(Guid RepositoryId, Guid FileId);

public sealed record WorkflowStartBootstrapRequest(
    Domain.Entities.Workflow Workflow,
    WorkflowInstance Instance,
    Guid UserId,
    int? StartTransactionId,
    string? EnvType,
    Stream? AttachmentStream,
    string? AttachmentFileName,
    string? AttachmentContentType,
    IReadOnlyDictionary<string, string>? FormDataFields = null,
    string? FormLineItemsJson = null,
    IReadOnlyList<StartWorkflowStagedFileRef>? StagedFiles = null);

public sealed record WorkflowStartBootstrapResult(
    int? FirstTransactionId,
    int? CurrentTransactionId,
    int? FormEntryId,
    Guid? ApAgentStepInstanceId,
    string FormDataJson,
    string? FormDataBlobPath,
    IReadOnlyDictionary<string, object?> StartPayload);

public sealed record WorkflowStartAttachmentUploadResult(
    string FilePath,
    Guid RepositoryItemId);

public interface IWorkflowStartAttachmentUploader
{
    Task<WorkflowStartAttachmentUploadResult?> UploadAsync(
        Guid tenantId,
        Guid repositoryId,
        Guid workflowId,
        Guid instanceId,
        int? transactionId,
        Stream fileStream,
        string fileName,
        string? contentType,
        Guid userId,
        CancellationToken cancellationToken = default);
}
