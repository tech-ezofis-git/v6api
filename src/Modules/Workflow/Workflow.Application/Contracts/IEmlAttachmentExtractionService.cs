namespace SaaSApp.Workflow.Application.Contracts;

/// <summary>
/// Parses a .eml already archived on a workflow instance and archives each embedded attachment
/// as its own repository item / workflow attachment. Additive only -- never touches how the
/// ticket was started or how the .eml itself got archived.
/// </summary>
public interface IEmlAttachmentExtractionService
{
    /// <summary>
    /// Extracts embedded attachments from a .eml already attached to <paramref name="instanceId"/>.
    /// When <paramref name="sourceItemId"/> is omitted, the most recently archived .eml-shaped
    /// attachment on the instance is used.
    /// </summary>
    Task<EmlAttachmentExtractionResult> ExtractAsync(
        Guid tenantId,
        Guid workflowId,
        Guid instanceId,
        Guid userId,
        Guid? sourceRepositoryId,
        Guid? sourceItemId,
        CancellationToken cancellationToken = default);
}

public sealed record EmlAttachmentExtractionResult(
    Guid SourceItemId,
    string SourceFileName,
    IReadOnlyList<ExtractedEmlAttachment> Extracted,
    IReadOnlyList<SkippedEmlAttachment> Skipped);

public sealed record ExtractedEmlAttachment(
    Guid AttachmentId,
    Guid ItemId,
    string FileName,
    string? ContentType,
    long FileSize,
    string ContentHash);

public sealed record SkippedEmlAttachment(
    string FileName,
    string Reason);
