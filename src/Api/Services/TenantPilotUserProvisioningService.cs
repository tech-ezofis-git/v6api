using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SaaSApp.Api.Options;
using SaaSApp.Catalog;
using SaaSApp.MultiTenancy;
using SaaSApp.Users.Domain.Entities;
using SaaSApp.Users.Infrastructure.Persistence;

namespace SaaSApp.Api.Services;

public sealed class TenantPilotUserProvisioningService : ITenantPilotUserProvisioningService
{
    private readonly TenantPilotUserOptions _options;
    private readonly IUserTenantRegistry _userTenantRegistry;

    public TenantPilotUserProvisioningService(
        IOptions<TenantPilotUserOptions> options,
        IUserTenantRegistry userTenantRegistry)
    {
        _options = options.Value;
        _userTenantRegistry = userTenantRegistry;
    }

    public async Task<TenantPilotUserEnsureResult?> EnsurePilotUserAsync(
        Guid tenantId,
        string tenantConnectionString,
        string? skipIfSameEmailAs = null,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return null;

        var pilotEmail = _options.Email?.Trim();
        if (string.IsNullOrWhiteSpace(pilotEmail))
            return null;

        if (string.IsNullOrWhiteSpace(_options.Password))
            return null;

        if (!string.IsNullOrWhiteSpace(skipIfSameEmailAs)
            && string.Equals(pilotEmail, skipIfSameEmailAs.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var displayName = string.IsNullOrWhiteSpace(_options.DisplayName)
            ? "AP Agent Pilot"
            : _options.DisplayName.Trim();
        var role = string.IsNullOrWhiteSpace(_options.Role)
            ? User.RoleTenantUser
            : _options.Role.Trim();

        var (userId, created) = await EnsureTenantUserAsync(
            tenantConnectionString,
            tenantId,
            pilotEmail,
            displayName,
            _options.Password,
            role,
            "EZOFIS",
            firstName: "AP Agent",
            lastName: "Pilot",
            cancellationToken);

        await _userTenantRegistry.AddOrUpdateAsync(pilotEmail, tenantId, role, userId, cancellationToken);

        return new TenantPilotUserEnsureResult(userId, pilotEmail, created);
    }

    internal static async Task<(Guid UserId, bool Created)> EnsureTenantUserAsync(
        string tenantConnectionString,
        Guid tenantId,
        string email,
        string displayName,
        string password,
        string role,
        string loginType,
        string? firstName,
        string? lastName,
        CancellationToken cancellationToken)
    {
        var optionsBuilder = new DbContextOptionsBuilder<UsersDbContext>();
        optionsBuilder.UseNpgsql(tenantConnectionString, npgsql =>
        {
            npgsql.MigrationsHistoryTable("__EFMigrationsHistory", UsersDbContext.SchemaName);
            npgsql.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(30),
                errorCodesToAdd: null);
        });
        var tenantProvider = new StaticTenantProvider(tenantId);
        await using var context = new UsersDbContext(optionsBuilder.Options, tenantProvider);
        await UsersSchemaEnsurer.EnsureExtendedUserColumnsAsync(context, cancellationToken);

        var normalizedEmail = email.Trim();
        var existing = await context.Users
            .FirstOrDefaultAsync(u => u.Email == normalizedEmail && !u.IsDeleted, cancellationToken);
        if (existing != null)
        {
            var hash = BCrypt.Net.BCrypt.HashPassword(password.Trim());
            existing.SetPasswordHash(hash);
            existing.SetLoginType(loginType);
            await context.SaveChangesAsync(cancellationToken);
            return (existing.Id, false);
        }

        var user = User.Create(
            tenantId,
            normalizedEmail,
            displayName,
            role,
            firstName?.Trim(),
            lastName?.Trim(),
            User.AuthStrategyEzofis);
        user.SetLoginType(loginType);
        user.SetPasswordHash(BCrypt.Net.BCrypt.HashPassword(password.Trim()));

        context.Users.Add(user);
        await context.SaveChangesAsync(cancellationToken);
        return (user.Id, true);
    }
}
