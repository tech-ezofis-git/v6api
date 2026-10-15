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

    /// <summary>
    /// One UI call. Returns the Microsoft sign-in URL. After sign-in this API creates the
    /// enquiry watch and sends the browser to <paramref name="redirectUrl"/> with
    /// <c>connectionId</c> and <c>watchId</c>.
    /// </summary>
    Task<WatchFoxConnectLinkResult> CreateMicrosoftConnectLinkAsync(
        Guid tenantId,
        Guid workflowId,
        string? redirectUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Browser return from WatchFox. Creates the enquiry watch, then returns the UI address to open.
    /// </summary>
    Task<string> CompleteMicrosoftConnectAsync(
        string? status,
        string? connectionId,
        string? state,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// After Microsoft sign-in, start an Enquiries watch for that mailbox.
    /// Uses <c>WatchFox:ApiKey</c> and <c>WatchFox:WebhookSecret</c> from the host.
    /// </summary>
    Task<WatchFoxEnquiryWatchResult> CreateEnquiryWatchAsync(
        string connectionId,
        Guid tenantId,
        Guid workflowId,
        CancellationToken cancellationToken = default);
}

public sealed record WatchFoxConnectLinkResult(string Url, int ExpiresIn);

public sealed record WatchFoxEnquiryWatchResult(
    string WatchId,
    string ConnectionId,
    Guid TenantId,
    Guid WorkflowId);

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
