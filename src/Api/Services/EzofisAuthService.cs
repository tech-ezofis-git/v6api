using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BCrypt.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using SaaSApp.Repository.Application.Contracts;
using SaaSApp.Users.Application.Contracts;
using SaaSApp.Users.Infrastructure.Persistence;

namespace SaaSApp.Api.Services;

/// <summary>Ezofis email/password login with BCrypt and optional 2FA. Issues JWT when successful.</summary>
public sealed class EzofisAuthService : IEzofisAuthService
{
    private const string PendingLoginCacheKeyPrefix = "2fa_login_";
    private static readonly TimeSpan PendingLoginExpiry = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AccessTokenExpiry = TimeSpan.FromDays(1);

    private readonly IUserRepository _userRepository;
    private readonly IShareGuestUserProvisioningService _guestProvisioning;
    private readonly IRepositoryItemShareService _shareService;
    private readonly IRepositorySignRequestService _signRequestService;
    private readonly ITwoFactorService _twoFactorService;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _configuration;

    public EzofisAuthService(
        IUserRepository userRepository,
        IShareGuestUserProvisioningService guestProvisioning,
        IRepositoryItemShareService shareService,
        IRepositorySignRequestService signRequestService,
        ITwoFactorService twoFactorService,
        IMemoryCache cache,
        IConfiguration configuration)
    {
        _userRepository = userRepository;
        _guestProvisioning = guestProvisioning;
        _shareService = shareService;
        _signRequestService = signRequestService;
        _twoFactorService = twoFactorService;
        _cache = cache;
        _configuration = configuration;
    }

    public async Task<LoginResult> LoginAsync(string email, string password, Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email is required.");

        var user = await _userRepository.GetByEmailAsync(email.Trim(), cancellationToken);
        if (user == null)
            throw new UnauthorizedAccessException("Invalid email or password.");

        if (user.TenantId != tenantId)
            throw new UnauthorizedAccessException("Invalid email or password.");

        if (string.IsNullOrEmpty(user.PasswordHash))
        {
            if (!IsEzofisPasswordUser(user))
                throw new UnauthorizedAccessException($"You should login with {DescribeExpectedProvider(user)}.");

            return new LoginRequiresPasswordSetup(
                tenantId,
                user.Id,
                user.Email,
                ShareToken: null);
        }

        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Password is required.");

        try
        {
            if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
                throw new UnauthorizedAccessException("Invalid email or password.");
        }
        catch (SaltParseException)
        {
            throw new UnauthorizedAccessException("Invalid email or password.");
        }

        if (user.TwoFactorAuthentication)
        {
            var tempToken = Guid.NewGuid().ToString("N");
            _cache.Set(PendingLoginCacheKeyPrefix + tempToken, (user.Id.ToString(), tenantId), PendingLoginExpiry);
            return new LoginRequiresTwoFactor(tempToken, tenantId, user.Id, (int)PendingLoginExpiry.TotalSeconds);
        }

        return BuildLoginSuccess(user, tenantId);
    }

    public async Task<LoginResult> SetShareInvitePasswordAsync(
        string shareToken,
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(shareToken))
            throw new ArgumentException("ShareToken is required.");
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email is required.");
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Password is required.");

        var preview = await _shareService.GetPreviewAsync(shareToken.Trim(), cancellationToken)
            ?? throw new UnauthorizedAccessException("Invalid or expired share link.");

        if (!preview.AutoProvisionGuest)
            throw new UnauthorizedAccessException("This share link does not support guest password setup.");

