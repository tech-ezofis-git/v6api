using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaaSApp.Api.Services;
using SaaSApp.MultiTenancy;
using SaaSApp.Security;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Api.Controllers;

/// <summary>
/// Proxies EZOFIS v6 dashboard schema/data/prompts to the Python API and persists
/// schema + HTML in catalog DB per tenant + repository/workflow.
/// </summary>
[ApiController]
[Route("api/dashboard")]
[Authorize(Policy = AuthorizationPolicies.TenantUser)]
public sealed class DashboardController : ControllerBase
{
    private readonly IDashboardPythonClient _client;
    private readonly IDashboardSchemaService _schemaService;
    private readonly ITenantProvider _tenantProvider;

    public DashboardController(
        IDashboardPythonClient client,
        IDashboardSchemaService schemaService,
        ITenantProvider tenantProvider)
    {
        _client = client;
        _schemaService = schemaService;
        _tenantProvider = tenantProvider;
    }

    /// <summary>
    /// Suggest KPIs and charts for a repository or workflow. Response is JSON
    /// (<c>dashboard_result</c> with <c>data: null</c>). No numeric values.
    /// </summary>
    /// <remarks>
    /// Python: <c>POST {ApiBaseUrl}/dashboard/schema</c>.
    /// Required: <c>session_id</c>, <c>tenant_id</c> (filled from the current tenant when omitted),
    /// and one of <c>repository_id</c> or <c>workflow_id</c>.
    /// </remarks>
    [HttpPost("schema")]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status504GatewayTimeout)]
    public async Task<IActionResult> Schema(
        [FromBody] DashboardSchemaRequest? request,
        CancellationToken cancellationToken = default)
    {
        request ??= new DashboardSchemaRequest();
        if (!TryValidate(request, requireDashboardJson: false, dashboardJson: default, out var error))
            return BadRequest(new { error });

        try
        {
            var result = await _client.GetSchemaAsync(request, cancellationToken);
            return ToActionResult(result, fallbackContentType: "application/json");
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { error = "Dashboard schema service timed out." });
        }
    }

    /// <summary>
    /// Suggest a natural-language dashboard prompt from repository/workflow metadata.
    /// Proxies Python <c>POST {ApiBaseUrl}/prompts</c>. Use the returned <c>prompt</c>
    /// as <c>message</c> on <c>POST /api/dashboard/schema</c>.
    /// </summary>
    [HttpPost("prompts")]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status504GatewayTimeout)]
    public async Task<IActionResult> Prompts(
        [FromBody] DashboardPromptRequest? request,
        CancellationToken cancellationToken = default)
    {
        request ??= new DashboardPromptRequest();
        request.TenantId ??= _tenantProvider.GetTenantId();

        if (request.TenantId is null || request.TenantId == Guid.Empty)
            return BadRequest(new { error = "tenant_id is required." });

        if (request.RepositoryId is null && request.WorkflowId is null)
            return BadRequest(new { error = "repository_id or workflow_id is required." });

        try
        {
            var result = await _client.GetPromptAsync(request, cancellationToken);
            return ToActionResult(result, fallbackContentType: "application/json");
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { error = "Dashboard prompt service timed out." });
        }
    }

    [HttpPost("schema/save")]
    [HttpPut("schema/save")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(DashboardSchemaSnapshotDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SaveSchema(
        [FromBody] SaveDashboardSchemaApiRequest? request,
        CancellationToken cancellationToken = default)
    {
        request ??= new SaveDashboardSchemaApiRequest();
        request.TenantId ??= _tenantProvider.GetTenantId();

        if (request.TenantId is null || request.TenantId == Guid.Empty)
            return BadRequest(new { error = "tenant_id is required." });

        if (request.RepositoryId is null && request.WorkflowId is null)
            return BadRequest(new { error = "repository_id or workflow_id is required." });

        try
        {
            var schemaJson = DashboardSchemaJsonResolver.ResolveSchemaJson(
                request.DashboardJson,
                request.DashboardResult);

            using var doc = JsonDocument.Parse(schemaJson);
            var (repoFromJson, workflowFromJson) = DashboardSchemaJsonResolver.ExtractScopeIds(doc.RootElement);

            var saved = await _schemaService.SaveAsync(
                new SaveDashboardSchemaRequest(
                    request.TenantId.Value,
                    request.RepositoryId ?? repoFromJson,
                    request.WorkflowId ?? workflowFromJson,
                    schemaJson),
                cancellationToken);

            return Ok(saved);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("schema/saved")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(DashboardSchemaSnapshotDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSavedSchema(
        [FromQuery] Guid? tenantId,
        [FromQuery] Guid? repositoryId,
        [FromQuery] Guid? workflowId,
        CancellationToken cancellationToken = default)
    {
        tenantId ??= _tenantProvider.GetTenantId();
        if (tenantId is null || tenantId == Guid.Empty)
            return BadRequest(new { error = "tenant_id is required." });

        if (repositoryId is null && workflowId is null)
            return BadRequest(new { error = "repository_id or workflow_id is required." });

        var snapshot = await _schemaService.GetAsync(tenantId.Value, repositoryId, workflowId, cancellationToken);
        if (snapshot == null)
            return NotFound(new { error = "No saved dashboard schema found for this tenant and scope." });

        return Ok(snapshot);
    }

    /// <summary>
    /// Fetch dashboard HTML from Python using saved schema (or pass dashboard_json).
    /// session_id is optional. Does not persist HTML — call POST /data/save to store it.
    /// </summary>
    [HttpPost("data")]
    [Produces("text/html", "application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status504GatewayTimeout)]
    public async Task<IActionResult> Data(
        [FromBody] DashboardDataRequest? request,
        CancellationToken cancellationToken = default)
    {
        request ??= new DashboardDataRequest();
        request.TenantId ??= _tenantProvider.GetTenantId();

        if (request.TenantId is null || request.TenantId == Guid.Empty)
            return BadRequest(new { error = "tenant_id is required." });

        if (request.RepositoryId is null && request.WorkflowId is null)
            return BadRequest(new { error = "repository_id or workflow_id is required." });

        if (request.DashboardJson.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            var saved = await _schemaService.GetAsync(
                request.TenantId.Value,
                request.RepositoryId,
                request.WorkflowId,
                cancellationToken);

            if (saved == null)
                return NotFound(new { error = "No saved dashboard schema found. Save schema first or pass dashboard_json." });

            request.DashboardJson = DashboardSchemaJsonResolver.ParseStoredSchema(saved.SchemaJson);
        }

        try
        {
            var result = await _client.GetDataAsync(request, cancellationToken);
            return ToActionResult(result, fallbackContentType: "text/html");
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { error = "Dashboard data service timed out." });
        }
    }

    /// <summary>
    /// Explicitly save dashboard HTML for a tenant + repository or workflow.
    /// Call this only when the user clicks Save (not from POST /data).
    /// </summary>
    [HttpPost("data/save")]
    [HttpPut("data/save")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(DashboardHtmlSnapshotDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SaveData(
        [FromBody] SaveDashboardHtmlApiRequest? request,
        CancellationToken cancellationToken = default)
    {
        request ??= new SaveDashboardHtmlApiRequest();
        request.TenantId ??= _tenantProvider.GetTenantId();

        if (request.TenantId is null || request.TenantId == Guid.Empty)
            return BadRequest(new { error = "tenant_id is required." });

        if (request.RepositoryId is null && request.WorkflowId is null)
            return BadRequest(new { error = "repository_id or workflow_id is required." });

        if (string.IsNullOrWhiteSpace(request.DashboardHtml))
            return BadRequest(new { error = "dashboard_html is required." });

        try
        {
            await _schemaService.SaveHtmlAsync(
                new SaveDashboardHtmlRequest(
                    request.TenantId.Value,
                    request.RepositoryId,
                    request.WorkflowId,
                    request.DashboardHtml),
                cancellationToken);

            var saved = await _schemaService.GetHtmlAsync(
                request.TenantId.Value,
                request.RepositoryId,
                request.WorkflowId,
                cancellationToken);

            return Ok(saved);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Get saved dashboard HTML for a tenant + repository or workflow.</summary>
    [HttpGet("data/saved")]
    [Produces("application/json", "text/html")]
    [ProducesResponseType(typeof(DashboardHtmlSnapshotDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSavedData(
        [FromQuery] Guid? tenantId,
        [FromQuery] Guid? repositoryId,
        [FromQuery] Guid? workflowId,
        [FromQuery] bool raw = false,
        CancellationToken cancellationToken = default)
    {
        tenantId ??= _tenantProvider.GetTenantId();
        if (tenantId is null || tenantId == Guid.Empty)
            return BadRequest(new { error = "tenant_id is required." });

        if (repositoryId is null && workflowId is null)
            return BadRequest(new { error = "repository_id or workflow_id is required." });

        var snapshot = await _schemaService.GetHtmlAsync(
            tenantId.Value,
            repositoryId,
            workflowId,
            cancellationToken);

        if (snapshot == null)
            return NotFound(new { error = "No saved dashboard HTML found for this tenant and scope." });

        if (raw || Request.Headers.Accept.Any(h => h?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true))
        {
            return Content(snapshot.DashboardHtml, "text/html");
        }

        return Ok(snapshot);
    }

    private bool TryValidate(
        DashboardSchemaRequest request,
        bool requireDashboardJson,
        JsonElement dashboardJson,
        out string error)
    {
        error = string.Empty;
        request.TenantId ??= _tenantProvider.GetTenantId();

        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            error = "session_id is required.";
            return false;
        }

        if (request.TenantId is null || request.TenantId == Guid.Empty)
        {
            error = "tenant_id is required.";
            return false;
        }

        if (request.RepositoryId is null && request.WorkflowId is null)
        {
            error = "repository_id or workflow_id is required.";
            return false;
        }

        if (requireDashboardJson
            && dashboardJson.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            error = "dashboard_json is required.";
            return false;
        }

        return true;
    }

    private static IActionResult ToActionResult(DashboardPythonProxyResult result, string fallbackContentType)
    {
        if (string.IsNullOrWhiteSpace(result.Body))
            return new StatusCodeResult(result.StatusCode);

        var contentType = string.IsNullOrWhiteSpace(result.ContentType)
            ? fallbackContentType
            : result.ContentType;

        return new ContentResult
        {
            StatusCode = result.StatusCode,
            Content = result.Body,
            ContentType = contentType
        };
    }
}

public sealed class SaveDashboardSchemaApiRequest
{
    [JsonPropertyName("tenant_id")]
    public Guid? TenantId { get; set; }

    [JsonPropertyName("repository_id")]
    public Guid? RepositoryId { get; set; }

    [JsonPropertyName("workflow_id")]
    public Guid? WorkflowId { get; set; }

    [JsonPropertyName("dashboard_json")]
    public JsonElement? DashboardJson { get; set; }

    [JsonPropertyName("dashboard_result")]
    public JsonElement? DashboardResult { get; set; }

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

    [JsonPropertyName("dashboardJson")]
    public JsonElement? DashboardJsonCamel
    {
        get => null;
        set
        {
            if (value is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null })
                DashboardJson = value;
        }
    }

    [JsonPropertyName("dashboardResult")]
    public JsonElement? DashboardResultCamel
    {
        get => null;
        set
        {
            if (value is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null })
                DashboardResult = value;
        }
    }
}

public sealed class SaveDashboardHtmlApiRequest
{
    [JsonPropertyName("tenant_id")]
    public Guid? TenantId { get; set; }

    [JsonPropertyName("repository_id")]
    public Guid? RepositoryId { get; set; }

    [JsonPropertyName("workflow_id")]
    public Guid? WorkflowId { get; set; }

    [JsonPropertyName("dashboard_html")]
    public string? DashboardHtml { get; set; }

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

    [JsonPropertyName("dashboardHtml")]
    public string? DashboardHtmlCamel
    {
        get => null;
        set { if (!string.IsNullOrWhiteSpace(value)) DashboardHtml = value; }
    }

    [JsonPropertyName("html")]
    public string? HtmlAlias
    {
        get => null;
        set { if (!string.IsNullOrWhiteSpace(value)) DashboardHtml = value; }
    }
}
