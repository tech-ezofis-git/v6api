using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaaSApp.Api.Services;
using SaaSApp.MultiTenancy;
using SaaSApp.Security;

namespace SaaSApp.Api.Controllers;

/// <summary>
/// Create Folder wizard drafts — save after each step, resume after network drop.
/// Steps: 1 folderDetails, 2 fields, 3 storage, 4 versioning, 5 integrations.
/// </summary>
[ApiController]
[Route("api/folder-creation/drafts")]
[Authorize(Policy = AuthorizationPolicies.TenantUser)]
public sealed class FolderCreationDraftController : ControllerBase
{
    private readonly IFolderCreationDraftService _drafts;
    private readonly ITenantProvider _tenantProvider;

    public FolderCreationDraftController(
        IFolderCreationDraftService drafts,
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
    [ProducesResponseType(typeof(FolderCreationDraftDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Save(
        [FromBody] SaveFolderCreationDraftApiRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = request.TenantId != Guid.Empty ? request.TenantId : RequireTenantId();
            var userId = request.UserId != Guid.Empty
                ? request.UserId
                : GetUserId() ?? throw new ArgumentException("UserId is required.");

            var saved = await _drafts.SaveAsync(
                new SaveFolderCreationDraftRequest(
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
    [ProducesResponseType(typeof(FolderCreationDraftDto), StatusCodes.Status200OK)]
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
            return NotFound(new { error = "No in-progress folder creation draft found." });

        return Ok(draft);
    }

    /// <summary>Get a draft by id.</summary>
    [HttpGet("{draftId:guid}")]
    [ProducesResponseType(typeof(FolderCreationDraftDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid draftId, CancellationToken cancellationToken)
    {
        var draft = await _drafts.GetByIdAsync(draftId, RequireTenantId(), cancellationToken);
        if (draft == null)
            return NotFound(new { error = "Draft not found." });

        return Ok(draft);
    }

    /// <summary>Mark draft completed after folder is created successfully.</summary>
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

    /// <summary>Discard / soft-delete an in-progress draft.</summary>
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

public sealed record SaveFolderCreationDraftApiRequest(
    Guid TenantId,
    Guid UserId,
    int CurrentStep,
    string? CurrentStepKey = null,
    string? DraftJson = null,
    Guid? DraftId = null);
