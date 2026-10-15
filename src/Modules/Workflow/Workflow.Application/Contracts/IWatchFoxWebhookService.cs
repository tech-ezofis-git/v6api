namespace SaaSApp.Workflow.Application.Contracts;

public interface IWatchFoxWebhookService
{
    /// <summary>
    /// Verifies the WatchFox signature. An invoice match queues an MJB ticket.
    /// An Enquiries match queues the FTL workflow start with the email .eml.
    /// Returns before the file is downloaded.
    /// </summary>
    Task AcceptAsync(string rawBody, string? signatureHeader, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enquiries webhook when WatchFox posts the .eml as multipart form-data.
    /// Queues the same FTL workflow start as <see cref="AcceptAsync"/>.
    /// </summary>
    Task AcceptEnquiryFileAsync(
        byte[] eml,
        string fileName,
        string? contentType,
        string? signatureHeader,
        string? signedPayload,
        CancellationToken cancellationToken = default);
}

/// <summary>One invoice attachment from a WatchFox <c>email.matched</c> event.</summary>
public sealed record WatchFoxInvoiceJobArgs(
    string EventKey,
    Guid TenantId,
    Guid WorkflowId,
    string FileName,
    string? ContentType,
    string DownloadUrl,
    string? FromEmail,
    string? Subject,
    long? ReceivedUnix,
    string? MessageId);

/// <summary>One Enquiries .eml from a WatchFox <c>email.matched</c> event.</summary>
public sealed record WatchFoxEnquiryJobArgs(
    string EventKey,
    Guid TenantId,
    Guid WorkflowId,
    string FileName,
    string? ContentType,
    string? DownloadUrl,
    byte[]? Content,
    string? FromEmail,
    string? Subject,
    string? MessageId);
