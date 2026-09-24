using Microsoft.EntityFrameworkCore;
using SaaSApp.Users.Application.Contracts;
using SaaSApp.Users.Domain;
using SaaSApp.Users.Domain.Entities;

namespace SaaSApp.Users.Infrastructure.Persistence;

public sealed class BuiltinRoleProvisioning : IBuiltinRoleProvisioning
{
    private readonly UsersDbContext _context;
    private readonly ITenantContext _tenantContext;

    public BuiltinRoleProvisioning(UsersDbContext context, ITenantContext tenantContext)
    {
        _context = context;
        _tenantContext = tenantContext;
    }

    public async Task EnsureBuiltinRolesAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantContext.TenantId
            ?? throw new InvalidOperationException("TenantId is required to ensure builtin roles.");

        await EnsureCoreAsync(_context, tenantId, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task SyncUserMembershipAsync(Guid userId, string? roleName, CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantContext.TenantId
            ?? throw new InvalidOperationException("TenantId is required to sync builtin role membership.");

        var (adminRole, tenantUserRole) = await EnsureRolesExistAsync(_context, tenantId, cancellationToken);
        await SyncOneUserAsync(_context, tenantId, userId, roleName, adminRole.Id, tenantUserRole.Id, cancellationToken);
    }

    /// <summary>Ensures builtin roles/permissions/membership for a tenant using an explicit DbContext (signup / middleware).</summary>
    public static async Task EnsureAsync(
        UsersDbContext context,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        await EnsureCoreAsync(context, tenantId, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }

    private static async Task EnsureCoreAsync(
        UsersDbContext context,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var (adminRole, tenantUserRole) = await EnsureRolesExistAsync(context, tenantId, cancellationToken);
        await UsersSchemaEnsurer.EnsureRoleMenusTablesAsync(context, cancellationToken);
        await EnsureRoleMenusAsync(context, tenantId, adminRole.Id, cancellationToken);
        await EnsureRoleMenusAsync(context, tenantId, tenantUserRole.Id, cancellationToken);
        await SyncAllMembershipsAsync(context, tenantId, adminRole.Id, tenantUserRole.Id, cancellationToken);
    }

    private static async Task<(Role Admin, Role TenantUser)> EnsureRolesExistAsync(
        UsersDbContext context,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var adminRole = await GetOrCreateBuiltinRoleAsync(
            context,
            tenantId,
            User.RoleAdmin,
            "Full system administrator.",
            cancellationToken);
        var tenantUserRole = await GetOrCreateBuiltinRoleAsync(
            context,
            tenantId,
            User.RoleTenantUser,
            "Standard tenant user.",
            cancellationToken);
        return (adminRole, tenantUserRole);
    }

    private static async Task<Role> GetOrCreateBuiltinRoleAsync(
        UsersDbContext context,
        Guid tenantId,
        string roleName,
        string description,
        CancellationToken cancellationToken)
    {
        var categoryKeys = await context.PermissionCategories
            .AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Key)
            .Select(c => c.Key)
            .ToListAsync(cancellationToken);

        // Always include folder, settings (configuration), and the other sidebar categories
        // even if the catalog query is empty on a brand-new tenant.
        foreach (var (_, key, _, _) in PermissionCategoryDefaults.All)
        {
            if (!categoryKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                categoryKeys.Add(key);
        }

        var existing = await context.Roles
            .Include(r => r.Permissions)
            .FirstOrDefaultAsync(r => r.Name == roleName, cancellationToken);

        if (existing != null)
        {
            // Backfill: Admin/TenantUser were only granted categories present at first create.
            // New categories (or empty create) left some options as Visible:false in the UI.
            EnsureAllCategoryPermissions(existing, categoryKeys);
            return existing;
        }

        var role = Role.Create(tenantId, roleName, description);
        if (categoryKeys.Count > 0)
            role.AssignPermissions(categoryKeys);

        await context.Roles.AddAsync(role, cancellationToken);
        return role;
    }

    /// <summary>Grant any missing active permission categories onto a builtin role (does not remove existing).</summary>
    private static void EnsureAllCategoryPermissions(Role role, IReadOnlyList<string> categoryKeys)
    {
        if (categoryKeys.Count == 0)
            return;

        var existing = new HashSet<string>(
            role.Permissions.Select(p => p.PermissionKey),
            StringComparer.OrdinalIgnoreCase);
        var missing = categoryKeys.Where(k => !existing.Contains(k)).ToList();
        if (missing.Count > 0)
            role.AssignPermissions(missing);
    }

    /// <summary>
    /// Link every sidebar menu onto the builtin role. Dashboard is the default landing page
    /// when the role has none yet. Does not remove menus an admin already customized.
    /// </summary>
    private static async Task EnsureRoleMenusAsync(
        UsersDbContext context,
        Guid tenantId,
        Guid roleId,
        CancellationToken cancellationToken)
    {
        var menus = await context.Menus
            .AsNoTracking()
            .OrderBy(m => m.SortOrder)
            .ThenBy(m => m.Key)
            .Select(m => new { m.Id, m.Key })
            .ToListAsync(cancellationToken);

        if (menus.Count == 0)
            return;

        var existing = await context.RoleMenus
            .Where(rm => rm.RoleId == roleId)
            .Select(rm => new { rm.MenuId, rm.IsDefaultLanding })
            .ToListAsync(cancellationToken);

        var existingIds = existing.Select(rm => rm.MenuId).ToHashSet();
        var hasDefault = existing.Any(rm => rm.IsDefaultLanding);

        foreach (var menu in menus)
        {
            if (!existingIds.Add(menu.Id))
                continue;

            var isDefault = !hasDefault
                && string.Equals(menu.Key, "dashboard", StringComparison.OrdinalIgnoreCase);
            if (isDefault)
                hasDefault = true;

            await context.RoleMenus.AddAsync(
                RoleMenu.Create(tenantId, roleId, menu.Id, isDefault),
                cancellationToken);
        }
    }

    private static async Task SyncAllMembershipsAsync(
        UsersDbContext context,
        Guid tenantId,
        Guid adminRoleId,
        Guid tenantUserRoleId,
        CancellationToken cancellationToken)
    {
        var adminUserIds = await context.Users
            .AsNoTracking()
            .Where(u => u.Role == User.RoleAdmin)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

        var tenantUserIds = await context.Users
            .AsNoTracking()
            .Where(u => u.Role == User.RoleTenantUser)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

        var existing = await context.UserRoles
            .Where(ur => ur.RoleId == adminRoleId || ur.RoleId == tenantUserRoleId)
            .ToListAsync(cancellationToken);

        var adminSet = adminUserIds.ToHashSet();
        var tenantUserSet = tenantUserIds.ToHashSet();

        foreach (var ur in existing)
        {
            var keep =
                (ur.RoleId == adminRoleId && adminSet.Contains(ur.UserId))
                || (ur.RoleId == tenantUserRoleId && tenantUserSet.Contains(ur.UserId));
            if (!keep)
                context.UserRoles.Remove(ur);
        }

        var existingPairs = existing
            .Select(ur => (ur.RoleId, ur.UserId))
            .ToHashSet();

        foreach (var userId in adminUserIds)
        {
            if (existingPairs.Add((adminRoleId, userId)))
                await context.UserRoles.AddAsync(UserRole.Create(tenantId, adminRoleId, userId), cancellationToken);
        }

        foreach (var userId in tenantUserIds)
        {
            if (existingPairs.Add((tenantUserRoleId, userId)))
                await context.UserRoles.AddAsync(UserRole.Create(tenantId, tenantUserRoleId, userId), cancellationToken);
        }
    }

    private static async Task SyncOneUserAsync(
        UsersDbContext context,
        Guid tenantId,
        Guid userId,
        string? roleName,
        Guid adminRoleId,
        Guid tenantUserRoleId,
        CancellationToken cancellationToken)
    {
        var trimmed = roleName?.Trim();
        var wantAdmin = string.Equals(trimmed, User.RoleAdmin, StringComparison.OrdinalIgnoreCase);
        var wantTenantUser = string.Equals(trimmed, User.RoleTenantUser, StringComparison.OrdinalIgnoreCase);

        var memberships = await context.UserRoles
            .Where(ur => ur.UserId == userId && (ur.RoleId == adminRoleId || ur.RoleId == tenantUserRoleId))
            .ToListAsync(cancellationToken);

        var hasAdmin = memberships.Any(ur => ur.RoleId == adminRoleId);
        var hasTenantUser = memberships.Any(ur => ur.RoleId == tenantUserRoleId);

        foreach (var ur in memberships)
        {
            if (ur.RoleId == adminRoleId && !wantAdmin)
                context.UserRoles.Remove(ur);
            else if (ur.RoleId == tenantUserRoleId && !wantTenantUser)
                context.UserRoles.Remove(ur);
        }

        if (wantAdmin && !hasAdmin)
            await context.UserRoles.AddAsync(UserRole.Create(tenantId, adminRoleId, userId), cancellationToken);

        if (wantTenantUser && !hasTenantUser)
            await context.UserRoles.AddAsync(UserRole.Create(tenantId, tenantUserRoleId, userId), cancellationToken);
    }
}
