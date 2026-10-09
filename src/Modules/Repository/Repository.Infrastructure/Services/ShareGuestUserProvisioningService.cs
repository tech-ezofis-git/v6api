using Microsoft.EntityFrameworkCore;
using SaaSApp.Catalog;
using SaaSApp.MultiTenancy;
using SaaSApp.Repository.Application.Contracts;
using SaaSApp.Users.Domain.Entities;
using SaaSApp.Users.Infrastructure.Persistence;

namespace SaaSApp.Repository.Infrastructure.Services;

public sealed class ShareGuestUserProvisioningService : IShareGuestUserProvisioningService
{
    private static readonly string[] FirstTimeAuthMethods = ["password_setup", "google", "microsoft"];
    private static readonly string[] PasswordLoginOnly = ["password_login"];

    private readonly ITenantConnectionStringResolver _connectionResolver;
    private readonly IUserTenantRegistry _userTenantRegistry;

    public ShareGuestUserProvisioningService(
        ITenantConnectionStringResolver connectionResolver,
        IUserTenantRegistry userTenantRegistry)
    {
        _connectionResolver = connectionResolver;
        _userTenantRegistry = userTenantRegistry;
    }

    public async Task<ProvisionedGuestUser> EnsureGuestUserAsync(
        Guid tenantId,
        string email,
        bool externalOnly = false,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(email);
        await using var context = await CreateUsersContextAsync(tenantId, cancellationToken);

        var existing = await context.Users
            .FirstOrDefaultAsync(u => u.Email == normalizedEmail && !u.IsDeleted, cancellationToken);

        if (existing != null)
        {
            // Prior share/sign guests may have been saved with Configuration=0 and would hit onboard.
            // Only auto-complete for incomplete guests (no password / social yet), not full onboard users.
            if (existing.Configuration == 0
                && string.IsNullOrEmpty(existing.PasswordHash)
                && ResolveSocialProvider(existing) == null)
            {
                existing.MarkConfigurationCompleted();
            }

            // File/sign invites must not turn a new recipient into a tenant user.
            // Someone who already has a real account keeps that role.
            if (externalOnly && IsAutoCreatedTenantGuest(existing))
            {
                existing.Update(role: User.RoleExternalUser, userType: "External");
                await DetachTenantUserRoleAsync(context, existing.Id, cancellationToken);
                await AttachExternalRoleAsync(context, tenantId, existing.Id, cancellationToken);
            }

            await context.SaveChangesAsync(cancellationToken);
            await _userTenantRegistry.AddOrUpdateAsync(normalizedEmail, tenantId, existing.Role, existing.Id, cancellationToken);
            return new ProvisionedGuestUser(existing.Id, existing.Role);
        }

        var displayName = normalizedEmail.Split('@')[0];
        var roleName = externalOnly ? User.RoleExternalUser : User.RoleTenantUser;
        var user = User.Create(
            tenantId,
            normalizedEmail,
            displayName,
            roleName,
            authStrategy: User.AuthStrategyEzofis,
            userType: externalOnly ? "External" : null);
        user.SetLoginType("EZOFIS");
        // Invite guests skip tenant onboarding wizard (Configuration=0 would send them to onboard).
        user.MarkConfigurationCompleted();

        context.Users.Add(user);
        await context.SaveChangesAsync(cancellationToken);
        if (externalOnly)
            await AttachExternalRoleAsync(context, tenantId, user.Id, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await _userTenantRegistry.AddOrUpdateAsync(normalizedEmail, tenantId, roleName, user.Id, cancellationToken);
        return new ProvisionedGuestUser(user.Id, roleName);
    }

    /// <summary>
    /// True when this row was created by a share/sign invite and stored as TenantUser.
    /// A password, social login, or a filled profile means they are already a real user.
    /// </summary>
    private static bool IsAutoCreatedTenantGuest(User user)
    {
        if (!string.Equals(user.Role, User.RoleTenantUser, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(user.PasswordHash) || ResolveSocialProvider(user) != null)
            return false;
        if (!string.IsNullOrWhiteSpace(user.UserType)
            && !user.UserType.Equals("External", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(user.EmployeeId)
            || !string.IsNullOrWhiteSpace(user.Department)
            || !string.IsNullOrWhiteSpace(user.JobTitle))
            return false;

        var localPart = user.Email.Split('@')[0];
        return string.Equals(user.DisplayName?.Trim(), localPart, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task DetachTenantUserRoleAsync(
        UsersDbContext context,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var tenantRoleIds = await context.Roles
            .Where(r => r.Name == User.RoleTenantUser)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);
        if (tenantRoleIds.Count == 0)
            return;

        var links = await context.UserRoles
            .Where(ur => ur.UserId == userId && tenantRoleIds.Contains(ur.RoleId))
            .ToListAsync(cancellationToken);
        if (links.Count > 0)
            context.UserRoles.RemoveRange(links);
    }

    private static async Task AttachExternalRoleAsync(
        UsersDbContext context,
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var role = await context.Roles
            .Include(r => r.Permissions)
            .FirstOrDefaultAsync(r => r.Name == User.RoleExternalUser, cancellationToken);

        if (role == null)
        {
            role = Role.Create(
                tenantId,
                User.RoleExternalUser,
                "External share or sign recipient. Can open the shared file and repository only.");
            role.AssignPermissions(["folder"]);
            context.Roles.Add(role);
            await context.SaveChangesAsync(cancellationToken);
        }
        else
        {
            var folderOnly = role.Permissions.Count == 1
                && role.Permissions.Any(p => p.PermissionKey.Equals("folder", StringComparison.OrdinalIgnoreCase));
            if (!folderOnly)
            {
                role.ReplacePermissions(["folder"]);
                await context.SaveChangesAsync(cancellationToken);
            }
        }

        var linked = await context.UserRoles
            .AnyAsync(ur => ur.UserId == userId && ur.RoleId == role.Id, cancellationToken);
        if (!linked)
            context.UserRoles.Add(UserRole.Create(tenantId, role.Id, userId));
    }

    public async Task<bool> RequiresPasswordSetupAsync(
        Guid tenantId,
        string email,
        CancellationToken cancellationToken = default)
    {
        var info = await GetShareInviteAuthInfoAsync(tenantId, email, cancellationToken);
        return info.RequiresPasswordSetup;
    }

    public async Task<ShareInviteAuthInfo> GetShareInviteAuthInfoAsync(
        Guid tenantId,
        string email,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(email);
        await using var context = await CreateUsersContextAsync(tenantId, cancellationToken);
        var user = await context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email == normalizedEmail && !u.IsDeleted, cancellationToken);

        if (user == null)
        {
            return new ShareInviteAuthInfo(
                UserExists: false,
                RequiresPasswordSetup: true,
                RequiredSocialProvider: null,
                AllowedAuthMethods: FirstTimeAuthMethods,
                LoginType: "EZOFIS");
        }

        var socialProvider = ResolveSocialProvider(user);
        if (socialProvider != null)
        {
            return new ShareInviteAuthInfo(
                UserExists: true,
                RequiresPasswordSetup: false,
                RequiredSocialProvider: socialProvider,
                AllowedAuthMethods: [socialProvider],
                LoginType: user.LoginType);
        }

        if (!string.IsNullOrEmpty(user.PasswordHash))
        {
            return new ShareInviteAuthInfo(
                UserExists: true,
                RequiresPasswordSetup: false,
                RequiredSocialProvider: null,
                AllowedAuthMethods: PasswordLoginOnly,
                LoginType: string.IsNullOrWhiteSpace(user.LoginType) ? "EZOFIS" : user.LoginType);
        }

        // Share guest created at invite time — auth method not chosen yet; recipient picks on first sign-in.
        return new ShareInviteAuthInfo(
            UserExists: true,
            RequiresPasswordSetup: true,
            RequiredSocialProvider: null,
            AllowedAuthMethods: FirstTimeAuthMethods,
            LoginType: string.IsNullOrWhiteSpace(user.LoginType) ? "EZOFIS" : user.LoginType);
    }

    public async Task<Guid> ConfirmGuestSocialLoginAsync(
        Guid tenantId,
        string email,
        string provider,
        CancellationToken cancellationToken = default)
    {
        var normalizedProvider = NormalizeSocialProvider(provider)
            ?? throw new ArgumentException("Provider must be google or microsoft.");

        var normalizedEmail = NormalizeEmail(email);
        await using var context = await CreateUsersContextAsync(tenantId, cancellationToken);
        var user = await context.Users
            .FirstOrDefaultAsync(u => u.Email == normalizedEmail && !u.IsDeleted, cancellationToken);

        if (user == null)
            throw new InvalidOperationException("User not found for this share invite.");

        var existingProvider = ResolveSocialProvider(user);
        if (existingProvider != null)
        {
            if (!existingProvider.Equals(normalizedProvider, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"This account is linked to {existingProvider} sign-in.");
            }

            return user.Id;
        }

        if (!string.IsNullOrEmpty(user.PasswordHash))
            throw new InvalidOperationException("This account uses password login. Use Ezofis login instead.");

        ApplySocialLogin(user, normalizedProvider);
        if (user.Configuration == 0)
            user.MarkConfigurationCompleted();
        await context.SaveChangesAsync(cancellationToken);
        await _userTenantRegistry.AddOrUpdateAsync(normalizedEmail, tenantId, user.Role, user.Id, cancellationToken);
        return user.Id;
    }

    public async Task<bool> SetFirstPasswordAsync(
        Guid tenantId,
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Password is required.");

        var normalizedEmail = NormalizeEmail(email);
        await using var context = await CreateUsersContextAsync(tenantId, cancellationToken);
        var user = await context.Users
            .FirstOrDefaultAsync(u => u.Email == normalizedEmail && !u.IsDeleted, cancellationToken);

        if (user == null)
            return false;

        if (ResolveSocialProvider(user) != null)
            throw new InvalidOperationException("This account uses Google or Microsoft sign-in. Use social login instead.");

        if (!string.IsNullOrEmpty(user.PasswordHash))
            throw new InvalidOperationException("Password is already set. Use login instead.");

        user.SetPasswordHash(BCrypt.Net.BCrypt.HashPassword(password.Trim()));
        user.SetLoginType("EZOFIS");
        if (user.Configuration == 0)
            user.MarkConfigurationCompleted();
        await context.SaveChangesAsync(cancellationToken);
        await _userTenantRegistry.AddOrUpdateAsync(normalizedEmail, tenantId, user.Role, user.Id, cancellationToken);
        return true;
    }

    private async Task<UsersDbContext> CreateUsersContextAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var connectionString = await _connectionResolver.GetConnectionStringAsync(tenantId, cancellationToken);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Tenant connection string not found.");

        var optionsBuilder = new DbContextOptionsBuilder<UsersDbContext>();
        optionsBuilder.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.MigrationsHistoryTable("__EFMigrationsHistory", UsersDbContext.SchemaName);
            npgsql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(30), null);
        });

