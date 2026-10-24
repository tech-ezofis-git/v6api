using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaaSApp.MultiTenancy;
using SaaSApp.Reporting.Application.Contracts;
using SaaSApp.Security;

namespace SaaSApp.Api.Controllers;

/// <summary>
/// Report AI Builder: UI sends a generate-report prompt, V6 saves it, then run-by-id
/// loads the saved prompt and executes via Agents <c>/chat</c> intent=report.
/// </summary>
[ApiController]
[Route("api/report-ai-builder")]
[Route("api/reports/agent")]
[Authorize(Policy = AuthorizationPolicies.TenantUser)]
public sealed class ReportAiBuilderController : ControllerBase
{
    private readonly IReportAiBuilderService _service;
    private readonly ITenantProvider _tenantProvider;

    public ReportAiBuilderController(
        IReportAiBuilderService service,
        ITenantProvider tenantProvider)
    {
        _service = service;
        _tenantProvider = tenantProvider;
    }

    [HttpGet("types")]
    public async Task<IActionResult> Types(CancellationToken cancellationToken) =>
        ToActionResult(await _service.ListTypesAsync(cancellationToken));

    [HttpGet("scope-options")]
    public async Task<IActionResult> ScopeOptions(
        [FromQuery] string scope,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        return ToActionResult(await _service.ListScopeOptionsAsync(scope, tenantId, cancellationToken));
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        ToActionResult(await _service.ListAsync(RequireTenantId(), RequireUserId(), cancellationToken));

    /// <summary>Generate a report prompt from the user description and save it. Returns the report id.</summary>
    [HttpPost("prompt")]
    public async Task<IActionResult> Prompt(
        [FromBody] ReportAiPromptRequest? request,
        CancellationToken cancellationToken)
    {
        request ??= new ReportAiPromptRequest();
        return ToActionResult(
            await _service.GenerateAndSaveAsync(RequireTenantId(), RequireUserId(), request, cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        ToActionResult(await _service.GetAsync(RequireTenantId(), id, cancellationToken));

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] ReportAiUpdateRequest? request,
        CancellationToken cancellationToken)
    {
        request ??= new ReportAiUpdateRequest();
        return ToActionResult(await _service.UpdateAsync(RequireTenantId(), id, request, cancellationToken));
    }

    /// <summary>Load the saved prompt for this report id and run it. Returns columns + rows.</summary>
    [HttpPost("{id:guid}/run")]
    public async Task<IActionResult> Run(
        Guid id,
        [FromBody] ReportAiRunRequest? request,
        CancellationToken cancellationToken)
    {
        request ??= new ReportAiRunRequest();
        return ToActionResult(await _service.RunSavedAsync(RequireTenantId(), id, request, cancellationToken));
    }

    private Guid RequireTenantId() =>
        _tenantProvider.GetTenantId()
        ?? throw new InvalidOperationException("X-Tenant-Id header is required.");

    private Guid RequireUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? User.FindFirstValue("sub")
                  ?? User.FindFirstValue("oid")
                  ?? User.FindFirstValue("userId");
        if (!Guid.TryParse(raw, out var id))
            throw new InvalidOperationException("User id is required.");
        return id;
    }

    private static IActionResult ToActionResult(ReportAiHttpResult result) =>
        new ObjectResult(result.Payload) { StatusCode = result.StatusCode };
}
