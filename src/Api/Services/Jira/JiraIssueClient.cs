using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace SaaSApp.Api.Services.Jira;

public sealed class JiraCreateIssueRequest
{
    public string? SupportCategory { get; init; }
    public string? Priority { get; init; }
    public string? PreferredContact { get; init; }
    public string? PhoneNO { get; init; }
    public string? RequestDescription { get; init; }
    public bool IsEmailSend { get; init; }
    public string? CallerEmail { get; init; }
}

public sealed class JiraCreateIssueResult
{
    public bool Success { get; init; }
    public string? IssueId { get; init; }
    public string? IssueKey { get; init; }
    public string? IssueUrl { get; init; }
    public string? RawResponse { get; init; }
}

/// <summary>
/// Creates Jira Cloud issues via REST API v3, or via a local Python gateway when
/// <see cref="JiraOptions.UseProxy"/> is true (workaround for Windows Schannel TLS limits).
/// </summary>
public sealed class JiraIssueClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly JiraOptions _options;
    private readonly ILogger<JiraIssueClient> _logger;

    public JiraIssueClient(
        HttpClient httpClient,
        IOptions<JiraOptions> options,
        ILogger<JiraIssueClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<JiraCreateIssueResult> CreateIssueAsync(
        JiraCreateIssueRequest request,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return new JiraCreateIssueResult
            {
                Success = false,
                RawResponse = "Jira:Enabled is false; skipped remote create."
            };
        }

        if (_options.UseProxy)
            return await CreateIssueViaProxyAsync(request, cancellationToken);

        return await CreateIssueDirectAsync(request, cancellationToken);
    }

    private async Task<JiraCreateIssueResult> CreateIssueViaProxyAsync(
        JiraCreateIssueRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ProxyBaseUrl))
        {
            return new JiraCreateIssueResult
            {
                Success = false,
                RawResponse = "Jira:UseProxy is true but ProxyBaseUrl is not configured."
            };
        }

        var proxyUrl = $"{_options.ProxyBaseUrl.TrimEnd('/')}/jira/create-issue";
        var payload = new
        {
            supportCategory = request.SupportCategory,
            priority = request.Priority,
            preferredContact = request.PreferredContact,
            phoneNO = request.PhoneNO,
            requestDescription = request.RequestDescription,
            isEmailSend = request.IsEmailSend,
            callerEmail = request.CallerEmail,
            // Optional overrides; gateway config is the source of truth when omitted.
            projectKey = _options.ProjectKey,
            issueType = _options.IssueType,
            baseUrl = _options.BaseUrl
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);

        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, proxyUrl);
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Jira proxy create issue failed with {StatusCode}: {Body}",
                    (int)response.StatusCode,
                    body);

                return ParseProxyResult(body, fallbackSuccess: false)
                    ?? new JiraCreateIssueResult { Success = false, RawResponse = body };
            }

            return ParseProxyResult(body, fallbackSuccess: true)
                ?? new JiraCreateIssueResult { Success = false, RawResponse = body };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Jira proxy create issue threw an exception");
            return new JiraCreateIssueResult
            {
                Success = false,
                RawResponse = ex.Message
            };
        }
    }

    private static JiraCreateIssueResult? ParseProxyResult(string body, bool fallbackSuccess)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            bool success;
            if (root.TryGetProperty("success", out var s))
                success = s.ValueKind == JsonValueKind.True;
            else
                success = fallbackSuccess && root.TryGetProperty("issueKey", out _);

            return new JiraCreateIssueResult
            {
                Success = success,
                IssueId = root.TryGetProperty("issueId", out var idEl) ? idEl.GetString() : null,
                IssueKey = root.TryGetProperty("issueKey", out var keyEl) ? keyEl.GetString() : null,
                IssueUrl = root.TryGetProperty("issueUrl", out var urlEl) ? urlEl.GetString() : null,
                RawResponse = root.TryGetProperty("rawResponse", out var rawEl)
                    ? rawEl.GetString()
                    : body
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<JiraCreateIssueResult> CreateIssueDirectAsync(
        JiraCreateIssueRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl)
            || string.IsNullOrWhiteSpace(_options.Email)
            || string.IsNullOrWhiteSpace(_options.ApiToken))
        {
            return new JiraCreateIssueResult
            {
                Success = false,
                RawResponse = "Jira BaseUrl, Email, or ApiToken is not configured."
            };
        }

        var baseUrl = _options.BaseUrl.TrimEnd('/');
        var summary = string.IsNullOrWhiteSpace(request.SupportCategory)
            ? "Support request"
            : request.SupportCategory.Trim();
        if (summary.Length > 255)
            summary = summary[..255];

        var descriptionText = BuildDescription(request);
        var payload = BuildCreateIssuePayload(summary, descriptionText, request.Priority);
        var json = JsonSerializer.Serialize(payload);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/rest/api/3/issue");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.Email}:{_options.ApiToken}")));
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Jira create issue failed with {StatusCode}: {Body}",
                    (int)response.StatusCode,
                    body);

                return new JiraCreateIssueResult
                {
                    Success = false,
                    RawResponse = body
                };
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var issueId = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var issueKey = root.TryGetProperty("key", out var keyEl) ? keyEl.GetString() : null;
            var issueUrl = !string.IsNullOrWhiteSpace(issueKey)
                ? $"{baseUrl}/browse/{issueKey}"
                : null;

            return new JiraCreateIssueResult
            {
                Success = true,
                IssueId = issueId,
                IssueKey = issueKey,
                IssueUrl = issueUrl,
                RawResponse = body
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Jira create issue threw an exception");
            return new JiraCreateIssueResult
            {
                Success = false,
                RawResponse = ex.Message
            };
        }
    }

    private object BuildCreateIssuePayload(string summary, string descriptionText, string? priority)
    {
        var fields = new Dictionary<string, object?>
        {
            ["project"] = new { key = _options.ProjectKey },
            ["summary"] = summary,
            ["issuetype"] = new { name = _options.IssueType },
            ["description"] = BuildAdfDescription(descriptionText)
        };

        var jiraPriority = MapPriority(priority);
        if (!string.IsNullOrWhiteSpace(jiraPriority))
            fields["priority"] = new { name = jiraPriority };

        return new { fields };
    }

    private static string? MapPriority(string? priority)
    {
        if (string.IsNullOrWhiteSpace(priority))
            return null;

        return priority.Trim() switch
        {
            "Low" => "Low",
            "Normal" => "Medium",
            "High" => "High",
            "Urgent" => "Highest",
            _ => priority.Trim()
        };
    }

    private static string BuildDescription(JiraCreateIssueRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Support category: {request.SupportCategory}");
        sb.AppendLine($"Priority: {request.Priority}");
        sb.AppendLine($"Preferred contact: {request.PreferredContact}");
        sb.AppendLine($"Phone: {request.PhoneNO}");
        sb.AppendLine($"Caller email: {request.CallerEmail}");
        sb.AppendLine($"Email updates: {request.IsEmailSend}");
        sb.AppendLine();
        sb.AppendLine("Description:");
        sb.AppendLine(request.RequestDescription ?? string.Empty);
        return sb.ToString();
    }

    private static object BuildAdfDescription(string text)
    {
        var paragraphs = text.Replace("\r\n", "\n").Split('\n');
        var content = new List<object>();
        foreach (var line in paragraphs)
        {
            content.Add(new
            {
                type = "paragraph",
                content = new object[]
                {
                    new { type = "text", text = line }
                }
            });
        }

        if (content.Count == 0)
        {
            content.Add(new
            {
                type = "paragraph",
                content = new object[]
                {
                    new { type = "text", text = "" }
                }
            });
        }

        return new
        {
            type = "doc",
            version = 1,
            content
        };
    }
}
