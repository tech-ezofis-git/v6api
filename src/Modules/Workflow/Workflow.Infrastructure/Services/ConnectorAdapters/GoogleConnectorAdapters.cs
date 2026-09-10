using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Infrastructure.Services.ConnectorAdapters;

internal sealed class GcpConnectorAdapter : ConnectorProviderAdapterBase
{
    public GcpConnectorAdapter(IHttpClientFactory httpClientFactory) : base(httpClientFactory) { }

    public override string ProviderCode => "GCP";
    public override bool SupportsFiles => true;
    public override bool SupportsGmail => false;

    public override async Task<ConnectorOAuthTokenResult> ExchangeCodeAsync(
        ConnectorProviderConfig config, string code, CancellationToken cancellationToken = default)
    {
        var token = await base.ExchangeCodeAsync(config, code, cancellationToken);
        var email = await TryGetGoogleEmailAsync(token.AccessToken, cancellationToken);
        return token with { ExternalAccountEmail = email };
    }

    public override async Task<IReadOnlyList<(string Path, string Name, bool IsFolder, long? SizeBytes, DateTime? ModifiedAtUtc)>> ListFilesAsync(
        string accessToken, string? path, string? extraConfigJson, CancellationToken cancellationToken = default)
    {
        var bucket = ReadExtra(extraConfigJson, "bucket")
            ?? throw new InvalidOperationException("GCP connector ConfigJson must include \"bucket\".");
        var prefix = string.IsNullOrWhiteSpace(path) ? "" : path.TrimStart('/');
        var url = $"https://storage.googleapis.com/storage/v1/b/{Uri.EscapeDataString(bucket)}/o?prefix={Uri.EscapeDataString(prefix)}&delimiter=/";

        using var client = CreateClient();
        using var req = AuthorizedGet(url, accessToken);
        using var response = await client.SendAsync(req, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GCS list failed ({(int)response.StatusCode}): {body}");

        var items = new List<(string, string, bool, long?, DateTime?)>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("prefixes", out var prefixes))
        {
            foreach (var p in prefixes.EnumerateArray())
            {
                var folder = p.GetString();
                if (string.IsNullOrEmpty(folder))
                    continue;
                var name = folder.TrimEnd('/').Split('/').LastOrDefault() ?? folder;
                items.Add((folder, name, true, null, null));
            }
        }
        if (doc.RootElement.TryGetProperty("items", out var files))
        {
            foreach (var f in files.EnumerateArray())
            {
                var objectName = f.GetProperty("name").GetString();
                if (string.IsNullOrEmpty(objectName))
                    continue;
                long? size = f.TryGetProperty("size", out var s) && long.TryParse(s.GetString(), out var n) ? n : null;
                DateTime? modified = f.TryGetProperty("updated", out var u) && DateTime.TryParse(u.GetString(), out var dt)
                    ? dt.ToUniversalTime()
                    : null;
                var shortName = objectName.Split('/').LastOrDefault() ?? objectName;
                items.Add((objectName, shortName, false, size, modified));
            }
        }
        return items;
    }

    public override async Task UploadFileAsync(
        string accessToken, string path, string fileName, Stream content, string? contentType, string? extraConfigJson, CancellationToken cancellationToken = default)
    {
        var bucket = ReadExtra(extraConfigJson, "bucket")
            ?? throw new InvalidOperationException("GCP connector must include \"bucket\" in ConfigJson.");
        var objectName = string.IsNullOrWhiteSpace(path)
            ? fileName
            : $"{path.TrimEnd('/')}/{fileName}".TrimStart('/');
        var url = $"https://storage.googleapis.com/upload/storage/v1/b/{Uri.EscapeDataString(bucket)}/o?uploadType=media&name={Uri.EscapeDataString(objectName)}";

        using var client = CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = new StreamContent(content);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? "application/octet-stream");
        using var response = await client.SendAsync(req, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"GCS upload failed ({(int)response.StatusCode}): {body}");
        }
    }

    public override async Task<(Stream Content, string ContentType, string FileName)> DownloadFileAsync(
        string accessToken, string path, string? extraConfigJson, CancellationToken cancellationToken = default)
    {
        var bucket = ReadExtra(extraConfigJson, "bucket")
            ?? throw new InvalidOperationException("GCP connector must include \"bucket\" in ConfigJson.");
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("path is required.");
        var objectName = path.TrimStart('/');
        var url = $"https://storage.googleapis.com/storage/v1/b/{Uri.EscapeDataString(bucket)}/o/{Uri.EscapeDataString(objectName)}?alt=media";

        using var client = CreateClient();
        using var req = AuthorizedGet(url, accessToken);
        using var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"GCS download failed ({(int)response.StatusCode}): {body}");
        }

        var ms = new MemoryStream();
        await response.Content.CopyToAsync(ms, cancellationToken);
        ms.Position = 0;
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        var fileName = objectName.Split('/').LastOrDefault() ?? "download";
        return (ms, contentType, fileName);
    }

    internal static async Task<string?> TryGetGoogleEmailAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/oauth2/v2/userinfo");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(req, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("email", out var email) ? email.GetString() : null;
    }
}

