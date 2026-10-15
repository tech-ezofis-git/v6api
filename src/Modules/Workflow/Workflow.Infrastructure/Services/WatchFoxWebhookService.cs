using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Infrastructure.Jobs;
using SaaSApp.Workflow.Infrastructure.Options;

namespace SaaSApp.Workflow.Infrastructure.Services;

public sealed class WatchFoxWebhookService : IWatchFoxWebhookService
{
    private const string WatchFoxBaseUrl = "https://watchfox.run";

    private readonly IOptions<WatchFoxOptions> _options;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<WatchFoxWebhookService> _logger;

    public WatchFoxWebhookService(
        IOptions<WatchFoxOptions> options,
        IHttpClientFactory http,
        ILogger<WatchFoxWebhookService> logger)
    {
        _options = options;
        _http = http;
        _logger = logger;
    }

    public Task AcceptAsync(string rawBody, string? signatureHeader, CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        var secret = options.WebhookSecret?.Trim();
        if (string.IsNullOrWhiteSpace(secret) || !SignatureMatches(secret, rawBody, signatureHeader))
            throw new UnauthorizedAccessException("WatchFox signature is invalid.");

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(rawBody) ? "{}" : rawBody);
        var root = doc.RootElement;
        var eventType = ReadString(root, "type");
        if (!string.Equals(eventType, "email.matched", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("WatchFox event {EventType} ignored.", eventType ?? "(none)");
            return Task.CompletedTask;
        }

        var isInvoice = IsInvoice(root);
        var isEnquiry = IsEnquiry(root);
        if (!isInvoice && !isEnquiry)
        {
            _logger.LogInformation("WatchFox event {EventId} is not an invoice or enquiry. No ticket started.", ReadString(root, "id"));
            return Task.CompletedTask;
        }

        var tenantId = ReadMetaGuid(root, "tenantId", "tenant_id") ?? ParseGuid(options.TenantId);
        var workflowId = ReadMetaGuid(root, "workflowId", "workflow_id") ?? ParseGuid(options.WorkflowId);
        if (isInvoice && (tenantId == null || workflowId == null))
            throw new InvalidOperationException("WatchFox tenantId and workflowId are not configured.");

        if (!root.TryGetProperty("email", out var email) || email.ValueKind != JsonValueKind.Object)
        {
            _logger.LogInformation("WatchFox invoice event has no email object.");
            return Task.CompletedTask;
        }

        var eventId = ReadString(root, "id") ?? Guid.NewGuid().ToString("N");
        var from = ParseMailbox(ReadString(email, "from"));
        var subject = ReadString(email, "subject");
        var messageId = ReadString(email, "message_id") ?? ReadString(email, "id");
        long? received = email.TryGetProperty("received", out var receivedElement) && receivedElement.TryGetInt64(out var unix)
            ? unix
            : null;

        var queued = 0;
        if (isInvoice
            && email.TryGetProperty("attachments", out var attachments)
            && attachments.ValueKind == JsonValueKind.Array)
        {
            foreach (var attachment in attachments.EnumerateArray())
            {
                if (!IsDocument(attachment))
                    continue;

                var url = ReadString(attachment, "url");
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                var fileName = ReadString(attachment, "filename");
                if (string.IsNullOrWhiteSpace(fileName))
                    fileName = "document.pdf";

                var attachmentId = ReadString(attachment, "id") ?? queued.ToString();
                var args = new WatchFoxInvoiceJobArgs(
                    EventKey: Truncate($"{eventId}:{attachmentId}", 160),
                    TenantId: tenantId!.Value,
                    WorkflowId: workflowId!.Value,
                    FileName: fileName,
                    ContentType: ReadString(attachment, "content_type"),
                    DownloadUrl: url,
                    FromEmail: from,
                    Subject: subject,
                    ReceivedUnix: received,
                    MessageId: messageId);

                BackgroundJob.Enqueue<RunWatchFoxInvoiceJob>(job => job.Execute(args, null));
                queued++;
            }
        }

        if (isInvoice && queued == 0)
            _logger.LogInformation("WatchFox invoice event {EventId} has no document attachment. No ticket started.", eventId);
        else if (isInvoice)
            _logger.LogInformation("WatchFox invoice event {EventId} queued {Count} ticket start(s).", eventId, queued);

        if (isEnquiry)
            QueueEnquiry(options, root, email, eventId, tenantId, from, subject, messageId);

        return Task.CompletedTask;
    }

