using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaaSApp.Api.Services;

namespace SaaSApp.Api.Controllers;

/// <summary>Save and load portal UI JSON in catalog DB per tenant + user (no JWT required).</summary>
[ApiController]
[Route("api/portal-json")]
[AllowAnonymous]
public sealed class PortalJsonController : ControllerBase
{
    private readonly IPortalJsonService _portalJson;

    public PortalJsonController(IPortalJsonService portalJson)
    {
        _portalJson = portalJson;
    }

    /// <summary>
    /// Save / upsert portal JSON in catalog DB for the given tenant + user.
    /// </summary>
    [HttpPost]
    [HttpPut]
    [ProducesResponseType(typeof(PortalJsonSnapshotDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Save(
        [FromBody] SavePortalJsonApiRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (request.TenantId == Guid.Empty)
                return BadRequest(new { error = "tenantId is required." });
            if (request.UserId == Guid.Empty)
                return BadRequest(new { error = "userId is required." });

            var saved = await _portalJson.SaveAsync(
                new SavePortalJsonRequest(request.TenantId, request.UserId, request.PortalJson ?? "{}"),
                cancellationToken);
            return Ok(saved);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Get saved portal JSON from catalog DB for the given tenant + user.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PortalJsonSnapshotDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(
        [FromQuery] Guid tenantId,
        [FromQuery] Guid userId,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
            return BadRequest(new { error = "tenantId is required." });
        if (userId == Guid.Empty)
            return BadRequest(new { error = "userId is required." });

        var snapshot = await _portalJson.GetForUserAsync(tenantId, userId, cancellationToken);
        if (snapshot == null)
            return NotFound(new { error = "No portal JSON found for this tenant and user." });

        return Ok(snapshot);
    }
}

public sealed record SavePortalJsonApiRequest(
    Guid TenantId,
    Guid UserId,
    string? PortalJson = null);
