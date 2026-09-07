using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Infrastructure.Options;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>
/// Proxies dashboard schema/data calls to the Python API at
/// <c>{ApiBaseUrl}/dashboard/schema</c> and <c>{ApiBaseUrl}/dashboard/data</c>.
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

    public Task<DashboardPythonProxyResult> GetSchemaAsync(
        DashboardSchemaRequest request,
        CancellationToken cancellationToken = default) =>
        PostAsync(request, "dashboard/schema", "schema", cancellationToken);

    public Task<DashboardPythonProxyResult> GetDataAsync(
        DashboardDataRequest request,
        CancellationToken cancellationToken = default) =>
        PostAsync(request, "dashboard/data", "data", cancellationToken);

    private async Task<DashboardPythonProxyResult> PostAsync(
        DashboardSchemaRequest request,
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
            using var content = new StringContent(ToPythonJson(request), Encoding.UTF8, "application/json");
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

    private static string ToPythonJson(DashboardSchemaRequest request)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("session_id", request.SessionId);

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
