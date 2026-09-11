namespace SaaSApp.Workflow.Application.Contracts;

/// <summary>
/// Archive workflow attachment: blob + repository item + WorkflowAttachments + processAddon.
/// </summary>
public interface IWorkflowAttachmentArchiveService
{
    Task<WorkflowAttachmentArchiveResult> UploadAsync(
        Guid tenantId,
        Guid workflowId,
        Guid instanceId,
        Guid repositoryId,
        Stream fileStream,
        string fileName,
        string? contentType,
        long? fileSize,
        string? metadataJson,
        int? transactionId,
        Guid userId,
        CancellationToken cancellationToken = default,
        bool allowIncompleteFolderMetadata = false);

    /// <summary>Promote a pre-ticket staged fileId into archive + WorkflowAttachments + processAddon.</summary>
    Task<WorkflowAttachmentArchiveResult?> PromoteFromStageAsync(
        Guid tenantId,
        Guid workflowId,
        Guid instanceId,
        Guid repositoryId,
        Guid stageId,
        int? transactionId,
        Guid userId,
        CancellationToken cancellationToken = default,
        bool allowIncompleteFolderMetadata = false,
        string? formJsonId = null);

    /// <summary>Link an already-archived repository item to the ticket (WorkflowAttachments + processAddon). No re-upload.</summary>
    Task<WorkflowAttachmentArchiveResult?> AttachExistingArchiveItemAsync(
        Guid tenantId,
        Guid workflowId,
        Guid instanceId,
        Guid repositoryId,
        Guid itemId,
        string? fileName,
        int? transactionId,
        Guid userId,
        CancellationToken cancellationToken = default);
}

public sealed record WorkflowAttachmentArchiveResult(
    Guid AttachmentId,
    Guid ItemId,
    Guid RepositoryId,
    Guid WorkflowId,
    Guid WorkflowInstanceId,
    int ProcessAddonId,
    string FileName,
    string FilePath,
    string StorageProviderCode,
    int FileVersion,
    Guid? LeafFolderId,
    IReadOnlyList<string> FolderNames);
