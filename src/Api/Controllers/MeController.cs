using Npgsql;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SaaSApp.Catalog.Persistence;

namespace SaaSApp.Api.Controllers;

/// <summary>Organizations (tenants) for the current user. Use after login for org picker.</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class MeController : ControllerBase
{
    private readonly IDbContextFactory<CatalogDbContext> _catalogFactory;

    public MeController(IDbContextFactory<CatalogDbContext> catalogFactory) =>
        _catalogFactory = catalogFactory;

    /// <summary>
    /// Returns the list of organizations (tenants) the current user can access. Same user can be in multiple tenants.
    /// Client: after login, call this; if multiple, show picker and send selected tenant in X-Tenant-Id header for subsequent requests.
    /// </summary>
    [HttpGet("tenants")]
    [ProducesResponseType(typeof(MyTenantsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMyTenants(CancellationToken cancellationToken)
    {
        var email = GetCurrentUserEmail();
        if (string.IsNullOrEmpty(email))
            return Unauthorized();

        try
        {
            await using var context = await _catalogFactory.CreateDbContextAsync(cancellationToken);
            var items = await context.UserTenants
                .AsNoTracking()
                .Where(ut => ut.Email == email)
                .Join(
                    context.Tenants.Where(t => t.IsActive),
                    ut => ut.TenantId,
                    t => t.Id,
                    (ut, t) => new MyTenantItem(t.Id, t.Name, ut.Role))
                .ToListAsync(cancellationToken);

            return Ok(new MyTenantsResponse(items));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return Ok(new MyTenantsResponse(Array.Empty<MyTenantItem>()));
        }
    }

    private string? GetCurrentUserEmail()
    {
        var user = HttpContext.User;
        var email = user.FindFirst("email")?.Value
            ?? user.FindFirst("preferred_username")?.Value
            ?? user.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress")?.Value;
        return email?.Trim();
    }
}

/// <summary>List of organizations the current user can access.</summary>
public record MyTenantsResponse(IReadOnlyList<MyTenantItem> Tenants);

/// <summary>Organization (tenant) with user's role in it.</summary>
public record MyTenantItem(Guid TenantId, string Name, string Role);
