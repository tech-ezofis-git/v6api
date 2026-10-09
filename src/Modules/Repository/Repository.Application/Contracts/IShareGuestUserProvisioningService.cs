namespace SaaSApp.Repository.Application.Contracts;

/// <summary>User row created or found for a share, sign, or workflow-inbox invite.</summary>
public sealed record ProvisionedGuestUser(Guid UserId, string Role);

/// <summary>
/// Provisions invited external users into a tenant for workflow inbox file shares.
/// Guest users are created without a password until they complete first-time setup.
/// </summary>
public interface IShareGuestUserProvisioningService
{
    /// <summary>
    /// Creates the user and catalog UserTenants row when missing. Idempotent.
    /// <paramref name="externalOnly"/> creates a file/sign recipient with repository access only.
    /// An email that already belongs to a real user keeps that user's role.
    /// </summary>
    Task<ProvisionedGuestUser> EnsureGuestUserAsync(
        Guid tenantId,
        string email,
        bool externalOnly = false,
        CancellationToken cancellationToken = default);

    /// <summary>True when the user exists in the tenant but has no password hash yet (EZOFIS only).</summary>
    Task<bool> RequiresPasswordSetupAsync(
        Guid tenantId,
        string email,
        CancellationToken cancellationToken = default);

    /// <summary>Resolves whether share invite needs password setup or Google/Microsoft social login.</summary>
    Task<ShareInviteAuthInfo> GetShareInviteAuthInfoAsync(
        Guid tenantId,
        string email,
        CancellationToken cancellationToken = default);

    /// <summary>Sets the first password for a guest-invited user (EZOFIS only).</summary>
    Task<bool> SetFirstPasswordAsync(
        Guid tenantId,
        string email,
        string password,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Locks a pending guest (no password yet) to Google or Microsoft on first social sign-in.
    /// </summary>
    Task<Guid> ConfirmGuestSocialLoginAsync(
        Guid tenantId,
        string email,
        string provider,
        CancellationToken cancellationToken = default);
}