    public Task AcceptEnquiryFileAsync(
        byte[] eml,
        string fileName,
        string? contentType,
        string? signatureHeader,
        string? signedPayload,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (eml == null || eml.Length == 0)
            throw new InvalidOperationException("WatchFox enquiry .eml is empty.");

        var options = _options.Value;
        var secret = options.WebhookSecret?.Trim();
        var signed = string.IsNullOrWhiteSpace(signedPayload) ? Convert.ToBase64String(eml) : signedPayload;
        if (string.IsNullOrWhiteSpace(secret) || !SignatureMatches(secret, signed, signatureHeader))
            throw new UnauthorizedAccessException("WatchFox signature is invalid.");

        var tenantId = ParseGuid(options.TenantId);
        var workflowId = ParseGuid(options.EnquiryWorkflowId);
        if (tenantId == null || workflowId == null)
            throw new InvalidOperationException("WatchFox tenantId and EnquiryWorkflowId are not configured.");

        var safeName = string.IsNullOrWhiteSpace(fileName) ? "message.eml" : fileName.Trim();
        var args = new WatchFoxEnquiryJobArgs(
            EventKey: Truncate($"enquiry-file:{Guid.NewGuid():N}", 160),
            TenantId: tenantId.Value,
            WorkflowId: workflowId.Value,
            FileName: safeName,
            ContentType: string.IsNullOrWhiteSpace(contentType) ? "message/rfc822" : contentType,
            DownloadUrl: null,
            Content: eml,
            FromEmail: null,
            Subject: null,
            MessageId: null);
        BackgroundJob.Enqueue<RunWatchFoxEnquiryJob>(job => job.Execute(args, null));

        _logger.LogInformation("WatchFox enquiry file {FileName} queued for workflow {WorkflowId}.", safeName, workflowId);
        return Task.CompletedTask;
    }

    public async Task<WatchFoxConnectLinkResult> CreateMicrosoftConnectLinkAsync(
        Guid tenantId,
        Guid workflowId,
        string? redirectUrl,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || workflowId == Guid.Empty)
            throw new InvalidOperationException("tenantId and workflowId are required.");

        var redirect = string.IsNullOrWhiteSpace(redirectUrl)
            ? "https://cloud.ezofis.com"
            : redirectUrl.Trim();
        if (!Uri.TryCreate(redirect, UriKind.Absolute, out var redirectUri)
            || redirectUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("redirectUrl must be an https address.");

        var payload = JsonSerializer.Serialize(new
        {
            provider = "m365",
            redirect_url = ConnectCallbackUrl(),
            state = EncodeConnectState(tenantId, workflowId, redirect),
            metadata = new Dictionary<string, string>
            {
                ["tenantId"] = tenantId.ToString("D"),
                ["enquiryWorkflowId"] = workflowId.ToString("D")
            }
        });

        using var doc = await SendWatchFoxAsync(HttpMethod.Post, "/v1/connect-links", payload, cancellationToken);
        var url = ReadString(doc.RootElement, "url");
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("WatchFox did not return a Microsoft connect URL.");

        var expiresIn = 3600;
        if (doc.RootElement.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds))
            expiresIn = seconds;

