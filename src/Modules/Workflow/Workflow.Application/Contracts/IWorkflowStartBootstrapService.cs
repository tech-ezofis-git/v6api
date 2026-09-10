using SaaSApp.Workflow.Domain.Entities;

namespace SaaSApp.Workflow.Application.Contracts;

/// <summary>
/// After workflow instance creation: auto-submit start step, form entry, and payload JSON.
/// AP-agent workflows advance to the AP agent step; normal workflows advance by start review.
/// </summary>
public interface IWorkflowStartBootstrapService
{
    Task<WorkflowStartBootstrapResult> RunAsync(
        WorkflowStartBootstrapRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Stage row to promote on workflow start. Optional <see cref="FormJsonId"/> / <see cref="JsonId"/> targets the FILE control column after archive.</summary>
public sealed record StartWorkflowStagedFileRef(Guid RepositoryId, Guid FileId, string? FormJsonId = null)
{
    [System.Text.Json.Serialization.JsonPropertyName("jsonId")]
    public string? JsonId { get; init; }

    public string? ResolveFormJsonId() =>
        !string.IsNullOrWhiteSpace(FormJsonId) ? FormJsonId.Trim()
        : !string.IsNullOrWhiteSpace(JsonId) ? JsonId.Trim()
        : null;
}

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
    Guid? FormEntryId,
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
