using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SaaSApp.Repository.Application.Contracts;
using SaaSApp.Repository.Infrastructure.Options;
using SaaSApp.SharedKernel.Options;

namespace SaaSApp.Repository.Infrastructure.Services;

/// <summary>
/// Proxies repository assistant search/chatbot to agents <c>/chat</c>
/// (search: <c>intent=global_search</c>, chatbot: <c>intent=chatbot</c>) via <see cref="AgentsChatOptions.ChatUrl"/>.
/// </summary>
public sealed class RepositoryPythonAssistantClient : IRepositoryPythonAssistantClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly RepositoryPythonAssistantOptions _options;
    private readonly AgentsChatOptions _agentsChat;
    private readonly ILogger<RepositoryPythonAssistantClient> _logger;

    public RepositoryPythonAssistantClient(
        IHttpClientFactory httpClientFactory,
        IOptions<RepositoryPythonAssistantOptions> options,
        IOptions<AgentsChatOptions> agentsChat,
        ILogger<RepositoryPythonAssistantClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _agentsChat = agentsChat.Value;
        _logger = logger;
    }

    public Task<RepositoryPythonProxyResult> SearchAsync(
        RepositoryAssistantSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var query = request.Query?.Trim() ?? string.Empty;
        var intent = string.IsNullOrWhiteSpace(_options.SearchIntent)
            ? "global_search"
            : _options.SearchIntent.Trim();
        var body = BuildChatBody(
            intent: intent,
            message: query,
            query: query,
            tenantId: request.TenantId,
            specificId: request.SpecificId,
            actionFrom: request.ActionFrom,
            token: null,
            sessionId: null);
        return PostChatAsync(body, "search", cancellationToken);
    }

    public Task<RepositoryPythonProxyResult> ChatbotAsync(
        RepositoryAssistantChatbotRequest request,
        CancellationToken cancellationToken = default)
    {
        var message = request.Message?.Trim() ?? string.Empty;
        var intent = string.IsNullOrWhiteSpace(_options.ChatbotIntent)
            ? "chatbot"
            : _options.ChatbotIntent.Trim();
        var body = BuildChatBody(
            intent: intent,
            message: message,
            query: message,
            tenantId: request.TenantId,
            specificId: request.SpecificId,
            actionFrom: request.ActionFrom,
            token: request.Token,
            sessionId: request.SessionId);
        return PostChatAsync(body, "chatbot", cancellationToken);
    }

    private async Task<RepositoryPythonProxyResult> PostChatAsync(
        object body,
        string operation,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            throw new InvalidOperationException("Repository Python assistant is disabled.");

        var apiUrl = ResolveChatUrl();
        if (string.IsNullOrWhiteSpace(apiUrl))
            throw new InvalidOperationException(
                "Agents:ChatUrl is not configured for repository assistant. " +
                "Set Agents:ChatUrl (e.g. https://cloud.ezofis.com/chat).");

        try
        {
            var client = _httpClientFactory.CreateClient(nameof(RepositoryPythonAssistantClient));
            using var response = await client.PostAsJsonAsync(apiUrl, body, JsonOptions, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";

            return new RepositoryPythonProxyResult((int)response.StatusCode, contentType, content);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Repository assistant {Operation} call to {Url} failed.", operation, apiUrl);
            throw new InvalidOperationException(
                $"Repository Python {operation} service is unavailable: {ex.Message}", ex);
        }
    }

    private string ResolveChatUrl()
    {
        var overrideUrl = _options.ChatUrl?.Trim();
        if (!string.IsNullOrWhiteSpace(overrideUrl))
            return overrideUrl;

        // Legacy: prefer SearchUrl/ChatbotUrl only when they are not the old localhost Functions defaults.
        var legacy = FirstNonLocalhost(_options.SearchUrl, _options.ChatbotUrl);
        return _agentsChat.ResolveChatUrl(legacy);
    }

    private static string? FirstNonLocalhost(params string?[] urls)
    {
        foreach (var url in urls)
        {
            var trimmed = url?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;
            if (trimmed.Contains("localhost:7071", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("127.0.0.1:7071", StringComparison.OrdinalIgnoreCase))
                continue;
            return trimmed;
        }

        return null;
    }

    private object BuildChatBody(
        string intent,
        string message,
        string query,
        Guid? tenantId,
        Guid? specificId,
        string? actionFrom,
        string? token,
        string? sessionId)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["query"] = query,
            ["tenantId"] = tenantId,
            ["specificId"] = specificId
        };

        if (!string.IsNullOrWhiteSpace(actionFrom))
            payload["actionFrom"] = actionFrom.Trim();

        if (!string.IsNullOrWhiteSpace(token))
            payload["token"] = token.Trim();

        var resolvedSessionId = string.IsNullOrWhiteSpace(sessionId)
            ? $"repo-assistant-{Guid.NewGuid():N}"
            : sessionId.Trim();

        return new
        {
            session_id = resolvedSessionId,
            intent,
            message,
            payload
        };
    }
}
