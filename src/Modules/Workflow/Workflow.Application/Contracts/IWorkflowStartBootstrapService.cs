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

/// <summary>
/// Stage row to archive on workflow start, or an already-archived item to link.
/// Prefer <see cref="FileId"/> only for normal start/json (stage id from uploadWithOcr / bulkUpload).
/// Set <see cref="ItemId"/> only for an already-archived repository item (raise-from-repository).
/// If <see cref="ItemId"/> is set but that archive row is missing, start falls back to promoting <see cref="FileId"/>.
/// <see cref="FieldId"/> is the form column that receives the archived itemId.
/// </summary>
public sealed record StartWorkflowStagedFileRef(
    Guid RepositoryId,
    Guid FileId,
    string? FormJsonId = null,
    Guid? ItemId = null,
    string? FileName = null,
    string? FieldId = null)
{
    [System.Text.Json.Serialization.JsonPropertyName("jsonId")]
    public string? JsonId { get; init; }

    public string? ResolveFormJsonId() =>
        FirstNonEmpty(FieldId, FormJsonId, JsonId);

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }
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
