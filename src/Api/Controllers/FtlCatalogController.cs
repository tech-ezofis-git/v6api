using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaaSApp.Api.Services;
using SaaSApp.Security;

namespace SaaSApp.Api.Controllers;

/// <summary>FTL product catalog from catalog database <c>public.ftl_catalog</c>.</summary>
[ApiController]
[Route("api/ftl/catalog")]
[Authorize(Policy = AuthorizationPolicies.TenantUser)]
public sealed class FtlCatalogController : ControllerBase
{
    private readonly IFtlCatalogService _catalog;

    public FtlCatalogController(IFtlCatalogService catalog)
    {
        _catalog = catalog;
    }

    /// <summary>
    /// One lookup for the FTL catalog.
    /// Leave <c>productCode</c> empty (or send <c>all</c>) to list every product code.
    /// Send a product code to return that product's details.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(FtlCatalogQueryResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Query(
        [FromBody] FtlCatalogQueryRequest? request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _catalog.QueryAsync(request ?? new FtlCatalogQueryRequest(), cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
