namespace SaaSApp.Workflow.Application.Contracts;

public sealed record ConnectorOAuthTokenResult(
    string AccessToken,
    string? RefreshToken,
    DateTime? ExpiresAtUtc,
    string? ExternalAccountEmail,
    string? ExternalAccountId);

public sealed record ConnectorProviderConfig(
    string ProviderCode,
    string ClientId,
    string ClientSecret,
    string AuthUrl,
    string TokenUrl,
    string Scopes,
    string RedirectUri,
    string? ExtraConfigJson);

/// <summary>Pluggable OAuth + ops adapter keyed by ProviderCode.</summary>
public interface IConnectorProviderAdapter
{
    string ProviderCode { get; }

    bool SupportsFiles { get; }

    bool SupportsGmail { get; }

    bool SupportsQuickBooks { get; }

    /// <summary>SAP S/4 purchase-order OData lookup using XSUAA access token.</summary>
    bool SupportsSap { get; }

    string BuildAuthorizeUrl(ConnectorProviderConfig config, string state);

    Task<ConnectorOAuthTokenResult> ExchangeCodeAsync(
        ConnectorProviderConfig config,
        string code,
        CancellationToken cancellationToken = default);

    Task<ConnectorOAuthTokenResult> RefreshTokenAsync(
        ConnectorProviderConfig config,
        string refreshToken,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<(string Path, string Name, bool IsFolder, long? SizeBytes, DateTime? ModifiedAtUtc)>> ListFilesAsync(
        string accessToken,
        string? path,
        string? extraConfigJson,
        CancellationToken cancellationToken = default);

    Task UploadFileAsync(
        string accessToken,
        string path,
        string fileName,
        Stream content,
        string? contentType,
        string? extraConfigJson,
        CancellationToken cancellationToken = default);

    Task<(Stream Content, string ContentType, string FileName)> DownloadFileAsync(
        string accessToken,
        string path,
        string? extraConfigJson,
        CancellationToken cancellationToken = default);

    Task<(int TotalCount, int UnreadCount)> GetMailSummaryAsync(
        string accessToken,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<(string Id, string? ThreadId, string? Subject, string? From, string? Snippet, DateTime? ReceivedAtUtc, bool IsUnread, IReadOnlyList<(string Id, string? FileName, string? MimeType, long? SizeBytes)> Attachments)>> ListGmailMessagesAsync(
        string accessToken,
        int maxResults,
        string? query,
        bool unreadOnly,
        CancellationToken cancellationToken = default);

    Task<(string Id, string? ThreadId, string? Subject, string? From, string? Snippet, string? BodyText, string? BodyHtml, DateTime? ReceivedAtUtc, bool IsUnread, IReadOnlyList<(string Id, string? FileName, string? MimeType, long? SizeBytes)> Attachments)> GetMailMessageAsync(
        string accessToken,
        string messageId,
        CancellationToken cancellationToken = default);

    Task MarkMailMessageReadAsync(
        string accessToken,
        string messageId,
        CancellationToken cancellationToken = default);

    Task<(Stream Content, string ContentType, string FileName)> DownloadGmailAttachmentAsync(
        string accessToken,
        string messageId,
        string attachmentId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<(string Id, string Type, string? DisplayName, string? Email, bool Active, string? RawJson)>> ListQuickBooksMastersAsync(
        string accessToken,
        string realmId,
        string masterType,
        int maxResults,
        string? extraConfigJson,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<(string Id, string Type, string? DocNumber, string? TxnDate, decimal? TotalAmount, string? PartyName, string? Status, string? RawJson)>> ListQuickBooksDocumentsAsync(
        string accessToken,
        string realmId,
        string documentType,
        int maxResults,
        string? extraConfigJson,
        CancellationToken cancellationToken = default);

    Task<(Stream Content, string ContentType, string FileName)> DownloadQuickBooksDocumentPdfAsync(
        string accessToken,
        string realmId,
        string documentType,
        string documentId,
        string? extraConfigJson,
        CancellationToken cancellationToken = default);

    /// <summary>Return raw QBO PurchaseOrder JSON for DocNumber (PO Number), or null if not found.</summary>
    Task<string?> GetQuickBooksPurchaseOrderRawByDocNumberAsync(
        string accessToken,
        string realmId,
        string poNumber,
        string? extraConfigJson,
        CancellationToken cancellationToken = default);

    /// <summary>Return raw SAP OData Purchase Order JSON for PO number, or null if not found.</summary>
    Task<string?> GetSapPurchaseOrderRawByNumberAsync(
        string accessToken,
        string poNumber,
        string? extraConfigJson,
        CancellationToken cancellationToken = default);

    /// <summary>Return raw SAP OData list JSON for a small sample of purchase orders ($top).</summary>
    Task<string> ListSapPurchaseOrdersRawAsync(
        string accessToken,
        int top,
        string? extraConfigJson,
        CancellationToken cancellationToken = default);
}
