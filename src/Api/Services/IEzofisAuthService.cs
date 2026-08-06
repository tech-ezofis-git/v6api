namespace SaaSApp.Api.Services;

/// <summary>Ezofis email/password authentication with optional 2FA.</summary>
public interface IEzofisAuthService
{
    /// <summary>Login with email and password. Returns JWT or LoginRequiresTwoFactor if 2FA enabled.</summary>
    Task<LoginResult> LoginAsync(string email, string password, Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Complete login after 2FA. Verifies TOTP code and returns JWT.</summary>
    Task<LoginResult> CompleteTwoFactorAsync(string tempToken, string code, CancellationToken cancellationToken = default);

    /// <summary>Social login (Google / Microsoft). Email + provider only; no password.</summary>
    Task<LoginResult> SocialLoginAsync(string email, string provider, Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// First-time password for a workflow/repository guest share invite (EZOFIS users only).
    /// Validates shareToken + email, sets password, returns JWT.
    /// </summary>
    Task<LoginResult> SetShareInvitePasswordAsync(
        string shareToken,
        string email,
        string password,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Social login for a share invite (Google / Microsoft). Validates shareToken + email + provider.
    /// No X-Tenant-Id required — tenant is resolved from the share token.
    /// </summary>
    Task<LoginResult> SetShareInviteSocialLoginAsync(
        string shareToken,
        string email,
        string provider,
        CancellationToken cancellationToken = default);

    /// <summary>First-time password for a repository sign-request invite. Tenant resolved from invite token.</summary>
    Task<LoginResult> SetSignInvitePasswordAsync(
        string inviteToken,
        string email,
        string password,
        CancellationToken cancellationToken = default);

    /// <summary>Social login for a repository sign-request invite. Tenant resolved from invite token.</summary>
    Task<LoginResult> SetSignInviteSocialLoginAsync(
        string inviteToken,
        string email,
        string provider,
        CancellationToken cancellationToken = default);
}

/// <summary>Result of Ezofis login: success, 2FA required, or first-time password setup.</summary>
public abstract record LoginResult;

/// <summary>Login succeeded. Use AccessToken in Authorization: Bearer header.</summary>
public sealed record LoginSuccess(Guid UserId, string AccessToken, string TokenType, int ExpiresIn) : LoginResult;

/// <summary>2FA required. Call POST /api/auth/2fa/complete with TempToken and TOTP code. Send X-Tenant-Id: TenantId.</summary>
public sealed record LoginRequiresTwoFactor(string TempToken, Guid TenantId, Guid UserId, int ExpiresInSeconds) : LoginResult;

/// <summary>Guest-invited user must set password first. Call POST /api/auth/share/set-password.</summary>
public sealed record LoginRequiresPasswordSetup(Guid TenantId, Guid UserId, string Email, string? ShareToken) : LoginResult;
