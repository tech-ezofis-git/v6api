using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaaSApp.Api.Services;
using SaaSApp.MultiTenancy;
using SaaSApp.Security;

namespace SaaSApp.Api.Controllers;

/// <summary>
/// Report Builder wizard drafts ΓÇö save after each step, resume after network drop.
/// Steps: 1 ai, 2 details, 3 fields, 4 filters, 5 schedule.
/// </summary>
[ApiController]
[Route("api/report-builder/drafts")]
[Authorize(Policy = AuthorizationPolicies.TenantUser)]
public sealed class ReportBuilderDraftController : ControllerBase
{
    private readonly IReportBuilderDraftService _drafts;
    private readonly ITenantProvider _tenantProvider;

    public ReportBuilderDraftController(
        IReportBuilderDraftService drafts,
        ITenantProvider tenantProvider)
    {
        _drafts = drafts;
        _tenantProvider = tenantProvider;
    }

    /// <summary>
    /// Save / upsert wizard progress after each step.
    /// Send full <c>draftJson</c> so far + <c>currentStep</c> / <c>currentStepKey</c>.
    /// </summary>
    [HttpPut]
    [HttpPost]
    [ProducesResponseType(typeof(ReportBuilderDraftDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Save(
        [FromBody] SaveReportBuilderDraftApiRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = request.TenantId != Guid.Empty ? request.TenantId : RequireTenantId();
            var userId = request.UserId != Guid.Empty
                ? request.UserId
                : GetUserId() ?? throw new ArgumentException("UserId is required.");

            var saved = await _drafts.SaveAsync(
                new SaveReportBuilderDraftRequest(
                    tenantId,
                    userId,
                    request.CurrentStep,
                    request.CurrentStepKey,
                    request.DraftJson,
                    request.DraftId),
                cancellationToken);
            return Ok(saved);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Get the active in-progress draft for this tenant + user (resume wizard).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ReportBuilderDraftDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetActive(
        [FromQuery] Guid? tenantId,
        [FromQuery] Guid? userId,
        CancellationToken cancellationToken)
    {
        var tid = tenantId is { } t && t != Guid.Empty ? t : RequireTenantId();
        var uid = userId is { } u && u != Guid.Empty
            ? u
            : GetUserId() ?? throw new InvalidOperationException("User id is required.");

        var draft = await _drafts.GetActiveAsync(tid, uid, cancellationToken);
        if (draft == null)
            return NotFound(new { error = "No in-progress report builder draft found." });

        return Ok(draft);
    }

    [HttpGet("{draftId:guid}")]
    [ProducesResponseType(typeof(ReportBuilderDraftDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid draftId, CancellationToken cancellationToken)
    {
        var draft = await _drafts.GetByIdAsync(draftId, RequireTenantId(), cancellationToken);
        if (draft == null)
            return NotFound(new { error = "Draft not found." });

        return Ok(draft);
    }

    [HttpPost("{draftId:guid}/complete")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Complete(Guid draftId, CancellationToken cancellationToken)
    {
        var userId = GetUserId() ?? throw new InvalidOperationException("User id is required.");
        var ok = await _drafts.CompleteAsync(draftId, RequireTenantId(), userId, cancellationToken);
        if (!ok)
            return NotFound(new { error = "Draft not found." });

        return Ok(new { completed = true, draftId });
    }

    [HttpDelete("{draftId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid draftId, CancellationToken cancellationToken)
    {
        var userId = GetUserId() ?? throw new InvalidOperationException("User id is required.");
        var ok = await _drafts.DeleteAsync(draftId, RequireTenantId(), userId, cancellationToken);
        if (!ok)
            return NotFound(new { error = "Draft not found." });

        return Ok(new { deleted = true, draftId });
    }

    private Guid RequireTenantId() =>
        _tenantProvider.GetTenantId()
        ?? throw new InvalidOperationException("X-Tenant-Id header is required.");

    private Guid? GetUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? User.FindFirstValue("sub")
                  ?? User.FindFirstValue("oid");
        return Guid.TryParse(raw, out var id) ? id : null;
    }
}

public sealed record SaveReportBuilderDraftApiRequest(
    Guid TenantId,
    Guid UserId,
    int CurrentStep,
    string? CurrentStepKey = null,
    string? DraftJson = null,
    Guid? DraftId = null);
