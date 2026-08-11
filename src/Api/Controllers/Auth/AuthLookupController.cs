using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SaaSApp.Catalog.Persistence;

namespace SaaSApp.Api.Controllers.Auth;

/// <summary>Unauthenticated auth lookups. Use on login page to show org picker before password.</summary>
[ApiController]
[Route("api/auth")]
[AllowAnonymous]
public sealed class AuthLookupController : ControllerBase
{
    private readonly IDbContextFactory<CatalogDbContext> _catalogFactory;

    public AuthLookupController(IDbContextFactory<CatalogDbContext> catalogFactory)
    {
        _catalogFactory = catalogFactory;
    }

    /// <summary>
    /// Returns organizations (tenants) for an email. Call this on the login page after user enters email.
    /// </summary>
    [HttpGet("tenants")]
    [ProducesResponseType(typeof(TenantLookupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetTenantsByEmail([FromQuery] string? email, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email))
            return BadRequest(new { error = "Email is required." });

        var normalizedEmail = email.Trim().ToLowerInvariant();

        try
        {
            await using var context = await _catalogFactory.CreateDbContextAsync(cancellationToken);
            var items = await (
                    from ut in context.UserTenants.AsNoTracking()
                    join t in context.Tenants.AsNoTracking() on ut.TenantId equals t.Id
                    where t.IsActive && ut.Email.ToLower() == normalizedEmail
                    orderby t.Name
                    select new TenantLookupItem(t.Id, t.Name, ut.Role))
                .ToListAsync(cancellationToken);

            return Ok(new TenantLookupResponse(
                items,
                RequiresOrgSelection: items.Count > 1));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return Ok(new TenantLookupResponse(Array.Empty<TenantLookupItem>(), RequiresOrgSelection: false));
        }
    }

    /// <summary>
    /// List emails registered in the catalog for a tenant (or all tenants if tenantId omitted).
    /// Used by playground "Choose an account" social login UI.
    /// </summary>
    [HttpGet("emails")]
    [ProducesResponseType(typeof(TenantEmailListResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListEmails(
        [FromQuery] Guid? tenantId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var context = await _catalogFactory.CreateDbContextAsync(cancellationToken);

            var query =
                from ut in context.UserTenants.AsNoTracking()
                join t in context.Tenants.AsNoTracking() on ut.TenantId equals t.Id
                where t.IsActive
                select new { ut.Email, ut.Role, ut.TenantId, TenantName = t.Name };

            if (tenantId.HasValue && tenantId.Value != Guid.Empty)
                query = query.Where(x => x.TenantId == tenantId.Value);

            var rows = await query
                .OrderBy(x => x.Email)
                .ToListAsync(cancellationToken);

            var emails = rows
                .GroupBy(r => (r.TenantId, Email: r.Email.Trim().ToLowerInvariant()))
                .Select(g =>
                {
                    var first = g.First();
                    var email = first.Email.Trim();
                    var local = email.Contains('@') ? email.Split('@')[0] : email;
                    var displayName = string.Join(' ', local.Split('.', '_', '-', ' ')
                        .Where(p => p.Length > 0)
                        .Select(p => char.ToUpperInvariant(p[0]) + (p.Length > 1 ? p[1..] : "")));
                    return new TenantEmailItem(
                        email,
                        displayName,
                        first.Role,
                        first.TenantId,
                        first.TenantName);
                })
                .OrderBy(e => e.Email, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Ok(new TenantEmailListResponse(tenantId, emails));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return Ok(new TenantEmailListResponse(tenantId, Array.Empty<TenantEmailItem>()));
        }
    }
}

public record TenantLookupResponse(
    IReadOnlyList<TenantLookupItem> Tenants,
    bool RequiresOrgSelection = false);

public record TenantLookupItem(Guid TenantId, string Name, string Role);

public record TenantEmailListResponse(Guid? TenantId, IReadOnlyList<TenantEmailItem> Emails);

public record TenantEmailItem(
    string Email,
    string DisplayName,
    string Role,
    Guid TenantId,
    string TenantName);
