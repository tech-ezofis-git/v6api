using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SaaSApp.Api.Services;
using SaaSApp.Catalog.Entities;
using SaaSApp.Catalog.Persistence;
using SaaSApp.Security;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Api.Controllers.Admin;

/// <summary>Admin tenant management. Create DB first, then register tenant in catalog.</summary>
[ApiController]
[Route("api/admin/[controller]")]
[Authorize(Policy = AuthorizationPolicies.Admin)]
public sealed class TenantsController : ControllerBase
{
    private readonly IDbContextFactory<CatalogDbContext> _catalogFactory;
    private readonly IEzfbEntryIdMigrationService _ezfbEntryIdMigration;
    private readonly ITenantPilotUserProvisioningService _pilotUserProvisioning;

    public TenantsController(
        IDbContextFactory<CatalogDbContext> catalogFactory,
        IEzfbEntryIdMigrationService ezfbEntryIdMigration,
        ITenantPilotUserProvisioningService pilotUserProvisioning)
    {
        _catalogFactory = catalogFactory;
        _ezfbEntryIdMigration = ezfbEntryIdMigration;
        _pilotUserProvisioning = pilotUserProvisioning;
    }

    /// <summary>
    /// Register a tenant in the catalog (database-per-tenant). Create the tenant DB and run Users migration first, then call this.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register([FromBody] RegisterTenantRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.ConnectionString))
            return BadRequest(new { error = "Name and ConnectionString are required." });

        await using var context = await _catalogFactory.CreateDbContextAsync(cancellationToken);
        var exists = await context.Tenants.AnyAsync(t => t.Id == request.Id, cancellationToken);
        if (exists)
            return Conflict(new { error = "Tenant already registered with this Id." });

        var tenant = new Tenant
        {
            Id = request.Id,
            Name = request.Name.Trim(),
            ConnectionString = request.ConnectionString.Trim(),
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };
        context.Tenants.Add(tenant);
        await context.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(Get), new { id = tenant.Id }, new { id = tenant.Id, name = tenant.Name });
    }

    /// <summary>Get tenant by ID from catalog.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        await using var context = await _catalogFactory.CreateDbContextAsync(cancellationToken);
        var tenant = await context.Tenants
            .AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => new { t.Id, t.Name, t.IsActive, t.CreatedAtUtc })
            .FirstOrDefaultAsync(cancellationToken);
        if (tenant == null)
            return NotFound();
        return Ok(tenant);
    }

    /// <summary>
    /// Migrate legacy integer ezfb item_id and workflow form_entry_id values to uuid for an existing tenant DB.
    /// Run once per tenant before using the Guid formEntryId API on live data.
    /// </summary>
    [HttpPost("{id:guid}/migrate-ezfb-entry-ids")]
    [ProducesResponseType(typeof(EzfbEntryIdMigrationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MigrateEzfbEntryIds(Guid id, CancellationToken cancellationToken)
    {
        await using var context = await _catalogFactory.CreateDbContextAsync(cancellationToken);
        var tenant = await context.Tenants
            .AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => new { t.ConnectionString })
            .FirstOrDefaultAsync(cancellationToken);
        if (tenant == null || string.IsNullOrWhiteSpace(tenant.ConnectionString))
            return NotFound();

        var result = await _ezfbEntryIdMigration.MigrateTenantAsync(tenant.ConnectionString, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Ensure AP Agent pilot user (<c>pilot@ezofis.com</c>) exists in the tenant DB and catalog.
    /// Idempotent; syncs password from <c>TenantPilotUser:Password</c> when user already exists.
    /// </summary>
    [HttpPost("{id:guid}/ensure-pilot-user")]
    [ProducesResponseType(typeof(TenantPilotUserEnsureResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> EnsurePilotUser(Guid id, CancellationToken cancellationToken)
    {
        await using var context = await _catalogFactory.CreateDbContextAsync(cancellationToken);
        var tenant = await context.Tenants
            .AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => new { t.ConnectionString })
            .FirstOrDefaultAsync(cancellationToken);
        if (tenant == null || string.IsNullOrWhiteSpace(tenant.ConnectionString))
            return NotFound();

        var result = await _pilotUserProvisioning.EnsurePilotUserAsync(
            id,
            tenant.ConnectionString,
            cancellationToken: cancellationToken);
        if (result == null)
            return BadRequest(new { error = "Pilot user provisioning is disabled or TenantPilotUser:Password is not configured." });

        return Ok(result);
    }

    /// <summary>Ensure pilot user for every active tenant in the catalog.</summary>
    [HttpPost("ensure-pilot-user-all")]
    [ProducesResponseType(typeof(EnsurePilotUserAllResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> EnsurePilotUserAll(CancellationToken cancellationToken)
    {
        await using var context = await _catalogFactory.CreateDbContextAsync(cancellationToken);
        var tenants = await context.Tenants
            .AsNoTracking()
            .Where(t => t.IsActive && t.ConnectionString != null && t.ConnectionString != "")
            .Select(t => new { t.Id, t.ConnectionString })
            .ToListAsync(cancellationToken);

        var results = new List<TenantPilotUserEnsureResult>(tenants.Count);
        foreach (var tenant in tenants)
        {
            var ensured = await _pilotUserProvisioning.EnsurePilotUserAsync(
                tenant.Id,
                tenant.ConnectionString!,
                cancellationToken: cancellationToken);
            if (ensured != null)
                results.Add(ensured);
        }

        return Ok(new EnsurePilotUserAllResult(tenants.Count, results.Count, results));
    }
}

public sealed record EnsurePilotUserAllResult(
    int TenantCount,
    int EnsuredCount,
    IReadOnlyList<TenantPilotUserEnsureResult> Results);

/// <summary>Request to register a tenant in the catalog. Create DB and run migrations first.</summary>
public record RegisterTenantRequest(Guid Id, string Name, string ConnectionString);
