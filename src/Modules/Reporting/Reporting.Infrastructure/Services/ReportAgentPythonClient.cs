using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SaaSApp.Reporting.Application.Contracts;
using SaaSApp.SharedKernel.Options;

namespace SaaSApp.Reporting.Infrastructure.Services;

public sealed class ReportAgentPythonClient : IReportAgentPythonClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AgentsChatOptions _agentsChat;
    private readonly ILogger<ReportAgentPythonClient> _logger;

    public ReportAgentPythonClient(
        IHttpClientFactory httpClientFactory,
        IOptions<AgentsChatOptions> agentsChat,
        ILogger<ReportAgentPythonClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _agentsChat = agentsChat.Value;
        _logger = logger;
    }

    public Task<ReportAgentProxyResult> GeneratePromptAsync(
        ReportAgentChatRequest request,
        CancellationToken cancellationToken = default) =>
        PostChatAsync(request, "prompt", cancellationToken);

    public Task<ReportAgentProxyResult> RunReportAsync(
        ReportAgentChatRequest request,
        CancellationToken cancellationToken = default) =>
        PostChatAsync(request, "run", cancellationToken);

    public Task<ReportAgentProxyResult> GetTypesAsync(CancellationToken cancellationToken = default) =>
        GetAgentsAsync("/api/report-agent/types", cancellationToken);

    public Task<ReportAgentProxyResult> GetScopeOptionsAsync(
        string scope,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var path = $"/api/report-agent/scope-options?scope={Uri.EscapeDataString(scope)}&tenantId={tenantId:D}";
        return GetAgentsAsync(path, cancellationToken);
    }

    private async Task<ReportAgentProxyResult> PostChatAsync(
        ReportAgentChatRequest request,
        string phase,
        CancellationToken cancellationToken)
    {
        var apiUrl = _agentsChat.ResolveChatUrl();
        if (string.IsNullOrWhiteSpace(apiUrl))
            throw new InvalidOperationException(
                "Agents:ChatUrl is not configured. Set Agents:ChatUrl (e.g. https://cloud.ezofis.com/chat).");

        var jsonBody = BuildChatBody(request, phase);
        try
        {
            var client = _httpClientFactory.CreateClient(nameof(ReportAgentPythonClient));
            using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            _logger.LogInformation("Report Agent /chat {Phase} → {Url}", phase, apiUrl);

            using var response = await client.PostAsync(apiUrl, content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return ReportAgentChatMapper.Parse((int)response.StatusCode, body);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Report Agent /chat {Phase} timed out at {Url}", phase, apiUrl);
            return new ReportAgentProxyResult { StatusCode = 504, TimedOut = true };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Report Agent /chat {Phase} call to {Url} failed.", phase, apiUrl);
            throw new InvalidOperationException($"Report Agent {phase} service is unavailable: {ex.Message}", ex);
        }
    }

    private async Task<ReportAgentProxyResult> GetAgentsAsync(string path, CancellationToken cancellationToken)
    {
        var chatUrl = _agentsChat.ResolveChatUrl();
        var baseUrl = ReportAgentChatMapper.ResolveAgentsBaseUrl(chatUrl);
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException(
                "Agents:ChatUrl is not configured. Set Agents:ChatUrl (e.g. https://cloud.ezofis.com/chat).");

        var url = baseUrl + path;
        try
        {
            var client = _httpClientFactory.CreateClient(nameof(ReportAgentPythonClient));
            using var response = await client.GetAsync(url, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return new ReportAgentProxyResult { StatusCode = (int)response.StatusCode, RawBody = body };
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ReportAgentProxyResult { StatusCode = 504, TimedOut = true };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Report Agent GET {Url} failed.", url);
            throw new InvalidOperationException($"Report Agent service is unavailable: {ex.Message}", ex);
        }
    }

    internal static string BuildChatBody(ReportAgentChatRequest request, string phase)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("session_id", string.IsNullOrWhiteSpace(request.SessionId)
                ? $"report-{Guid.NewGuid():N}"
                : request.SessionId.Trim());
            writer.WriteString("intent", ReportAgentChatMapper.Intent);

            var message = request.Message ?? request.Description;
            if (!string.IsNullOrWhiteSpace(message))
                writer.WriteString("message", message.Trim());
            else
                writer.WriteString("message", "");

            writer.WritePropertyName("payload");
            writer.WriteStartObject();
            writer.WriteString("phase", phase);
            writer.WriteString("tenant_id", request.TenantId);

            if (!string.IsNullOrWhiteSpace(request.Description))
                writer.WriteString("description", request.Description.Trim());
            if (!string.IsNullOrWhiteSpace(request.ReportType))
                writer.WriteString("report_type", request.ReportType.Trim());
            if (!string.IsNullOrWhiteSpace(request.WorkflowName))
                writer.WriteString("workflow_name", request.WorkflowName.Trim());
            if (!string.IsNullOrWhiteSpace(request.RepositoryName))
                writer.WriteString("repository_name", request.RepositoryName.Trim());
            if (!string.IsNullOrWhiteSpace(request.ReportPrompt))
                writer.WriteString("report_prompt", request.ReportPrompt);
            if (!string.IsNullOrWhiteSpace(request.Model))
                writer.WriteString("model", request.Model.Trim());

            if (request.ReportSpec.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                writer.WritePropertyName("reportSpec");
                request.ReportSpec.WriteTo(writer);
            }

            writer.WriteNumber("page", request.Page < 1 ? 1 : request.Page);
            writer.WriteNumber("pageSize", request.PageSize < 1 ? 25 : request.PageSize);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
