using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using SaaSApp.Catalog.Persistence;
using SaaSApp.MultiTenancy;

namespace SaaSApp.Catalog;

public sealed class TenantConnectionStringResolver : ITenantConnectionStringResolver
{
    private readonly IDbContextFactory<CatalogDbContext> _catalogFactory;
    private readonly IMemoryCache _cache;
    private readonly int _tenantMaxPoolSize;

    public TenantConnectionStringResolver(
        IDbContextFactory<CatalogDbContext> catalogFactory,
        IMemoryCache cache,
        IConfiguration configuration)
    {
        _catalogFactory = catalogFactory;
        _cache = cache;
        _tenantMaxPoolSize = configuration.GetValue<int?>("TenantDatabase:MaxPoolSize")
            ?? TenantConnectionPool.MaxPoolSize;
    }

    public async Task<string?> GetConnectionStringAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (TenantConnectionStringCache.TryGet(_cache, tenantId, out var cached))
            return string.IsNullOrWhiteSpace(cached) ? cached : TenantConnectionPool.Apply(cached, _tenantMaxPoolSize);

        await using var context = await _catalogFactory.CreateDbContextAsync(cancellationToken);
        var tenant = await context.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId && t.IsActive)
            .Select(t => t.ConnectionString)
            .FirstOrDefaultAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(tenant))
        {
            tenant = TenantConnectionPool.Apply(tenant, _tenantMaxPoolSize);
            TenantConnectionStringCache.Set(_cache, tenantId, tenant);
        }

        return tenant;
    }
}
