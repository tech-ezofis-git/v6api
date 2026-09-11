using System.Net.Http.Headers;
using System.Text.Json;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Infrastructure.Services.ConnectorAdapters;

internal abstract class ConnectorProviderAdapterBase : IConnectorProviderAdapter
{
    protected static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;

    protected ConnectorProviderAdapterBase(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public abstract string ProviderCode { get; }
    public abstract bool SupportsFiles { get; }
    public abstract bool SupportsGmail { get; }
    public virtual bool SupportsQuickBooks => false;
    public virtual bool SupportsSap => false;

    public virtual string BuildAuthorizeUrl(ConnectorProviderConfig config, string state)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId,
            ["redirect_uri"] = config.RedirectUri,
            ["response_type"] = "code",
            ["state"] = state,
            ["access_type"] = "offline",
            ["prompt"] = "consent"
        };
        if (!string.IsNullOrWhiteSpace(config.Scopes))
            query["scope"] = config.Scopes;

        return AppendQuery(config.AuthUrl, query);
    }

    public virtual async Task<ConnectorOAuthTokenResult> ExchangeCodeAsync(
        ConnectorProviderConfig config,
        string code,
        CancellationToken cancellationToken = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = config.RedirectUri,
            ["client_id"] = config.ClientId,
            ["client_secret"] = config.ClientSecret
        };
        return await RequestTokenAsync(config.TokenUrl, form, cancellationToken);
    }

    public virtual async Task<ConnectorOAuthTokenResult> RefreshTokenAsync(
        ConnectorProviderConfig config,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = config.ClientId,
            ["client_secret"] = config.ClientSecret
        };
        var result = await RequestTokenAsync(config.TokenUrl, form, cancellationToken);
        return result with { RefreshToken = result.RefreshToken ?? refreshToken };
    }

    public virtual Task<IReadOnlyList<(string Path, string Name, bool IsFolder, long? SizeBytes, DateTime? ModifiedAtUtc)>> ListFilesAsync(
        string accessToken, string? path, string? extraConfigJson, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{ProviderCode} does not support file listing.");

    public virtual Task UploadFileAsync(
        string accessToken, string path, string fileName, Stream content, string? contentType, string? extraConfigJson, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{ProviderCode} does not support file upload.");

    public virtual Task<(Stream Content, string ContentType, string FileName)> DownloadFileAsync(
        string accessToken, string path, string? extraConfigJson, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{ProviderCode} does not support file download.");

    public virtual Task<(int TotalCount, int UnreadCount)> GetMailSummaryAsync(
        string accessToken, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{ProviderCode} does not support mail summary.");

    public virtual Task<IReadOnlyList<(string Id, string? ThreadId, string? Subject, string? From, string? Snippet, DateTime? ReceivedAtUtc, bool IsUnread, IReadOnlyList<(string Id, string? FileName, string? MimeType, long? SizeBytes)> Attachments)>> ListGmailMessagesAsync(
        string accessToken, int maxResults, string? query, bool unreadOnly, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{ProviderCode} does not support Gmail.");

    public virtual Task<(string Id, string? ThreadId, string? Subject, string? From, string? Snippet, string? BodyText, string? BodyHtml, DateTime? ReceivedAtUtc, bool IsUnread, IReadOnlyList<(string Id, string? FileName, string? MimeType, long? SizeBytes)> Attachments)> GetMailMessageAsync(
        string accessToken, string messageId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{ProviderCode} does not support mail get.");

    public virtual Task MarkMailMessageReadAsync(
        string accessToken, string messageId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{ProviderCode} does not support mark-as-read.");

    public virtual Task<(Stream Content, string ContentType, string FileName)> DownloadGmailAttachmentAsync(
        string accessToken, string messageId, string attachmentId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{ProviderCode} does not support Gmail attachments.");

    public virtual Task<IReadOnlyList<(string Id, string Type, string? DisplayName, string? Email, bool Active, string? RawJson)>> ListQuickBooksMastersAsync(
        string accessToken, string realmId, string masterType, int maxResults, string? extraConfigJson, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{ProviderCode} does not support QuickBooks masters.");

    public virtual Task<IReadOnlyList<(string Id, string Type, string? DocNumber, string? TxnDate, decimal? TotalAmount, string? PartyName, string? Status, string? RawJson)>> ListQuickBooksDocumentsAsync(
        string accessToken, string realmId, string documentType, int maxResults, string? extraConfigJson, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{ProviderCode} does not support QuickBooks documents.");

    public virtual Task<(Stream Content, string ContentType, string FileName)> DownloadQuickBooksDocumentPdfAsync(
        string accessToken, string realmId, string documentType, string documentId, string? extraConfigJson, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{ProviderCode} does not support QuickBooks document download.");

    public virtual Task<string?> GetQuickBooksPurchaseOrderRawByDocNumberAsync(
        string accessToken, string realmId, string poNumber, string? extraConfigJson, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{ProviderCode} does not support QuickBooks purchase order lookup.");

    public virtual Task<string?> GetSapPurchaseOrderRawByNumberAsync(
        string accessToken, string poNumber, string? extraConfigJson, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{ProviderCode} does not support SAP purchase order lookup.");

    public virtual Task<string> ListSapPurchaseOrdersRawAsync(
        string accessToken, int top, string? extraConfigJson, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{ProviderCode} does not support SAP purchase order list.");

    protected HttpClient CreateClient() => _httpClientFactory.CreateClient(nameof(IConnectorProviderAdapter));

    protected async Task<ConnectorOAuthTokenResult> RequestTokenAsync(
        string tokenUrl,
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        using var content = new FormUrlEncodedContent(form);
        using var response = await client.PostAsync(tokenUrl, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token endpoint failed ({(int)response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var access = root.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Token response missing access_token.");
        string? refresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        DateTime? expires = null;
        if (root.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var seconds))
            expires = DateTime.UtcNow.AddSeconds(seconds);

        return new ConnectorOAuthTokenResult(access, refresh, expires, null, null);
    }

    protected static string AppendQuery(string baseUrl, IReadOnlyDictionary<string, string> query)
    {
        var qs = string.Join("&", query.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return baseUrl.Contains('?', StringComparison.Ordinal) ? $"{baseUrl}&{qs}" : $"{baseUrl}?{qs}";
    }

    protected static HttpRequestMessage AuthorizedGet(string url, string accessToken)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return req;
    }

    protected static string? ReadExtra(string? extraConfigJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(extraConfigJson))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(extraConfigJson);
            return doc.RootElement.TryGetProperty(propertyName, out var p) ? p.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
