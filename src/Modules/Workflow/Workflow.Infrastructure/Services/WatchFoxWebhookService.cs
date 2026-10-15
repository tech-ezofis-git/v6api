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
    private readonly IOptions<WatchFoxOptions> _options;
    private readonly ILogger<WatchFoxWebhookService> _logger;

    public WatchFoxWebhookService(
        IOptions<WatchFoxOptions> options,
        ILogger<WatchFoxWebhookService> logger)
    {
        _options = options;
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

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
