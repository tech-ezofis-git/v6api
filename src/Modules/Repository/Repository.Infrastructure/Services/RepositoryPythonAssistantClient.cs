using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SaaSApp.Repository.Application.Contracts;
using SaaSApp.Repository.Infrastructure.Options;

namespace SaaSApp.Repository.Infrastructure.Services;

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
    private readonly ILogger<RepositoryPythonAssistantClient> _logger;

    public RepositoryPythonAssistantClient(
        IHttpClientFactory httpClientFactory,
        IOptions<RepositoryPythonAssistantOptions> options,
        ILogger<RepositoryPythonAssistantClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public Task<RepositoryPythonProxyResult> SearchAsync(
        RepositoryAssistantSearchRequest request,
        CancellationToken cancellationToken = default) =>
        PostAsync(_options.SearchUrl, request, "search", cancellationToken);

    public Task<RepositoryPythonProxyResult> ChatbotAsync(
        RepositoryAssistantChatbotRequest request,
        CancellationToken cancellationToken = default) =>
        PostAsync(_options.ChatbotUrl, request, "chatbot", cancellationToken);

    private async Task<RepositoryPythonProxyResult> PostAsync<T>(
        string? url,
        T body,
        string operation,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            throw new InvalidOperationException("Repository Python assistant is disabled.");

        var apiUrl = url?.Trim();
        if (string.IsNullOrWhiteSpace(apiUrl))
            throw new InvalidOperationException($"RepositoryPythonAssistant URL for {operation} is not configured.");

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
            _logger.LogError(ex, "Repository Python {Operation} call to {Url} failed.", operation, apiUrl);
            throw new InvalidOperationException(
                $"Repository Python {operation} service is unavailable: {ex.Message}", ex);
        }
    }
}