        return new WatchFoxConnectLinkResult(url, expiresIn);
    }

    public async Task<string> CompleteMicrosoftConnectAsync(
        string? status,
        string? connectionId,
        string? state,
        CancellationToken cancellationToken = default)
    {
        if (!TryDecodeConnectState(state, out var tenantId, out var workflowId, out var redirect))
            return AppendQuery("https://cloud.ezofis.com", new Dictionary<string, string?>
            {
                ["status"] = "error",
                ["error"] = "Office 365 connect state was missing."
            });

        if (!string.Equals(status, "connected", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(connectionId))
        {
            return AppendQuery(redirect, new Dictionary<string, string?>
            {
                ["status"] = status ?? "error",
                ["error"] = "Office 365 mailbox was not connected."
            });
        }

        try
        {
            var watch = await CreateEnquiryWatchAsync(connectionId, tenantId, workflowId, cancellationToken);
            return AppendQuery(redirect, new Dictionary<string, string?>
            {
                ["status"] = "connected",
                ["connectionId"] = watch.ConnectionId,
                ["watchId"] = watch.WatchId,
                ["tenantId"] = watch.TenantId.ToString("D"),
                ["workflowId"] = watch.WorkflowId.ToString("D")
            });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "WatchFox enquiry watch was not created after Office 365 connect.");
            return AppendQuery(redirect, new Dictionary<string, string?>
            {
                ["status"] = "error",
                ["connectionId"] = connectionId,
                ["error"] = ex.Message
            });
        }
    }

    public async Task<WatchFoxEnquiryWatchResult> CreateEnquiryWatchAsync(
        string connectionId,
        Guid tenantId,
        Guid workflowId,
        CancellationToken cancellationToken = default)
    {
        var connection = connectionId?.Trim() ?? string.Empty;
        if (!connection.StartsWith("cn_", StringComparison.Ordinal) || tenantId == Guid.Empty || workflowId == Guid.Empty)
            throw new InvalidOperationException("connectionId, tenantId, and workflowId are required.");

        var secret = _options.Value.WebhookSecret?.Trim();
        if (string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("WatchFox:WebhookSecret is not configured.");

        var webhookUrl = _options.Value.WebhookUrl?.Trim();
        if (string.IsNullOrWhiteSpace(webhookUrl))
            webhookUrl = "https://cloud.ezofis.com/api/email-ingest/watchfox";

        await DeleteEnquiryWatchesAsync(connection, cancellationToken);

        var payload = JsonSerializer.Serialize(new
        {
            connection_id = connection,
            what = new[] { "enquiry" },
            include = new[] { "eml" },
            webhook_url = webhookUrl,
            webhook_secret = secret,
            metadata = new Dictionary<string, string>
            {
                ["tenantId"] = tenantId.ToString("D"),
                ["enquiryWorkflowId"] = workflowId.ToString("D")
            }
        });

        using var doc = await SendWatchFoxAsync(HttpMethod.Post, "/v1/watches", payload, cancellationToken);
        var watchId = ReadString(doc.RootElement, "id");
        if (string.IsNullOrWhiteSpace(watchId))
            throw new InvalidOperationException("WatchFox did not return a watch id.");

        _logger.LogInformation(
            "WatchFox enquiry watch {WatchId} started for connection {ConnectionId} workflow {WorkflowId}.",
            watchId,
            connection,
            workflowId);

        return new WatchFoxEnquiryWatchResult(watchId, connection, tenantId, workflowId);
    }

    private async Task DeleteEnquiryWatchesAsync(string connectionId, CancellationToken cancellationToken)
    {
        using var doc = await SendWatchFoxAsync(HttpMethod.Get, "/v1/watches", null, cancellationToken);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return;

        foreach (var watch in doc.RootElement.EnumerateArray())
        {
            var existingConnection = ReadString(watch, "connection_id");
            if (!string.Equals(existingConnection, connectionId, StringComparison.Ordinal))
                continue;

            var isEnquiry = false;
            if (watch.TryGetProperty("what", out var what) && what.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in what.EnumerateArray())
                {
                    var name = item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString();
                    if (!string.IsNullOrWhiteSpace(name)
                        && name.Contains("enquir", StringComparison.OrdinalIgnoreCase))
                    {
                        isEnquiry = true;
                        break;
                    }
                }
            }

            var watchId = ReadString(watch, "id");
            if (!isEnquiry || string.IsNullOrWhiteSpace(watchId))
                continue;

            using var deleted = await SendWatchFoxAsync(HttpMethod.Delete, $"/v1/watches/{watchId}", null, cancellationToken);
            _logger.LogInformation("Removed previous WatchFox enquiry watch {WatchId}.", watchId);
        }
    }

    private async Task<JsonDocument> SendWatchFoxAsync(
        HttpMethod method,
        string path,
        string? json,
        CancellationToken cancellationToken)
    {
        var apiKey = _options.Value.ApiKey?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("WatchFox:ApiKey is not configured.");

        var client = _http.CreateClient(nameof(WatchFoxWebhookService));
        using var request = new HttpRequestMessage(method, WatchFoxBaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (json != null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"WatchFox returned {(int)response.StatusCode}: {Truncate(body, 300)}");

        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
    }

    private void QueueEnquiry(
        WatchFoxOptions options,
        JsonElement root,
        JsonElement email,
        string eventId,
        Guid? tenantId,
        string? from,
        string? subject,
        string? messageId)
    {
        var workflowId = ReadMetaGuid(root, "enquiryWorkflowId", "enquiry_workflow_id")
            ?? ParseGuid(options.EnquiryWorkflowId);
        if (tenantId == null || workflowId == null)
            throw new InvalidOperationException("WatchFox tenantId and EnquiryWorkflowId are not configured.");

        var eml = FindEml(email);
        if (eml == null)
        {
            _logger.LogInformation("WatchFox enquiry event {EventId} has no .eml. No ticket started.", eventId);
            return;
        }

        var args = new WatchFoxEnquiryJobArgs(
            EventKey: Truncate($"enquiry:{eventId}", 160),
            TenantId: tenantId.Value,
            WorkflowId: workflowId.Value,
            FileName: eml.Value.FileName,
            ContentType: eml.Value.ContentType,
            DownloadUrl: eml.Value.Url,
            Content: eml.Value.Content,
            FromEmail: from,
            Subject: subject,
            MessageId: messageId);
        BackgroundJob.Enqueue<RunWatchFoxEnquiryJob>(job => job.Execute(args, null));

        _logger.LogInformation(
            "WatchFox enquiry event {EventId} queued workflow {WorkflowId} start with {FileName}.",
            eventId,
            workflowId,
            eml.Value.FileName);
    }

    private static bool SignatureMatches(string secret, string rawBody, string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
            return false;

        string? timestamp = null;
        string? signature = null;
        foreach (var piece in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var split = piece.IndexOf('=');
            if (split <= 0)
                continue;
            var key = piece[..split];
            var value = piece[(split + 1)..];
            if (key == "t")
                timestamp = value;
            else if (key == "v1")
                signature = value;
        }

        if (string.IsNullOrWhiteSpace(timestamp)
            || string.IsNullOrWhiteSpace(signature)
            || !long.TryParse(timestamp, out var unix))
            return false;

        var age = Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unix);
        if (age > 300)
            return false;

        var prefix = Encoding.UTF8.GetBytes(timestamp + ".");
        var body = Encoding.UTF8.GetBytes(rawBody ?? string.Empty);
        var data = new byte[prefix.Length + body.Length];
        Buffer.BlockCopy(prefix, 0, data, 0, prefix.Length);
        Buffer.BlockCopy(body, 0, data, prefix.Length, body.Length);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = Convert.ToHexString(hmac.ComputeHash(data)).ToLowerInvariant();
        var actual = signature.Trim().ToLowerInvariant();
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static bool IsInvoice(JsonElement root)
    {
        if (root.TryGetProperty("matches", out var matches) && matches.ValueKind == JsonValueKind.Array)
        {
            foreach (var match in matches.EnumerateArray())
            {
                var what = ReadString(match, "what");
                if (!string.IsNullOrWhiteSpace(what)
                    && what.Contains("invoice", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return root.TryGetProperty("results", out var results)
            && results.ValueKind == JsonValueKind.Object
            && results.TryGetProperty("invoice", out var invoice)
            && invoice.TryGetProperty("matched", out var matched)
            && matched.ValueKind == JsonValueKind.True;
    }

    private static bool IsEnquiry(JsonElement root)
    {
        if (root.TryGetProperty("matches", out var matches) && matches.ValueKind == JsonValueKind.Array)
        {
            foreach (var match in matches.EnumerateArray())
            {
                var what = ReadString(match, "what");
                if (!string.IsNullOrWhiteSpace(what)
                    && what.Contains("enquir", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return root.TryGetProperty("results", out var results)
            && results.ValueKind == JsonValueKind.Object
            && results.TryGetProperty("enquiries", out var enquiries)
            && enquiries.TryGetProperty("matched", out var matched)
            && matched.ValueKind == JsonValueKind.True;
    }

    private static (string? Url, byte[]? Content, string FileName, string ContentType)? FindEml(JsonElement email)
    {
        foreach (var name in new[] { "eml_url", "emlUrl", "raw_url", "rawUrl", "source_url", "sourceUrl" })
        {
            var url = ReadString(email, name);
            if (!string.IsNullOrWhiteSpace(url))
                return (url, null, "message.eml", "message/rfc822");
        }

        var inline = DecodeEml(ReadString(email, "eml_base64") ?? ReadString(email, "eml"));
        if (inline is { Length: > 0 })
            return (null, inline, "message.eml", "message/rfc822");

        if (email.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Object)
        {
            var raw = ReadString(links, "raw");
            if (!string.IsNullOrWhiteSpace(raw))
                return (raw, null, "message.eml", "message/rfc822");
        }

        if (!email.TryGetProperty("attachments", out var attachments) || attachments.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var attachment in attachments.EnumerateArray())
        {
            var name = ReadString(attachment, "filename") ?? string.Empty;
            var type = ReadString(attachment, "content_type") ?? string.Empty;
            var isEml = name.EndsWith(".eml", StringComparison.OrdinalIgnoreCase)
                || type.Contains("rfc822", StringComparison.OrdinalIgnoreCase)
                || type.Contains("message/rfc822", StringComparison.OrdinalIgnoreCase);
            if (!isEml)
                continue;

            var url = ReadString(attachment, "url");
            var content = DecodeEml(ReadString(attachment, "content") ?? ReadString(attachment, "data"));
            if (string.IsNullOrWhiteSpace(url) && content is not { Length: > 0 })
                continue;

            if (string.IsNullOrWhiteSpace(name))
                name = "message.eml";
            return (string.IsNullOrWhiteSpace(url) ? null : url, content, name, string.IsNullOrWhiteSpace(type) ? "message/rfc822" : type);
        }

        return null;
    }

    private static byte[]? DecodeEml(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        if (trimmed.StartsWith("From ", StringComparison.Ordinal) || trimmed.Contains("\nMIME-Version", StringComparison.OrdinalIgnoreCase))
            return Encoding.UTF8.GetBytes(trimmed);
        try
        {
            return Convert.FromBase64String(trimmed);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool IsDocument(JsonElement attachment)
    {
        var name = ReadString(attachment, "filename") ?? string.Empty;
        var type = ReadString(attachment, "content_type") ?? string.Empty;
        if (type.Contains("pdf", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase))
            return true;

        return string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(type);
    }

    private static Guid? ReadMetaGuid(JsonElement root, params string[] names)
    {
        if (!root.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            var value = ReadString(metadata, name);
            var id = ParseGuid(value);
            if (id != null)
                return id;
        }

        return null;
    }

    private static Guid? ParseGuid(string? value) =>
        Guid.TryParse(value, out var id) && id != Guid.Empty ? id : null;

    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static string? ParseMailbox(string? from)
    {
        if (string.IsNullOrWhiteSpace(from))
            return null;
        var start = from.LastIndexOf('<');
        var end = from.LastIndexOf('>');
        if (start >= 0 && end > start)
            return from[(start + 1)..end].Trim();
        return from.Trim();
    }

    private string ConnectCallbackUrl()
    {
        var webhook = _options.Value.WebhookUrl?.Trim();
        if (string.IsNullOrWhiteSpace(webhook))
            webhook = "https://cloud.ezofis.com/api/email-ingest/watchfox";
        return webhook.TrimEnd('/') + "/connected";
    }

    private static string EncodeConnectState(Guid tenantId, Guid workflowId, string redirectUrl)
    {
        var json = JsonSerializer.Serialize(new ConnectState(tenantId, workflowId, redirectUrl));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool TryDecodeConnectState(
        string? state,
        out Guid tenantId,
        out Guid workflowId,
        out string redirectUrl)
    {
        tenantId = Guid.Empty;
        workflowId = Guid.Empty;
        redirectUrl = "https://cloud.ezofis.com";
        if (string.IsNullOrWhiteSpace(state))
            return false;

        try
        {
            var padded = state.Trim().Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var parsed = JsonSerializer.Deserialize<ConnectState>(json);
            if (parsed == null || parsed.TenantId == Guid.Empty || parsed.WorkflowId == Guid.Empty)
                return false;
            if (!Uri.TryCreate(parsed.RedirectUrl, UriKind.Absolute, out var redirect)
                || redirect.Scheme != Uri.UriSchemeHttps)
                return false;

            tenantId = parsed.TenantId;
            workflowId = parsed.WorkflowId;
            redirectUrl = parsed.RedirectUrl;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string AppendQuery(string redirect, IReadOnlyDictionary<string, string?> query)
    {
        var parts = new List<string>();
        foreach (var pair in query)
        {
            if (string.IsNullOrWhiteSpace(pair.Value))
                continue;
            parts.Add($"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}");
        }

        if (parts.Count == 0)
            return redirect;

        var separator = redirect.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return redirect + separator + string.Join("&", parts);
    }

    private sealed record ConnectState(Guid TenantId, Guid WorkflowId, string RedirectUrl);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
