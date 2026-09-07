using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaaSApp.MultiTenancy;
using SaaSApp.Security;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Api.Controllers;

/// <summary>
/// Proxies EZOFIS v6 dashboard schema/data calls to the Python API
/// (<c>{Dashboard:ApiBaseUrl}/dashboard/schema</c> and <c>.../data</c>).
/// </summary>
[ApiController]
[Route("api/dashboard")]
[Authorize(Policy = AuthorizationPolicies.TenantUser)]
public sealed class DashboardController : ControllerBase
{
    private readonly IDashboardPythonClient _client;
    private readonly ITenantProvider _tenantProvider;

    public DashboardController(IDashboardPythonClient client, ITenantProvider tenantProvider)
    {
        _client = client;
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
    /// Hydrate enabled widgets from an edited schema. Response is embeddable HTML
    /// (KPI values and charts). Use <c>response.text()</c> and set <c>innerHTML</c>.
    /// </summary>
    /// <remarks>
    /// Python: <c>POST {ApiBaseUrl}/dashboard/data</c>.
    /// Required: <c>session_id</c>, <c>tenant_id</c> (filled from the current tenant when omitted),
    /// one of <c>repository_id</c> or <c>workflow_id</c>, and <c>dashboard_json</c>
    /// (the entire <c>dashboard_result</c> from schema after enable/disable edits).
    /// </remarks>
    [HttpPost("data")]
    [Produces("text/html", "application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status504GatewayTimeout)]
    public async Task<IActionResult> Data(
        [FromBody] DashboardDataRequest? request,
        CancellationToken cancellationToken = default)
    {
        request ??= new DashboardDataRequest();
        if (!TryValidate(request, requireDashboardJson: true, request.DashboardJson, out var error))
            return BadRequest(new { error });

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
