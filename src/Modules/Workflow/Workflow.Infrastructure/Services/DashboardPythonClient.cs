using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Infrastructure.Options;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>
/// Proxies dashboard calls to the Python API at
/// <c>{ApiBaseUrl}/prompts</c>, <c>{ApiBaseUrl}/dashboard/schema</c>, and <c>{ApiBaseUrl}/dashboard/data</c>.
/// Local <c>Dashboard:ApiBaseUrl</c> typically includes <c>/api</c> (Phase-8 wiring).
/// </summary>
public sealed class DashboardPythonClient : IDashboardPythonClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DashboardPythonOptions _options;
    private readonly ILogger<DashboardPythonClient> _logger;

    public DashboardPythonClient(
        IHttpClientFactory httpClientFactory,
        IOptions<DashboardPythonOptions> options,
        ILogger<DashboardPythonClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public Task<DashboardPythonProxyResult> GetPromptAsync(
        DashboardPromptRequest request,
        CancellationToken cancellationToken = default) =>
        PostRawAsync(ToPromptJson(request), "prompts", "prompt", cancellationToken);

    public Task<DashboardPythonProxyResult> GetSchemaAsync(
        DashboardSchemaRequest request,
        CancellationToken cancellationToken = default) =>
        PostRawAsync(ToSchemaJson(request), "dashboard/schema", "schema", cancellationToken);

    public Task<DashboardPythonProxyResult> GetDataAsync(
        DashboardDataRequest request,
        CancellationToken cancellationToken = default) =>
        PostRawAsync(ToSchemaJson(request), "dashboard/data", "data", cancellationToken);

    private async Task<DashboardPythonProxyResult> PostRawAsync(
        string jsonBody,
        string relativePath,
        string operation,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            throw new InvalidOperationException("Dashboard Python API is disabled.");

        var apiUrl = CombineUrl(_options.ApiBaseUrl, relativePath);
        if (string.IsNullOrWhiteSpace(apiUrl))
            throw new InvalidOperationException("Dashboard:ApiBaseUrl is not configured.");

        try
        {
            var client = _httpClientFactory.CreateClient(nameof(DashboardPythonClient));
            using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var response = await client.PostAsync(apiUrl, content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var contentType = response.Content.Headers.ContentType?.ToString();

            return new DashboardPythonProxyResult((int)response.StatusCode, contentType, body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Dashboard Python {Operation} call to {Url} failed.", operation, apiUrl);
            throw new InvalidOperationException(
                $"Dashboard Python {operation} service is unavailable: {ex.Message}", ex);
        }
    }

    private static string ToPromptJson(DashboardPromptRequest request)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "session_id",
                string.IsNullOrWhiteSpace(request.SessionId) ? "demo" : request.SessionId.Trim());

            if (request.TenantId is { } tenantId && tenantId != Guid.Empty)
                writer.WriteString("tenant_id", tenantId);

            if (request.RepositoryId is { } repositoryId && repositoryId != Guid.Empty)
                writer.WriteString("repository_id", repositoryId);

            if (request.WorkflowId is { } workflowId && workflowId != Guid.Empty)
                writer.WriteString("workflow_id", workflowId);

            if (!string.IsNullOrWhiteSpace(request.RepositoryName))
                writer.WriteString("repository_name", request.RepositoryName.Trim());

            if (!string.IsNullOrWhiteSpace(request.WorkflowName))
                writer.WriteString("workflow_name", request.WorkflowName.Trim());

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string ToSchemaJson(DashboardSchemaRequest request)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            var sessionId = string.IsNullOrWhiteSpace(request.SessionId) ? "default" : request.SessionId;
            writer.WriteString("session_id", sessionId);

            if (!string.IsNullOrWhiteSpace(request.Message))
                writer.WriteString("message", request.Message);

            if (request.TenantId is { } tenantId && tenantId != Guid.Empty)
                writer.WriteString("tenant_id", tenantId);

            if (request.RepositoryId is { } repositoryId && repositoryId != Guid.Empty)
                writer.WriteString("repository_id", repositoryId);

            if (request.WorkflowId is { } workflowId && workflowId != Guid.Empty)
                writer.WriteString("workflow_id", workflowId);

            if (request is DashboardDataRequest data
                && data.DashboardJson.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
            {
                writer.WritePropertyName("dashboard_json");
                data.DashboardJson.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string CombineUrl(string? baseUrl, string relativePath)
    {
        var root = baseUrl?.Trim();
        if (string.IsNullOrWhiteSpace(root))
            return string.Empty;

        return $"{root.TrimEnd('/')}/{relativePath.TrimStart('/')}";
    }
}
