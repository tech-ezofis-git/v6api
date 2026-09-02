using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SaaSApp.Api.Options;
using SaaSApp.Catalog.Persistence;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Infrastructure.Options;

namespace SaaSApp.Api.Services;

/// <summary>
/// Logs in as the tenant pilot user and caches JWT per tenant for AP Agent callbacks.
/// </summary>
public sealed class TenantPilotTokenService : IApAgentPilotAuthProvider
{
    private const int RefreshSkewSeconds = 120;

    private readonly TenantPilotUserOptions _pilotOptions;
    private readonly ApAgentOptions _apAgentOptions;
    private readonly IEzofisAuthService _authService;
    private readonly ITenantPilotUserProvisioningService _pilotProvisioning;
    private readonly IDbContextFactory<CatalogDbContext> _catalogFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TenantPilotTokenService> _logger;

    public TenantPilotTokenService(
        IOptions<TenantPilotUserOptions> pilotOptions,
        IOptions<ApAgentOptions> apAgentOptions,
        IEzofisAuthService authService,
        ITenantPilotUserProvisioningService pilotProvisioning,
        IDbContextFactory<CatalogDbContext> catalogFactory,
        IMemoryCache cache,
        ILogger<TenantPilotTokenService> logger)
    {
        _pilotOptions = pilotOptions.Value;
        _apAgentOptions = apAgentOptions.Value;
        _authService = authService;
        _pilotProvisioning = pilotProvisioning;
        _catalogFactory = catalogFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task<ApAgentPilotAuth?> GetAuthForTenantAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        if (!_pilotOptions.Enabled || !_apAgentOptions.IncludePilotAuthInPayload)
            return null;

        var pilotEmail = _pilotOptions.Email?.Trim();
        if (string.IsNullOrWhiteSpace(pilotEmail) || string.IsNullOrWhiteSpace(_pilotOptions.Password))
            return null;

        var cacheKey = $"pilot-jwt:{tenantId:D}";
        if (_cache.TryGetValue(cacheKey, out ApAgentPilotAuth? cached)
            && cached != null
            && !IsExpiringSoon(cacheKey))
        {
            return cached;
        }

        await using var catalog = await _catalogFactory.CreateDbContextAsync(cancellationToken);
        var tenant = await catalog.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId && t.IsActive)
            .Select(t => new { t.ConnectionString })
            .FirstOrDefaultAsync(cancellationToken);

        if (tenant == null || string.IsNullOrWhiteSpace(tenant.ConnectionString))
        {
            _logger.LogWarning("Cannot issue pilot token: tenant {TenantId} not found or inactive.", tenantId);
            return null;
        }

        var ensured = await _pilotProvisioning.EnsurePilotUserAsync(
            tenantId,
            tenant.ConnectionString,
            cancellationToken: cancellationToken);
        if (ensured == null)
            return null;

        var login = await _authService.LoginAsync(
            pilotEmail,
            _pilotOptions.Password,
            tenantId,
            cancellationToken);

        if (login is LoginRequiresTwoFactor)
        {
            throw new InvalidOperationException(
                $"Pilot user {pilotEmail} has 2FA enabled. Disable 2FA for the AP Agent service account.");
        }

        if (login is not LoginSuccess success)
        {
            _logger.LogWarning(
                "Pilot login failed for tenant {TenantId} ({Email}): {ResultType}",
                tenantId,
                pilotEmail,
                login.GetType().Name);
            return null;
        }

        var auth = new ApAgentPilotAuth(
            success.UserId,
            pilotEmail,
            success.AccessToken,
            success.ExpiresIn);

        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, success.ExpiresIn - RefreshSkewSeconds));
        _cache.Set(cacheKey, auth, expiresAt);
        _cache.Set($"{cacheKey}:exp", expiresAt.UtcDateTime, expiresAt);

        if (ensured.Created)
        {
            _logger.LogInformation(
                "Created pilot user {Email} ({UserId}) for tenant {TenantId}.",
                pilotEmail,
                ensured.UserId,
                tenantId);
        }

        return auth;
    }

    private bool IsExpiringSoon(string cacheKey)
    {
        if (!_cache.TryGetValue($"{cacheKey}:exp", out DateTime expiresAtUtc))
            return true;

        return expiresAtUtc <= DateTime.UtcNow.AddSeconds(RefreshSkewSeconds);
    }
}