        if (!string.Equals(preview.RecipientEmail, email.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Email does not match this share invite.");

        var tenantId = preview.SourceTenantId;
        var authInfo = await _guestProvisioning.GetShareInviteAuthInfoAsync(tenantId, email.Trim(), cancellationToken);
        if (authInfo.RequiredSocialProvider != null)
        {
            throw new UnauthorizedAccessException(
                $"This account uses {authInfo.RequiredSocialProvider} sign-in. Use POST /api/auth/share/social-login instead.");
        }

        if (!authInfo.AllowedAuthMethods.Contains("password_setup", StringComparer.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Password setup is not available for this account. Use login or social sign-in.");
        }

        var userId = await _guestProvisioning.EnsureGuestUserAsync(tenantId, email.Trim(), cancellationToken);

        var updated = await _guestProvisioning.SetFirstPasswordAsync(tenantId, email.Trim(), password, cancellationToken);
        if (!updated)
            throw new UnauthorizedAccessException("Unable to set password for this invite.");

        return new LoginSuccess(
            userId,
            GenerateJwt(userId, email.Trim().ToLowerInvariant(), email.Trim(), SaaSApp.Users.Domain.Entities.User.RoleTenantUser, tenantId),
            "Bearer",
            (int)AccessTokenExpiry.TotalSeconds);
    }

    public async Task<LoginResult> SetShareInviteSocialLoginAsync(
        string shareToken,
        string email,
        string provider,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(shareToken))
            throw new ArgumentException("ShareToken is required.");
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email is required.");
        if (string.IsNullOrWhiteSpace(provider))
            throw new ArgumentException("Provider is required (google or microsoft).");

        var preview = await _shareService.GetPreviewAsync(shareToken.Trim(), cancellationToken)
            ?? throw new UnauthorizedAccessException("Invalid or expired share link.");

        if (!preview.AutoProvisionGuest)
            throw new UnauthorizedAccessException("This share link does not support guest social login.");

        if (!string.Equals(preview.RecipientEmail, email.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Email does not match this share invite.");

        var tenantId = preview.SourceTenantId;
        var authInfo = await _guestProvisioning.GetShareInviteAuthInfoAsync(tenantId, email.Trim(), cancellationToken);
        var normalizedProvider = provider.Trim();
        if (authInfo.RequiredSocialProvider != null
            && !authInfo.RequiredSocialProvider.Equals(normalizedProvider, StringComparison.OrdinalIgnoreCase)
            && !authInfo.RequiredSocialProvider.Equals(
                normalizedProvider.Equals("office365", StringComparison.OrdinalIgnoreCase) ? "microsoft" : normalizedProvider,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"This account must sign in with {authInfo.RequiredSocialProvider}.");
        }

        if (!authInfo.AllowedAuthMethods.Any(m =>
                m.Equals(normalizedProvider, StringComparison.OrdinalIgnoreCase)
                || (m.Equals("microsoft", StringComparison.OrdinalIgnoreCase)
                    && normalizedProvider.Equals("office365", StringComparison.OrdinalIgnoreCase))))
        {
            throw new UnauthorizedAccessException("Social sign-in is not available for this share invite.");
        }

        await _guestProvisioning.EnsureGuestUserAsync(tenantId, email.Trim(), cancellationToken);
        await _guestProvisioning.ConfirmGuestSocialLoginAsync(tenantId, email.Trim(), provider, cancellationToken);

        return await SocialLoginAsync(email.Trim(), provider, tenantId, cancellationToken);
    }

    public async Task<LoginResult> SetSignInvitePasswordAsync(
        string inviteToken,
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(inviteToken))
            throw new ArgumentException("InviteToken is required.");
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email is required.");
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Password is required.");

        var preview = await _signRequestService.GetInvitePreviewAsync(inviteToken.Trim(), cancellationToken)
            ?? throw new UnauthorizedAccessException("Invalid or expired sign invite.");

        if (!string.Equals(preview.RecipientEmail, email.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Email does not match this sign invite.");

        var tenantId = preview.TenantId;
        var authInfo = await _guestProvisioning.GetShareInviteAuthInfoAsync(tenantId, email.Trim(), cancellationToken);
        if (authInfo.RequiredSocialProvider != null)
        {
            throw new UnauthorizedAccessException(
                $"This account uses {authInfo.RequiredSocialProvider} sign-in. Use POST /api/auth/sign-request/social-login instead.");
        }

        if (!authInfo.AllowedAuthMethods.Contains("password_setup", StringComparer.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Password setup is not available for this account. Use login or social sign-in.");

        var userId = await _guestProvisioning.EnsureGuestUserAsync(tenantId, email.Trim(), cancellationToken);
        var updated = await _guestProvisioning.SetFirstPasswordAsync(tenantId, email.Trim(), password, cancellationToken);
        if (!updated)
            throw new UnauthorizedAccessException("Unable to set password for this invite.");

        return new LoginSuccess(
            userId,
            GenerateJwt(userId, email.Trim().ToLowerInvariant(), email.Trim(), SaaSApp.Users.Domain.Entities.User.RoleTenantUser, tenantId),
            "Bearer",
            (int)AccessTokenExpiry.TotalSeconds);
    }

    public async Task<LoginResult> SetSignInviteSocialLoginAsync(
        string inviteToken,
        string email,
        string provider,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(inviteToken))
            throw new ArgumentException("InviteToken is required.");
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email is required.");
        if (string.IsNullOrWhiteSpace(provider))
            throw new ArgumentException("Provider is required (google or microsoft).");

        var preview = await _signRequestService.GetInvitePreviewAsync(inviteToken.Trim(), cancellationToken)
            ?? throw new UnauthorizedAccessException("Invalid or expired sign invite.");

        if (!string.Equals(preview.RecipientEmail, email.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Email does not match this sign invite.");

        var tenantId = preview.TenantId;
        var authInfo = await _guestProvisioning.GetShareInviteAuthInfoAsync(tenantId, email.Trim(), cancellationToken);
        var normalizedProvider = provider.Trim();
        if (authInfo.RequiredSocialProvider != null
            && !authInfo.RequiredSocialProvider.Equals(normalizedProvider, StringComparison.OrdinalIgnoreCase)
            && !authInfo.RequiredSocialProvider.Equals(
                normalizedProvider.Equals("office365", StringComparison.OrdinalIgnoreCase) ? "microsoft" : normalizedProvider,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"This account must sign in with {authInfo.RequiredSocialProvider}.");
        }

        if (!authInfo.AllowedAuthMethods.Any(m =>
                m.Equals(normalizedProvider, StringComparison.OrdinalIgnoreCase)
                || (m.Equals("microsoft", StringComparison.OrdinalIgnoreCase)
                    && normalizedProvider.Equals("office365", StringComparison.OrdinalIgnoreCase))))
        {
            throw new UnauthorizedAccessException("Social sign-in is not available for this sign invite.");
        }

        await _guestProvisioning.EnsureGuestUserAsync(tenantId, email.Trim(), cancellationToken);
        await _guestProvisioning.ConfirmGuestSocialLoginAsync(tenantId, email.Trim(), provider, cancellationToken);
        return await SocialLoginAsync(email.Trim(), provider, tenantId, cancellationToken);
    }

    public async Task<LoginResult> CompleteTwoFactorAsync(string tempToken, string code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(tempToken) || string.IsNullOrEmpty(code))
            throw new ArgumentException("TempToken and code are required.");

        var cached = _cache.Get<(string UserId, Guid TenantId)>(PendingLoginCacheKeyPrefix + tempToken);
        if (cached == default)
            throw new UnauthorizedAccessException("Session expired. Please login again.");

        if (!await _twoFactorService.VerifyAsync(cached.UserId, code, cancellationToken))
            throw new UnauthorizedAccessException("Invalid verification code.");

        _cache.Remove(PendingLoginCacheKeyPrefix + tempToken);

        var user = await _userRepository.GetByIdAsync(Guid.Parse(cached.UserId), cancellationToken);
        if (user == null)
            throw new UnauthorizedAccessException("User not found.");

        return BuildLoginSuccess(user, cached.TenantId);
    }

    public async Task<LoginResult> SocialLoginAsync(
        string email,
        string provider,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email is required.");

        if (string.IsNullOrWhiteSpace(provider))
            throw new ArgumentException("Provider is required (google or microsoft).");

        var normalizedProvider = NormalizeSocialProvider(provider);
        if (normalizedProvider is null)
            throw new ArgumentException("Provider must be google or microsoft.");

        var user = await _userRepository.GetByEmailAsync(email.Trim(), cancellationToken);
        if (user == null)
            throw new UnauthorizedAccessException("Invalid email or provider.");

        if (user.TenantId != tenantId)
            throw new UnauthorizedAccessException("Invalid email or provider.");

        if (IsEzofisPasswordUser(user))
            throw new UnauthorizedAccessException("You should login with password.");

        if (!MatchesSocialProvider(user, normalizedProvider))
            throw new UnauthorizedAccessException($"You should login with {DescribeExpectedProvider(user)}.");

        return BuildLoginSuccess(user, tenantId);
    }

    private LoginSuccess BuildLoginSuccess(SaaSApp.Users.Domain.Entities.User user, Guid tenantId)
    {
        var safeEmail = user.Email?.Trim();
        if (string.IsNullOrWhiteSpace(safeEmail))
            throw new InvalidOperationException("User email is missing.");

        var safeDisplayName = string.IsNullOrWhiteSpace(user.DisplayName) ? safeEmail : user.DisplayName;
        var safeRole = string.IsNullOrWhiteSpace(user.Role) ? "User" : user.Role;
        if (safeRole.Equals("Administrator", StringComparison.OrdinalIgnoreCase))
            safeRole = "Admin";

        return new LoginSuccess(
            user.Id,
            GenerateJwt(user.Id, safeEmail, safeDisplayName, safeRole, tenantId),
            "Bearer",
            (int)AccessTokenExpiry.TotalSeconds);
    }

    private static string? NormalizeSocialProvider(string provider)
    {
        var value = provider.Trim();
        if (value.Equals("google", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("GOOGLE", StringComparison.OrdinalIgnoreCase))
            return "google";

        if (value.Equals("microsoft", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("MICROSOFT", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("office365", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Office365", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("OFFICE365", StringComparison.OrdinalIgnoreCase))
            return "microsoft";

        return null;
    }

    private static bool IsEzofisPasswordUser(SaaSApp.Users.Domain.Entities.User user)
    {
        var loginType = user.LoginType?.Trim();
        var authStrategy = user.AuthStrategy?.Trim();

        var isEzofisLoginType = string.IsNullOrWhiteSpace(loginType) ||
            loginType.Equals("EZOFIS", StringComparison.OrdinalIgnoreCase) ||
            loginType.Equals("Ezofis", StringComparison.OrdinalIgnoreCase);

        var isEzofisStrategy = string.IsNullOrWhiteSpace(authStrategy) ||
            authStrategy.Equals(SaaSApp.Users.Domain.Entities.User.AuthStrategyEzofis, StringComparison.OrdinalIgnoreCase);

        return isEzofisLoginType && isEzofisStrategy;
    }

    private static bool MatchesSocialProvider(SaaSApp.Users.Domain.Entities.User user, string normalizedProvider)
    {
        var loginType = user.LoginType?.Trim() ?? string.Empty;
        var authStrategy = user.AuthStrategy?.Trim() ?? string.Empty;

        if (normalizedProvider == "google")
        {
            return loginType.Equals("GOOGLE", StringComparison.OrdinalIgnoreCase) ||
                   loginType.Equals("Google", StringComparison.OrdinalIgnoreCase) ||
                   authStrategy.Equals(SaaSApp.Users.Domain.Entities.User.AuthStrategyGoogle, StringComparison.OrdinalIgnoreCase);
        }

        return loginType.Equals("MICROSOFT", StringComparison.OrdinalIgnoreCase) ||
               loginType.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) ||
               loginType.Equals("OFFICE365", StringComparison.OrdinalIgnoreCase) ||
               loginType.Equals("Office365", StringComparison.OrdinalIgnoreCase) ||
               authStrategy.Equals(SaaSApp.Users.Domain.Entities.User.AuthStrategyOffice365, StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeExpectedProvider(SaaSApp.Users.Domain.Entities.User user)
    {
        if (!string.IsNullOrWhiteSpace(user.LoginType))
            return user.LoginType;

        if (user.AuthStrategy?.Equals(SaaSApp.Users.Domain.Entities.User.AuthStrategyGoogle, StringComparison.OrdinalIgnoreCase) == true)
            return "GOOGLE";

        if (user.AuthStrategy?.Equals(SaaSApp.Users.Domain.Entities.User.AuthStrategyOffice365, StringComparison.OrdinalIgnoreCase) == true)
            return "MICROSOFT";

        return user.AuthStrategy ?? "social provider";
    }

    private string GenerateJwt(Guid userId, string email, string displayName, string role, Guid tenantId)
    {
        var key = _configuration["EzofisAuth:SigningKey"];
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException("EzofisAuth:SigningKey not configured.");

        var keyBytes = Encoding.UTF8.GetBytes(key);
        if (keyBytes.Length < 32)
            throw new InvalidOperationException("EzofisAuth:SigningKey must be at least 32 characters.");

        var signingKey = new SymmetricSecurityKey(keyBytes);
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var issuer = _configuration["EzofisAuth:Issuer"] ?? "Ezofis";
        var audience = _configuration["EzofisAuth:Audience"] ?? "Ezofis";

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, email),
            new Claim("name", displayName),
            new Claim("role", role),
            new Claim("tid", tenantId.ToString())
        };

        var token = new JwtSecurityToken(
            issuer,
            audience,
            claims,
            expires: DateTime.UtcNow.Add(AccessTokenExpiry),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
