using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Infrastructure.Services.ConnectorAdapters;

/// <summary>Office 365 Outlook mail via Microsoft Graph (same app as OneDrive/Teams, mail scopes).</summary>
internal sealed class OutlookConnectorAdapter : ConnectorProviderAdapterBase
{
    public OutlookConnectorAdapter(IHttpClientFactory httpClientFactory) : base(httpClientFactory) { }

    public override string ProviderCode => "OUTLOOK";
    public override bool SupportsFiles => false;
    public override bool SupportsGmail => true;

    public override string BuildAuthorizeUrl(ConnectorProviderConfig config, string state)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId,
            ["redirect_uri"] = config.RedirectUri,
            ["response_type"] = "code",
            ["response_mode"] = "query",
            ["state"] = state,
            ["scope"] = string.IsNullOrWhiteSpace(config.Scopes)
                ? "offline_access openid profile email Mail.ReadWrite User.Read"
                : config.Scopes
        };
        return AppendQuery(config.AuthUrl, query);
    }

    public override async Task<ConnectorOAuthTokenResult> ExchangeCodeAsync(
        ConnectorProviderConfig config, string code, CancellationToken cancellationToken = default)
    {
        var token = await base.ExchangeCodeAsync(config, code, cancellationToken);
        var email = await TryGetGraphEmailAsync(token.AccessToken, cancellationToken);
        return token with { ExternalAccountEmail = email };
    }

    public override async Task<(int TotalCount, int UnreadCount)> GetMailSummaryAsync(
        string accessToken, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        using var req = AuthorizedGet(
            "https://graph.microsoft.com/v1.0/me/mailFolders/inbox?$select=totalItemCount,unreadItemCount",
            accessToken);
        using var res = await client.SendAsync(req, cancellationToken);
        var body = await res.Content.ReadAsStringAsync(cancellationToken);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Outlook summary failed ({(int)res.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var total = doc.RootElement.TryGetProperty("totalItemCount", out var t) ? t.GetInt32() : 0;
        var unread = doc.RootElement.TryGetProperty("unreadItemCount", out var u) ? u.GetInt32() : 0;
        return (total, unread);
    }

    public override async Task<IReadOnlyList<(string Id, string? ThreadId, string? Subject, string? From, string? Snippet, DateTime? ReceivedAtUtc, bool IsUnread, IReadOnlyList<(string Id, string? FileName, string? MimeType, long? SizeBytes)> Attachments)>> ListGmailMessagesAsync(
        string accessToken, int maxResults, string? query, bool unreadOnly, CancellationToken cancellationToken = default)
    {
        maxResults = Math.Clamp(maxResults, 1, 50);
        // Graph often rejects $filter+$search and $search+$orderby — pick a compatible query shape.
        var url =
            $"https://graph.microsoft.com/v1.0/me/mailFolders/inbox/messages?$top={maxResults}" +
            "&$select=id,conversationId,subject,from,bodyPreview,receivedDateTime,hasAttachments,isRead";

        if (!string.IsNullOrWhiteSpace(query))
        {
            var search = unreadOnly
                ? $"isRead:false {query.Trim()}"
                : query.Trim();
            url += $"&$search=\"{search.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
        }
        else
        {
            url += "&$orderby=receivedDateTime desc";
            if (unreadOnly)
                url += "&$filter=isRead eq false";
        }

        using var client = CreateClient();
        using var listReq = AuthorizedGet(url, accessToken);
        AddOutlookImmutableIdPrefer(listReq);
        using var listRes = await client.SendAsync(listReq, cancellationToken);
        var listBody = await listRes.Content.ReadAsStringAsync(cancellationToken);
        if (!listRes.IsSuccessStatusCode)
            throw new InvalidOperationException($"Outlook list failed ({(int)listRes.StatusCode}): {listBody}");

        var results = new List<(string, string?, string?, string?, string?, DateTime?, bool, IReadOnlyList<(string, string?, string?, long?)>)>();
        using var listDoc = JsonDocument.Parse(listBody);
        if (!listDoc.RootElement.TryGetProperty("value", out var messages))
            return results;

        foreach (var m in messages.EnumerateArray())
        {
            var id = m.GetProperty("id").GetString();
            if (string.IsNullOrEmpty(id))
                continue;

            var isUnread = !(m.TryGetProperty("isRead", out var ir) && ir.ValueKind == JsonValueKind.True);
            // Client-side guard when $search may not honor unread filter.
            if (unreadOnly && !isUnread)
                continue;

            string? from = null;
            if (m.TryGetProperty("from", out var fromEl) &&
                fromEl.TryGetProperty("emailAddress", out var ea) &&
                ea.TryGetProperty("address", out var addr))
                from = addr.GetString();

            DateTime? received = m.TryGetProperty("receivedDateTime", out var rd) && DateTime.TryParse(rd.GetString(), out var dt)
                ? dt.ToUniversalTime()
                : null;

            var attachments = new List<(string, string?, string?, long?)>();
            var hasAttachments = m.TryGetProperty("hasAttachments", out var ha) && ha.GetBoolean();
            if (hasAttachments)
                await LoadAttachmentsAsync(client, accessToken, id, attachments, cancellationToken);

            results.Add((
                id,
                m.TryGetProperty("conversationId", out var cid) ? cid.GetString() : null,
                m.TryGetProperty("subject", out var sub) ? sub.GetString() : null,
                from,
                m.TryGetProperty("bodyPreview", out var bp) ? bp.GetString() : null,
                received,
                isUnread,
                attachments));
        }

        return results;
    }

    public override async Task<(string Id, string? ThreadId, string? Subject, string? From, string? Snippet, string? BodyText, string? BodyHtml, DateTime? ReceivedAtUtc, bool IsUnread, IReadOnlyList<(string Id, string? FileName, string? MimeType, long? SizeBytes)> Attachments)> GetMailMessageAsync(
        string accessToken, string messageId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            throw new ArgumentException("messageId is required.");

        using var client = CreateClient();
        using var req = AuthorizedGet(
            $"https://graph.microsoft.com/v1.0/me/messages/{Uri.EscapeDataString(messageId)}" +
            "?$select=id,conversationId,subject,from,bodyPreview,body,receivedDateTime,hasAttachments,isRead",
            accessToken);
        AddOutlookImmutableIdPrefer(req);
        using var res = await client.SendAsync(req, cancellationToken);
        var body = await res.Content.ReadAsStringAsync(cancellationToken);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Outlook get message failed ({(int)res.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var m = doc.RootElement;
        string? from = null;
        if (m.TryGetProperty("from", out var fromEl) &&
            fromEl.TryGetProperty("emailAddress", out var ea) &&
            ea.TryGetProperty("address", out var addr))
            from = addr.GetString();

        DateTime? received = m.TryGetProperty("receivedDateTime", out var rd) && DateTime.TryParse(rd.GetString(), out var dt)
            ? dt.ToUniversalTime()
            : null;

        string? bodyText = null;
        string? bodyHtml = null;
        if (m.TryGetProperty("body", out var bodyEl))
        {
            var content = bodyEl.TryGetProperty("content", out var c) ? c.GetString() : null;
            var contentType = bodyEl.TryGetProperty("contentType", out var ct) ? ct.GetString() : null;
            if (string.Equals(contentType, "html", StringComparison.OrdinalIgnoreCase))
                bodyHtml = content;
            else
                bodyText = content;
        }

        var attachments = new List<(string, string?, string?, long?)>();
        if (m.TryGetProperty("hasAttachments", out var ha) && ha.GetBoolean())
            await LoadAttachmentsAsync(client, accessToken, messageId, attachments, cancellationToken);

        return (
            m.GetProperty("id").GetString() ?? messageId,
            m.TryGetProperty("conversationId", out var cid) ? cid.GetString() : null,
            m.TryGetProperty("subject", out var sub) ? sub.GetString() : null,
            from,
            m.TryGetProperty("bodyPreview", out var bp) ? bp.GetString() : null,
            bodyText,
            bodyHtml,
            received,
            !(m.TryGetProperty("isRead", out var ir) && ir.ValueKind == JsonValueKind.True),
            attachments);
    }

    public override async Task MarkMailMessageReadAsync(
        string accessToken, string messageId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            throw new ArgumentException("messageId is required.");

        using var client = CreateClient();
        using var req = new HttpRequestMessage(
            HttpMethod.Patch,
            $"https://graph.microsoft.com/v1.0/me/messages/{Uri.EscapeDataString(messageId)}")
        {
            Content = new StringContent("""{"isRead":true}""", Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        AddOutlookImmutableIdPrefer(req);
        using var res = await client.SendAsync(req, cancellationToken);
        var body = await res.Content.ReadAsStringAsync(cancellationToken);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Outlook mark-as-read failed ({(int)res.StatusCode}): {body}");
    }

    public override async Task<(Stream Content, string ContentType, string FileName)> DownloadGmailAttachmentAsync(
        string accessToken, string messageId, string attachmentId, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        using var metaReq = AuthorizedGet(
            $"https://graph.microsoft.com/v1.0/me/messages/{Uri.EscapeDataString(messageId)}/attachments/{Uri.EscapeDataString(attachmentId)}",
            accessToken);
        AddOutlookImmutableIdPrefer(metaReq);
        using var metaRes = await client.SendAsync(metaReq, cancellationToken);
        var metaBody = await metaRes.Content.ReadAsStringAsync(cancellationToken);
        if (!metaRes.IsSuccessStatusCode)
            throw new InvalidOperationException($"Outlook attachment meta failed ({(int)metaRes.StatusCode}): {metaBody}");

        using var doc = JsonDocument.Parse(metaBody);
        var root = doc.RootElement;
        var fileName = root.TryGetProperty("name", out var n) ? n.GetString() ?? attachmentId : attachmentId;
        var contentType = root.TryGetProperty("contentType", out var ct) ? ct.GetString() ?? "application/octet-stream" : "application/octet-stream";

        if (root.TryGetProperty("contentBytes", out var cb) && !string.IsNullOrWhiteSpace(cb.GetString()))
        {
            var bytes = Convert.FromBase64String(cb.GetString()!);
            return (new MemoryStream(bytes), contentType, fileName);
        }

        using var binReq = AuthorizedGet(
            $"https://graph.microsoft.com/v1.0/me/messages/{Uri.EscapeDataString(messageId)}/attachments/{Uri.EscapeDataString(attachmentId)}/$value",
            accessToken);
        using var binRes = await client.SendAsync(binReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!binRes.IsSuccessStatusCode)
        {
            var err = await binRes.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Outlook attachment download failed ({(int)binRes.StatusCode}): {err}");
        }

        var ms = new MemoryStream();
        await binRes.Content.CopyToAsync(ms, cancellationToken);
        ms.Position = 0;
        return (ms, contentType, fileName);
    }

    private static async Task LoadAttachmentsAsync(
        HttpClient client,
        string accessToken,
        string messageId,
        List<(string, string?, string?, long?)> attachments,
        CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://graph.microsoft.com/v1.0/me/messages/{Uri.EscapeDataString(messageId)}/attachments?$select=id,name,contentType,size,isInline");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        AddOutlookImmutableIdPrefer(req);
        using var res = await client.SendAsync(req, cancellationToken);
        if (!res.IsSuccessStatusCode)
            return;

        var body = await res.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("value", out var values))
            return;

        foreach (var a in values.EnumerateArray())
        {
            var id = a.GetProperty("id").GetString();
            if (string.IsNullOrEmpty(id))
                continue;

            var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            var mime = a.TryGetProperty("contentType", out var ct) ? ct.GetString() : null;
            long? size = a.TryGetProperty("size", out var s) ? s.GetInt64() : null;

            // Skip Outlook inline signature / embedded images, but keep real PNG/JPEG/PDF scans.
            if (a.TryGetProperty("isInline", out var inline)
                && inline.ValueKind == JsonValueKind.True
                && !IsDocumentLikeOutlookAttachment(name, mime, size))
            {
                continue;
            }

            attachments.Add((id, name, mime, size));
        }
    }

    private static bool IsDocumentLikeOutlookAttachment(string? fileName, string? mimeType, long? sizeBytes)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
            return false;

        var mime = mimeType.Trim().ToLowerInvariant();
        var isDocMime = mime.Contains("pdf", StringComparison.Ordinal)
            || mime.Contains("tiff", StringComparison.Ordinal)
            || mime.Contains("image/png", StringComparison.Ordinal)
            || mime.Contains("image/jpeg", StringComparison.Ordinal)
            || mime.Contains("image/jpg", StringComparison.Ordinal);

        if (!isDocMime)
            return false;

        if (!string.IsNullOrWhiteSpace(fileName))
            return true;

        return sizeBytes is null or >= 50_000;
    }

    private static void AddOutlookImmutableIdPrefer(HttpRequestMessage req) =>
        req.Headers.TryAddWithoutValidation("Prefer", "IdType=\"ImmutableId\"");

    private static async Task<string?> TryGetGraphEmailAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(req, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("mail", out var mail) && !string.IsNullOrWhiteSpace(mail.GetString()))
            return mail.GetString();
        return doc.RootElement.TryGetProperty("userPrincipalName", out var upn) ? upn.GetString() : null;
    }
}

/// <summary>QuickBooks Online — OAuth + master (Customer/Vendor/Item) + documents (Invoice/Bill/PurchaseOrder).</summary>
internal sealed class QuickBooksConnectorAdapter : ConnectorProviderAdapterBase
{
    private static readonly HashSet<string> MasterTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Customer", "Vendor", "Item"
    };

    private static readonly HashSet<string> DocumentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Invoice", "Bill", "PurchaseOrder", "Estimate", "SalesReceipt"
    };

    public QuickBooksConnectorAdapter(IHttpClientFactory httpClientFactory) : base(httpClientFactory) { }

    public override string ProviderCode => "QUICKBOOKS";
    public override bool SupportsFiles => false;
    public override bool SupportsGmail => false;
    public override bool SupportsQuickBooks => true;

    public override string BuildAuthorizeUrl(ConnectorProviderConfig config, string state)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId,
            ["redirect_uri"] = config.RedirectUri,
            ["response_type"] = "code",
            ["scope"] = string.IsNullOrWhiteSpace(config.Scopes)
                ? "com.intuit.quickbooks.accounting openid profile email"
                : config.Scopes,
            ["state"] = state
        };
        return AppendQuery(config.AuthUrl, query);
    }

    public override async Task<ConnectorOAuthTokenResult> ExchangeCodeAsync(
        ConnectorProviderConfig config, string code, CancellationToken cancellationToken = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = config.RedirectUri
        };
        return await RequestIntuitTokenAsync(config, form, cancellationToken);
    }

    public override async Task<ConnectorOAuthTokenResult> RefreshTokenAsync(
        ConnectorProviderConfig config, string refreshToken, CancellationToken cancellationToken = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        };
        var result = await RequestIntuitTokenAsync(config, form, cancellationToken);
        return result with { RefreshToken = result.RefreshToken ?? refreshToken };
    }

    public override async Task<IReadOnlyList<(string Id, string Type, string? DisplayName, string? Email, bool Active, string? RawJson)>> ListQuickBooksMastersAsync(
        string accessToken, string realmId, string masterType, int maxResults, string? extraConfigJson, CancellationToken cancellationToken = default)
    {
        var type = NormalizeEntity(masterType, MasterTypes, "Customer");
        maxResults = Math.Clamp(maxResults, 1, 100);
        var query = $"select * from {type} maxresults {maxResults}";
        var root = await QueryAsync(accessToken, realmId, query, extraConfigJson, cancellationToken);
        var items = new List<(string, string, string?, string?, bool, string?)>();

        if (!TryGetQueryArray(root, type, out var arr))
            return items;

        foreach (var el in arr.EnumerateArray())
        {
            var id = el.TryGetProperty("Id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrEmpty(id))
                continue;

            string? display = null;
            if (el.TryGetProperty("DisplayName", out var dn)) display = dn.GetString();
            else if (el.TryGetProperty("Name", out var nm)) display = nm.GetString();

            string? email = null;
            if (el.TryGetProperty("PrimaryEmailAddr", out var pea) && pea.TryGetProperty("Address", out var addr))
                email = addr.GetString();

            var active = !el.TryGetProperty("Active", out var act) || act.ValueKind != JsonValueKind.False;
            items.Add((id, type, display, email, active, el.GetRawText()));
        }

        return items;
    }

    public override async Task<IReadOnlyList<(string Id, string Type, string? DocNumber, string? TxnDate, decimal? TotalAmount, string? PartyName, string? Status, string? RawJson)>> ListQuickBooksDocumentsAsync(
        string accessToken, string realmId, string documentType, int maxResults, string? extraConfigJson, CancellationToken cancellationToken = default)
    {
        var type = NormalizeEntity(documentType, DocumentTypes, "Invoice");
        maxResults = Math.Clamp(maxResults, 1, 100);
        var query = $"select * from {type} maxresults {maxResults}";
        var root = await QueryAsync(accessToken, realmId, query, extraConfigJson, cancellationToken);
        var items = new List<(string, string, string?, string?, decimal?, string?, string?, string?)>();

        if (!TryGetQueryArray(root, type, out var arr))
            return items;

        foreach (var el in arr.EnumerateArray())
        {
            var id = el.TryGetProperty("Id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrEmpty(id))
                continue;

            string? docNumber = el.TryGetProperty("DocNumber", out var dn) ? dn.GetString() : null;
            string? txnDate = el.TryGetProperty("TxnDate", out var td) ? td.GetString() : null;
            decimal? total = el.TryGetProperty("TotalAmt", out var ta) && ta.TryGetDecimal(out var amt) ? amt : null;

            string? party = null;
            if (el.TryGetProperty("CustomerRef", out var cr) && cr.TryGetProperty("name", out var crn))
                party = crn.GetString();
            else if (el.TryGetProperty("VendorRef", out var vr) && vr.TryGetProperty("name", out var vrn))
                party = vrn.GetString();

            string? status = el.TryGetProperty("EmailStatus", out var es) ? es.GetString() : null;
            if (status == null && el.TryGetProperty("PrintStatus", out var ps))
                status = ps.GetString();

            items.Add((id, type, docNumber, txnDate, total, party, status, el.GetRawText()));
        }

        return items;
    }

    public override async Task<(Stream Content, string ContentType, string FileName)> DownloadQuickBooksDocumentPdfAsync(
        string accessToken, string realmId, string documentType, string documentId, string? extraConfigJson, CancellationToken cancellationToken = default)
    {
        var type = NormalizeEntity(documentType, DocumentTypes, "Invoice");
        if (string.IsNullOrWhiteSpace(documentId))
            throw new ArgumentException("documentId is required.");
        if (string.IsNullOrWhiteSpace(realmId))
            throw new InvalidOperationException("QuickBooks realmId is missing. Re-authorize the connector.");

        var baseUrl = ResolveApiBase(extraConfigJson);
        var url = $"{baseUrl}/v3/company/{Uri.EscapeDataString(realmId)}/{type.ToLowerInvariant()}/{Uri.EscapeDataString(documentId)}/pdf";

        using var client = CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/pdf"));
        using var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"QuickBooks PDF download failed ({(int)response.StatusCode}): {err}");
        }

        var ms = new MemoryStream();
        await response.Content.CopyToAsync(ms, cancellationToken);
        ms.Position = 0;
        return (ms, "application/pdf", $"{type}-{documentId}.pdf");
    }

    public override async Task<string?> GetQuickBooksPurchaseOrderRawByDocNumberAsync(
        string accessToken,
        string realmId,
        string poNumber,
        string? extraConfigJson,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(poNumber))
            throw new ArgumentException("poNumber is required.");

        var escaped = EscapeQboLiteral(poNumber.Trim());
        // Exact DocNumber match (PO Number on PurchaseOrder).
        var query = $"select * from PurchaseOrder where DocNumber = '{escaped}'";
        var root = await QueryAsync(accessToken, realmId, query, extraConfigJson, cancellationToken);
        if (!TryGetQueryArray(root, "PurchaseOrder", out var arr) || arr.GetArrayLength() == 0)
            return null;

        return arr[0].GetRawText();
    }

    private static string EscapeQboLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private async Task<JsonElement> QueryAsync(
        string accessToken,
        string realmId,
        string query,
        string? extraConfigJson,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(realmId))
            throw new InvalidOperationException("QuickBooks realmId is missing. Re-authorize the connector.");

        var baseUrl = ResolveApiBase(extraConfigJson);
        var url = $"{baseUrl}/v3/company/{Uri.EscapeDataString(realmId)}/query?query={Uri.EscapeDataString(query)}&minorversion=65";

        using var client = CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await client.SendAsync(req, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"QuickBooks query failed ({(int)response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    private static bool TryGetQueryArray(JsonElement root, string entityType, out JsonElement array)
    {
        array = default;
        if (!root.TryGetProperty("QueryResponse", out var qr))
            return false;
        if (qr.TryGetProperty(entityType, out array) && array.ValueKind == JsonValueKind.Array)
            return true;
        // QBO sometimes returns singular casing
        foreach (var prop in qr.EnumerateObject())
        {
            if (string.Equals(prop.Name, entityType, StringComparison.OrdinalIgnoreCase) &&
                prop.Value.ValueKind == JsonValueKind.Array)
            {
                array = prop.Value;
                return true;
            }
        }
        return false;
    }

    private static string NormalizeEntity(string? requested, HashSet<string> allowed, string fallback)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return fallback;
        var match = allowed.FirstOrDefault(a => string.Equals(a, requested.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match == null)
            throw new InvalidOperationException($"Unsupported QuickBooks type '{requested}'. Allowed: {string.Join(", ", allowed)}.");
        return match;
    }

    private static string ResolveApiBase(string? extraConfigJson)
    {
        var env = ReadExtra(extraConfigJson, "environment")
            ?? ReadExtra(extraConfigJson, "Environment");
        if (string.Equals(env, "sandbox", StringComparison.OrdinalIgnoreCase))
            return "https://sandbox-quickbooks.api.intuit.com";
        return "https://quickbooks.api.intuit.com";
    }

    private async Task<ConnectorOAuthTokenResult> RequestIntuitTokenAsync(
        ConnectorProviderConfig config,
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, config.TokenUrl);
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.ClientId}:{config.ClientSecret}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Content = new FormUrlEncodedContent(form);

        using var response = await client.SendAsync(req, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"QuickBooks token endpoint failed ({(int)response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var access = root.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("QuickBooks token response missing access_token.");
        string? refresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        DateTime? expires = null;
        if (root.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var seconds))
            expires = DateTime.UtcNow.AddSeconds(seconds);

        string? email = null;
        if (root.TryGetProperty("email", out var em))
            email = em.GetString();

        return new ConnectorOAuthTokenResult(access, refresh, expires, email, null);
    }
}
