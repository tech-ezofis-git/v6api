namespace SaaSApp.Api.Services;

/// <summary>Creates or verifies the AP Agent pilot user in a tenant database.</summary>
public interface ITenantPilotUserProvisioningService
{
    /// <summary>
    /// Ensures <see cref="TenantPilotUserOptions.Email"/> exists in the tenant DB and catalog registry.
    /// Returns the pilot user id, or null when pilot provisioning is disabled/misconfigured.
    /// </summary>
    Task<TenantPilotUserEnsureResult?> EnsurePilotUserAsync(
        Guid tenantId,
        string tenantConnectionString,
        string? skipIfSameEmailAs = null,
        CancellationToken cancellationToken = default);
}

public sealed record TenantPilotUserEnsureResult(
    Guid UserId,
    string Email,
    bool Created);
