namespace SaaSApp.Workflow.Application.Contracts;

public interface IWatchFoxWebhookService
{
    /// <summary>
    /// Verifies the WatchFox signature, ignores anything that is not an invoice,
    /// and queues a ticket start. Returns before the file is downloaded.
    /// </summary>
    Task AcceptAsync(string rawBody, string? signatureHeader, CancellationToken cancellationToken = default);
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
