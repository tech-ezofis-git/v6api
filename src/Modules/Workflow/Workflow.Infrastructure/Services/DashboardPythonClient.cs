using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SaaSApp.SharedKernel.Options;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Infrastructure.Options;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>
/// Proxies dashboard prompts/schema/data through agents <c>POST /chat</c>
/// with <c>intent=dashboard</c> and <c>payload.phase</c> = prompts | schema | data.
/// V6 keeps <c>/api/dashboard/prompts|schema|data</c>; there is no separate Python /dashboard/schema.
/// </summary>
public sealed class DashboardPythonClient : IDashboardPythonClient
{
    private const string DashboardIntent = "dashboard";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DashboardPythonOptions _options;
    private readonly AgentsChatOptions _agentsChat;
    private readonly ILogger<DashboardPythonClient> _logger;

    public DashboardPythonClient(
        IHttpClientFactory httpClientFactory,
        IOptions<DashboardPythonOptions> options,
        IOptions<AgentsChatOptions> agentsChat,
        ILogger<DashboardPythonClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _agentsChat = agentsChat.Value;
        _logger = logger;
    }

    public Task<DashboardPythonProxyResult> GetPromptAsync(
        DashboardPromptRequest request,
        CancellationToken cancellationToken = default) =>
        PostChatAsync(
            BuildChatBody(
                sessionId: string.IsNullOrWhiteSpace(request.SessionId) ? "demo" : request.SessionId.Trim(),
                message: null,
                phase: "prompts",
                tenantId: request.TenantId,
                repositoryId: request.RepositoryId,
                workflowId: request.WorkflowId,
                repositoryName: request.RepositoryName,
                workflowName: request.WorkflowName,
                dashboardJson: default),
            phase: "prompts",
            cancellationToken);

    public Task<DashboardPythonProxyResult> GetSchemaAsync(
        DashboardSchemaRequest request,
        CancellationToken cancellationToken = default) =>
        PostChatAsync(
            BuildChatBody(
                sessionId: string.IsNullOrWhiteSpace(request.SessionId) ? "demo" : request.SessionId.Trim(),
                message: request.Message,
                phase: "schema",
                tenantId: request.TenantId,
                repositoryId: request.RepositoryId,
                workflowId: request.WorkflowId,
                repositoryName: null,
                workflowName: null,
                dashboardJson: default),
            phase: "schema",
            cancellationToken);

    public Task<DashboardPythonProxyResult> GetDataAsync(
        DashboardDataRequest request,
        CancellationToken cancellationToken = default) =>
        PostChatAsync(
            BuildChatBody(
                sessionId: string.IsNullOrWhiteSpace(request.SessionId) ? "demo" : request.SessionId.Trim(),
                message: string.IsNullOrWhiteSpace(request.Message) ? "apply" : request.Message,
                phase: "data",
                tenantId: request.TenantId,
                repositoryId: request.RepositoryId,
                workflowId: request.WorkflowId,
                repositoryName: null,
                workflowName: null,
                dashboardJson: request.DashboardJson),
            phase: "data",
            cancellationToken);

    private async Task<DashboardPythonProxyResult> PostChatAsync(
        string jsonBody,
        string phase,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            throw new InvalidOperationException("Dashboard Python API is disabled.");

        var apiUrl = _agentsChat.ResolveChatUrl();
        if (string.IsNullOrWhiteSpace(apiUrl))
            throw new InvalidOperationException(
                "Agents:ChatUrl is not configured. Set Agents:ChatUrl (e.g. https://cloud.ezofis.com/chat).");

        try
        {
            var client = _httpClientFactory.CreateClient(nameof(DashboardPythonClient));
            using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            _logger.LogInformation(
                "Dashboard /chat {Phase} → {Url}",
                phase,
                apiUrl);

            using var response = await client.PostAsync(apiUrl, content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var contentType = response.Content.Headers.ContentType?.ToString();

            // Data phase: orchestrator returns raw HTML. If agents wrap HTML in Chat JSON, unwrap it.
            if (string.Equals(phase, "data", StringComparison.OrdinalIgnoreCase)
                && TryExtractDashboardHtml(body, out var html))
            {
                return new DashboardPythonProxyResult(
                    (int)response.StatusCode,
                    "text/html; charset=utf-8",
                    html);
            }

            return new DashboardPythonProxyResult((int)response.StatusCode, contentType, body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Dashboard /chat {Phase} call to {Url} failed.", phase, apiUrl);
            throw new InvalidOperationException(
                $"Dashboard {phase} service is unavailable: {ex.Message}", ex);
        }
    }

    private static string BuildChatBody(
        string sessionId,
        string? message,
        string phase,
        Guid? tenantId,
        Guid? repositoryId,
        Guid? workflowId,
        string? repositoryName,
        string? workflowName,
        JsonElement dashboardJson)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("session_id", sessionId);
            writer.WriteString("intent", DashboardIntent);

            if (!string.IsNullOrWhiteSpace(message))
                writer.WriteString("message", message.Trim());

            writer.WritePropertyName("payload");
            writer.WriteStartObject();
            writer.WriteString("phase", phase);

            if (tenantId is { } tid && tid != Guid.Empty)
                writer.WriteString("tenant_id", tid);

            if (repositoryId is { } rid && rid != Guid.Empty)
                writer.WriteString("repository_id", rid);

            if (workflowId is { } wid && wid != Guid.Empty)
                writer.WriteString("workflow_id", wid);

            if (!string.IsNullOrWhiteSpace(repositoryName))
                writer.WriteString("repository_name", repositoryName.Trim());

            if (!string.IsNullOrWhiteSpace(workflowName))
                writer.WriteString("workflow_name", workflowName.Trim());

            if (dashboardJson.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
            {
                writer.WritePropertyName("dashboard_json");
                dashboardJson.WriteTo(writer);
            }

            writer.WriteEndObject(); // payload
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Data phase should be HTML. Accept raw HTML, or Chat JSON with <c>html</c> / nested dashboard HTML.
    /// </summary>
    private static bool TryExtractDashboardHtml(string body, out string html)
    {
        html = string.Empty;
        if (string.IsNullOrWhiteSpace(body))
            return false;

        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('<'))
        {
            html = body;
            return true;
        }

        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (TryReadString(root, "html", out var topHtml))
            {
                html = topHtml!;
                return true;
            }

            if (root.TryGetProperty("dashboard_result", out var result)
                && result.ValueKind == JsonValueKind.Object
                && TryReadString(result, "html", out var nestedHtml))
            {
                html = nestedHtml!;
                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static bool TryReadString(JsonElement el, string name, out string? value)
    {
        value = null;
        if (!el.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            return false;
        value = prop.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }
}