        return new UsersDbContext(optionsBuilder.Options, new StaticTenantProvider(tenantId));
    }

    private static string NormalizeEmail(string email)
    {
        var normalized = email?.Trim().ToLowerInvariant()
            ?? throw new ArgumentException("Email is required.");
        if (normalized.IndexOf('@') < 1)
            throw new ArgumentException("A valid email is required.");
        return normalized;
    }

    private static string? ResolveSocialProvider(User user)
    {
        var loginType = user.LoginType?.Trim() ?? string.Empty;
        var authStrategy = user.AuthStrategy?.Trim() ?? string.Empty;

        if (loginType.Equals("GOOGLE", StringComparison.OrdinalIgnoreCase)
            || loginType.Equals("Google", StringComparison.OrdinalIgnoreCase)
            || authStrategy.Equals(User.AuthStrategyGoogle, StringComparison.OrdinalIgnoreCase))
        {
            return "google";
        }

        if (loginType.Equals("MICROSOFT", StringComparison.OrdinalIgnoreCase)
            || loginType.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)
            || loginType.Equals("OFFICE365", StringComparison.OrdinalIgnoreCase)
            || loginType.Equals("Office365", StringComparison.OrdinalIgnoreCase)
            || authStrategy.Equals(User.AuthStrategyOffice365, StringComparison.OrdinalIgnoreCase))
        {
            return "microsoft";
        }

        return null;
    }

    private static string? NormalizeSocialProvider(string provider)
    {
        var value = provider.Trim();
        if (value.Equals("google", StringComparison.OrdinalIgnoreCase)
            || value.Equals("GOOGLE", StringComparison.OrdinalIgnoreCase))
        {
            return "google";
        }

        if (value.Equals("microsoft", StringComparison.OrdinalIgnoreCase)
            || value.Equals("MICROSOFT", StringComparison.OrdinalIgnoreCase)
            || value.Equals("office365", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Office365", StringComparison.OrdinalIgnoreCase))
        {
            return "microsoft";
        }

        return null;
    }

    private static void ApplySocialLogin(User user, string normalizedProvider)
    {
        if (normalizedProvider == "google")
        {
            user.SetLoginType("GOOGLE");
            return;
        }

        user.SetLoginType("MICROSOFT");
    }
}
