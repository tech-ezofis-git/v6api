using MediatR;

namespace SaaSApp.Workflow.Application.Workflows.Queries.GetInstanceAttachments;

/// <summary>Get attachments for a workflow instance from workflow-specific table.</summary>
public record GetInstanceAttachmentsQuery(Guid WorkflowId, Guid InstanceId) : IRequest<GetInstanceAttachmentsQueryResult>;

/// <summary>Attachments from workflow-specific table.</summary>
public record GetInstanceAttachmentsQueryResult(
    Guid WorkflowId,
    Guid InstanceId,
    List<AttachmentItem> Attachments,
    string TableName);

/// <summary>Attachment item.</summary>
public record AttachmentItem(
    Guid Id,
    Guid WorkflowInstanceId,
    Guid WorkflowId,
    string FileName,
    string FilePath,
    long? FileSize,
    string? ContentType,
    DateTime CreatedAtUtc,
    Guid CreatedBy,
    string? UploadedBy,
    Guid? ModifiedBy,
    DateTime? ModifiedAtUtc,
    Guid? RepositoryId,
    Guid? ItemId,
    /// <summary>
    /// True for the source .eml (email tickets) and for generated quote PDFs
    /// (<c>*_quote_N.pdf</c>) so the UI can show those first and treat the rest as supporting files.
    /// </summary>
    bool IsOriginal = false
);
