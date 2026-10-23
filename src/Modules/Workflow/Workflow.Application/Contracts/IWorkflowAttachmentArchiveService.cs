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

    /// <summary>
    /// Promote a pre-ticket staged fileId into archive + WorkflowAttachments + processAddon.
    /// <paramref name="namingFieldFallback"/> (typically the ticket's reference number) fills the
    /// repository's naming field only when it has no value from the stage row/OCR and isn't the
    /// literal uploaded-file-name field -- see RepositoryNamingFieldMetadataInjector.
    /// </summary>
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
        string? formJsonId = null,
        string? namingFieldFallback = null);

    /// <summary>
    /// Link an already-archived repository item to the ticket (WorkflowAttachments + processAddon). No re-upload.
    /// When <paramref name="namingFieldFallback"/> is set (typically the ticket reference number), fills the
    /// repository naming field if it is still empty or only holds the upload file name.
    /// </summary>
    Task<WorkflowAttachmentArchiveResult?> AttachExistingArchiveItemAsync(
        Guid tenantId,
        Guid workflowId,
        Guid instanceId,
        Guid repositoryId,
        Guid itemId,
        string? fileName,
        int? transactionId,
        Guid userId,
        CancellationToken cancellationToken = default,
        string? namingFieldFallback = null);
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
