namespace SaaSApp.Workflow.Application.Contracts;

public sealed record GmailPushEnableResult(
    string Email,
    string TopicName,
    string HistoryId,
    DateTime? ExpirationUtc);

public interface IGmailPushService
{
    Task<GmailPushEnableResult> EnableAsync(Guid connectorId, CancellationToken cancellationToken = default);

    /// <summary>Pub/Sub push. Starts the linked mailbox ingest for that Gmail address.</summary>
    Task HandlePushAsync(string body, string? token, CancellationToken cancellationToken = default);
}