internal sealed class GmailConnectorAdapter : ConnectorProviderAdapterBase
{
    public GmailConnectorAdapter(IHttpClientFactory httpClientFactory) : base(httpClientFactory) { }

    public override string ProviderCode => "GMAIL";
    public override bool SupportsFiles => false;
    public override bool SupportsGmail => true;

    public override async Task<ConnectorOAuthTokenResult> ExchangeCodeAsync(
        ConnectorProviderConfig config, string code, CancellationToken cancellationToken = default)
    {
        var token = await base.ExchangeCodeAsync(config, code, cancellationToken);
        var email = await GcpConnectorAdapter.TryGetGoogleEmailAsync(token.AccessToken, cancellationToken);
        return token with { ExternalAccountEmail = email };
    }

    public override async Task<(int TotalCount, int UnreadCount)> GetMailSummaryAsync(
        string accessToken, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        using var req = AuthorizedGet("https://gmail.googleapis.com/gmail/v1/users/me/labels/INBOX", accessToken);
        using var res = await client.SendAsync(req, cancellationToken);
        var body = await res.Content.ReadAsStringAsync(cancellationToken);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gmail summary failed ({(int)res.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var total = doc.RootElement.TryGetProperty("messagesTotal", out var mt) ? mt.GetInt32() : 0;
        var unread = doc.RootElement.TryGetProperty("messagesUnread", out var mu) ? mu.GetInt32() : 0;
        return (total, unread);
    }

    public override async Task<IReadOnlyList<(string Id, string? ThreadId, string? Subject, string? From, string? Snippet, DateTime? ReceivedAtUtc, bool IsUnread, IReadOnlyList<(string Id, string? FileName, string? MimeType, long? SizeBytes)> Attachments)>> ListGmailMessagesAsync(
        string accessToken, int maxResults, string? query, bool unreadOnly, CancellationToken cancellationToken = default)
    {
        maxResults = Math.Clamp(maxResults, 1, 50);
        var qParts = new List<string> { "in:inbox" };
        if (unreadOnly)
            qParts.Add("is:unread");
        if (!string.IsNullOrWhiteSpace(query))
            qParts.Add(query.Trim());
        var listUrl =
            $"https://gmail.googleapis.com/gmail/v1/users/me/messages?maxResults={maxResults}&q={Uri.EscapeDataString(string.Join(' ', qParts))}";

        using var client = CreateClient();
        using var listReq = AuthorizedGet(listUrl, accessToken);
        using var listRes = await client.SendAsync(listReq, cancellationToken);
        var listBody = await listRes.Content.ReadAsStringAsync(cancellationToken);
        if (!listRes.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gmail list failed ({(int)listRes.StatusCode}): {listBody}");

        var results = new List<(string, string?, string?, string?, string?, DateTime?, bool, IReadOnlyList<(string, string?, string?, long?)>)>();
        using var listDoc = JsonDocument.Parse(listBody);
        if (!listDoc.RootElement.TryGetProperty("messages", out var messages))
            return results;

        foreach (var m in messages.EnumerateArray())
        {
            var id = m.GetProperty("id").GetString();
            if (string.IsNullOrEmpty(id))
                continue;

            var detail = await LoadGmailMessageAsync(client, accessToken, id, includeBody: false, cancellationToken);
            if (detail != null)
            {
                results.Add((
                    detail.Value.Id,
                    detail.Value.ThreadId,
                    detail.Value.Subject,
                    detail.Value.From,
                    detail.Value.Snippet,
                    detail.Value.ReceivedAtUtc,
                    detail.Value.IsUnread,
                    detail.Value.Attachments));
            }
        }

        return results;
    }

    public override async Task<(string Id, string? ThreadId, string? Subject, string? From, string? Snippet, string? BodyText, string? BodyHtml, DateTime? ReceivedAtUtc, bool IsUnread, IReadOnlyList<(string Id, string? FileName, string? MimeType, long? SizeBytes)> Attachments)> GetMailMessageAsync(
        string accessToken, string messageId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            throw new ArgumentException("messageId is required.");

        using var client = CreateClient();
        var detail = await LoadGmailMessageAsync(client, accessToken, messageId, includeBody: true, cancellationToken)
            ?? throw new InvalidOperationException("Gmail message not found.");

        return (
            detail.Id,
            detail.ThreadId,
            detail.Subject,
            detail.From,
            detail.Snippet,
            detail.BodyText,
            detail.BodyHtml,
            detail.ReceivedAtUtc,
            detail.IsUnread,
            detail.Attachments);
    }

    public override async Task MarkMailMessageReadAsync(
        string accessToken, string messageId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            throw new ArgumentException("messageId is required.");

        using var client = CreateClient();
        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://gmail.googleapis.com/gmail/v1/users/me/messages/{Uri.EscapeDataString(messageId)}/modify")
        {
            Content = new StringContent(
                """{"removeLabelIds":["UNREAD"]}""",
                Encoding.UTF8,
                "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var res = await client.SendAsync(req, cancellationToken);
        var body = await res.Content.ReadAsStringAsync(cancellationToken);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gmail mark-as-read failed ({(int)res.StatusCode}): {body}");
    }

    public override async Task<(Stream Content, string ContentType, string FileName)> DownloadGmailAttachmentAsync(
        string accessToken, string messageId, string attachmentId, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        using var req = AuthorizedGet(
            $"https://gmail.googleapis.com/gmail/v1/users/me/messages/{Uri.EscapeDataString(messageId)}/attachments/{Uri.EscapeDataString(attachmentId)}",
            accessToken);
        using var response = await client.SendAsync(req, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gmail attachment failed ({(int)response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var data = doc.RootElement.GetProperty("data").GetString()
            ?? throw new InvalidOperationException("Attachment data missing.");
        var bytes = Convert.FromBase64String(data.Replace('-', '+').Replace('_', '/'));
        // Gmail attachment bytes API does not return the original name; callers must use message metadata.
        return (new MemoryStream(bytes), "application/octet-stream", string.Empty);
    }

    private static async Task<(string Id, string? ThreadId, string? Subject, string? From, string? Snippet, DateTime? ReceivedAtUtc, bool IsUnread, IReadOnlyList<(string Id, string? FileName, string? MimeType, long? SizeBytes)> Attachments, string? BodyText, string? BodyHtml)?> LoadGmailMessageAsync(
        HttpClient client,
        string accessToken,
        string messageId,
        bool includeBody,
        CancellationToken cancellationToken)
    {
        using var getReq = AuthorizedGet(
            $"https://gmail.googleapis.com/gmail/v1/users/me/messages/{Uri.EscapeDataString(messageId)}?format=full",
            accessToken);
        using var getRes = await client.SendAsync(getReq, cancellationToken);
        var getBody = await getRes.Content.ReadAsStringAsync(cancellationToken);
        if (!getRes.IsSuccessStatusCode)
            return null;

        using var msg = JsonDocument.Parse(getBody);
        var root = msg.RootElement;
        string? subject = null, from = null;
        DateTime? received = null;
        if (root.TryGetProperty("payload", out var payload) && payload.TryGetProperty("headers", out var headers))
        {
            foreach (var h in headers.EnumerateArray())
            {
                var name = h.GetProperty("name").GetString();
                var value = h.GetProperty("value").GetString();
                if (string.Equals(name, "Subject", StringComparison.OrdinalIgnoreCase)) subject = value;
                if (string.Equals(name, "From", StringComparison.OrdinalIgnoreCase)) from = value;
                if (string.Equals(name, "Date", StringComparison.OrdinalIgnoreCase) && DateTime.TryParse(value, out var dt))
                    received = dt.ToUniversalTime();
            }
        }

        var isUnread = false;
        if (root.TryGetProperty("labelIds", out var labels) && labels.ValueKind == JsonValueKind.Array)
        {
            foreach (var label in labels.EnumerateArray())
            {
                if (string.Equals(label.GetString(), "UNREAD", StringComparison.OrdinalIgnoreCase))
                {
                    isUnread = true;
                    break;
                }
            }
        }

        var attachments = new List<(string, string?, string?, long?)>();
        string? bodyText = null;
        string? bodyHtml = null;
        if (root.TryGetProperty("payload", out var payloadRoot))
        {
            CollectAttachments(payloadRoot, attachments);
            if (includeBody)
                ExtractBodies(payloadRoot, ref bodyText, ref bodyHtml);
        }

        return (
            messageId,
            root.TryGetProperty("threadId", out var tid) ? tid.GetString() : null,
            subject,
            from,
            root.TryGetProperty("snippet", out var sn) ? sn.GetString() : null,
            received,
            isUnread,
            attachments,
            bodyText,
            bodyHtml);
    }

    private static void ExtractBodies(JsonElement part, ref string? bodyText, ref string? bodyHtml)
    {
        var mime = part.TryGetProperty("mimeType", out var mt) ? mt.GetString() : null;
        if (part.TryGetProperty("body", out var body) &&
            body.TryGetProperty("data", out var dataEl) &&
            !string.IsNullOrWhiteSpace(dataEl.GetString()))
        {
            var decoded = Encoding.UTF8.GetString(
                Convert.FromBase64String(dataEl.GetString()!.Replace('-', '+').Replace('_', '/')));
            if (string.Equals(mime, "text/plain", StringComparison.OrdinalIgnoreCase) && bodyText == null)
                bodyText = decoded;
            else if (string.Equals(mime, "text/html", StringComparison.OrdinalIgnoreCase) && bodyHtml == null)
                bodyHtml = decoded;
        }

        if (part.TryGetProperty("parts", out var parts))
        {
            foreach (var child in parts.EnumerateArray())
                ExtractBodies(child, ref bodyText, ref bodyHtml);
        }
    }

    private static void CollectAttachments(JsonElement part, List<(string, string?, string?, long?)> attachments)
    {
        if (part.TryGetProperty("body", out var body)
            && body.TryGetProperty("attachmentId", out var aid)
            && !string.IsNullOrWhiteSpace(aid.GetString()))
        {
            var fileName = ResolveGmailPartFileName(part);
            if (!string.IsNullOrWhiteSpace(fileName) && ShouldIncludeGmailAttachment(part, fileName))
            {
                long? size = body.TryGetProperty("size", out var s) ? s.GetInt64() : null;
                string? mime = part.TryGetProperty("mimeType", out var mt) ? mt.GetString() : null;
                attachments.Add((aid.GetString()!, fileName, mime, size));
            }
        }

        if (part.TryGetProperty("parts", out var parts))
        {
            foreach (var child in parts.EnumerateArray())
                CollectAttachments(child, attachments);
        }
    }

    private static string? ResolveGmailPartFileName(JsonElement part)
    {
        if (part.TryGetProperty("filename", out var fn) && !string.IsNullOrWhiteSpace(fn.GetString()))
            return fn.GetString()!.Trim();

        if (!part.TryGetProperty("headers", out var headers) || headers.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var h in headers.EnumerateArray())
        {
            var name = h.TryGetProperty("name", out var n) ? n.GetString() : null;
            var value = h.TryGetProperty("value", out var v) ? v.GetString() : null;
            if (!string.Equals(name, "Content-Disposition", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var match = Regex.Match(
                value,
                @"filename\*?=(?:UTF-8''|"")?([^"";\r\n]+)",
                RegexOptions.IgnoreCase);
            if (match.Success)
                return Uri.UnescapeDataString(match.Groups[1].Value.Trim().Trim('"'));
        }

        return null;
    }

    private static bool ShouldIncludeGmailAttachment(JsonElement part, string? fileName = null)
    {
        fileName ??= ResolveGmailPartFileName(part);
        var mime = part.TryGetProperty("mimeType", out var mt) ? mt.GetString() : null;
        long? size = null;
        if (part.TryGetProperty("body", out var body) && body.TryGetProperty("size", out var s))
            size = s.GetInt64();

        if (IsDocumentLikeMailAttachment(fileName, mime, size))
            return true;

        return !IsInlineGmailPart(part);
    }

    private static bool IsDocumentLikeMailAttachment(string? fileName, string? mimeType, long? sizeBytes)
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

    private static bool IsInlineGmailPart(JsonElement part)
    {
        if (!part.TryGetProperty("headers", out var headers) || headers.ValueKind != JsonValueKind.Array)
            return false;

        var hasContentId = false;
        foreach (var h in headers.EnumerateArray())
        {
            var name = h.TryGetProperty("name", out var n) ? n.GetString() : null;
            var value = h.TryGetProperty("value", out var v) ? v.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
                continue;

            if (name.Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase)
                && value.Contains("inline", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (name.Equals("Content-ID", StringComparison.OrdinalIgnoreCase))
                hasContentId = true;
        }

        // Embedded signature images usually have Content-ID + image mime.
        if (hasContentId
            && part.TryGetProperty("mimeType", out var mt)
            && (mt.GetString() ?? string.Empty).StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
