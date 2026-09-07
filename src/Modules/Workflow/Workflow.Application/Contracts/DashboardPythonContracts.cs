using System.Text.Json;
using System.Text.Json.Serialization;

namespace SaaSApp.Workflow.Application.Contracts;

/// <summary>POST /dashboard/schema body. Wire names match the Python API (snake_case); camelCase aliases are accepted.</summary>
public class DashboardSchemaRequest
{
    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("tenant_id")]
    public Guid? TenantId { get; set; }

    [JsonPropertyName("repository_id")]
    public Guid? RepositoryId { get; set; }

    [JsonPropertyName("workflow_id")]
    public Guid? WorkflowId { get; set; }

    [JsonPropertyName("sessionId")]
    public string? SessionIdCamel
    {
        get => null;
        set { if (!string.IsNullOrWhiteSpace(value)) SessionId = value; }
    }

    [JsonPropertyName("tenantId")]
    public Guid? TenantIdCamel
    {
        get => null;
        set { if (value is { } id && id != Guid.Empty) TenantId = id; }
    }

    [JsonPropertyName("repositoryId")]
    public Guid? RepositoryIdCamel
    {
        get => null;
        set { if (value is { } id && id != Guid.Empty) RepositoryId = id; }
    }

    [JsonPropertyName("workflowId")]
    public Guid? WorkflowIdCamel
    {
        get => null;
        set { if (value is { } id && id != Guid.Empty) WorkflowId = id; }
    }
}

/// <summary>POST /dashboard/data body — schema payload plus edited dashboard_json.</summary>
public sealed class DashboardDataRequest : DashboardSchemaRequest
{
    [JsonPropertyName("dashboard_json")]
    public JsonElement DashboardJson { get; set; }

    [JsonPropertyName("dashboardJson")]
    public JsonElement DashboardJsonCamel
    {
        get => default;
        set
        {
            if (value.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
                DashboardJson = value.Clone();
        }
    }
}

public sealed record DashboardPythonProxyResult(
    int StatusCode,
    string? ContentType,
    string Body);

public interface IDashboardPythonClient
{
    Task<DashboardPythonProxyResult> GetSchemaAsync(
        DashboardSchemaRequest request,
        CancellationToken cancellationToken = default);

    Task<DashboardPythonProxyResult> GetDataAsync(
        DashboardDataRequest request,
        CancellationToken cancellationToken = default);
}
