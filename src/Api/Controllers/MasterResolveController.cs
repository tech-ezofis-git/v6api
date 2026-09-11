using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaaSApp.Security;
using SaaSApp.Workflow.Application.Connectors;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Api.Controllers;

[ApiController]
[Route("api/master")]
[Authorize(Policy = AuthorizationPolicies.TenantUser)]
public sealed class MasterResolveController : ControllerBase
{
    private readonly IMasterResolveService _masterResolve;

    public MasterResolveController(IMasterResolveService masterResolve)
    {
        _masterResolve = masterResolve;
    }

    /// <summary>
    /// Resolve Vendor/Customer/Item from InternalForm, QuickBooks, or SAP (sample ConfigJson) master.
    /// Pass mailboxId to use that mailbox's master binding, or source+formId/connectorId explicitly.
    /// </summary>
    [HttpGet("resolve")]
    [ProducesResponseType(typeof(MasterResolveResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Resolve(
        [FromQuery] string type,
        [FromQuery] string? q = null,
        [FromQuery] int maxResults = 50,
        [FromQuery] string? source = null,
        [FromQuery] string? formId = null,
        [FromQuery] Guid? connectorId = null,
        [FromQuery] Guid? mailboxId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _masterResolve.ResolveAsync(
                type, q, maxResults, source, formId, connectorId, mailboxId, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (NotSupportedException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
