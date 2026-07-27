using SaaSApp.SharedKernel.Domain;
using SaaSApp.SharedKernel.Domain.Interfaces;

namespace SaaSApp.Users.Domain.Entities;

/// <summary>Tenant-scoped custom role definition (separate from User.Role system access).</summary>
public sealed class Role : Entity<Guid>, ITenantEntity
{
    private readonly List<UserRole> _userRoles = [];
    private readonly List<RolePermission> _permissions = [];
    private readonly List<RoleMenu> _menus = [];

    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = null!;
    public string? Description { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public bool IsDeleted { get; private set; }

    public IReadOnlyCollection<UserRole> UserRoles => _userRoles.AsReadOnly();
    public IReadOnlyCollection<RolePermission> Permissions => _permissions.AsReadOnly();
    public IReadOnlyCollection<RoleMenu> Menus => _menus.AsReadOnly();

    private Role() { }

    private Role(Guid id, Guid tenantId, string name, string? description)
    {
        Id = id;
        TenantId = tenantId;
        Name = name;
        Description = description;
        CreatedAtUtc = DateTime.UtcNow;
    }

    public static bool IsReservedName(string name) =>
        string.Equals(name, User.RoleAdmin, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, User.RoleTenantUser, StringComparison.OrdinalIgnoreCase);

    public static Role Create(Guid tenantId, string name, string? description = null)
    {
        var trimmedName = name.Trim();
        return new Role(Guid.NewGuid(), tenantId, trimmedName, string.IsNullOrWhiteSpace(description) ? null : description.Trim());
    }

    public void AssignUsers(IEnumerable<Guid> userIds)
    {
        foreach (var userId in userIds.Distinct())
            _userRoles.Add(UserRole.Create(TenantId, Id, userId));
    }

    public void AssignPermissions(IEnumerable<string> permissionKeys)
    {
        foreach (var key in permissionKeys.Distinct(StringComparer.OrdinalIgnoreCase))
            _permissions.Add(RolePermission.Create(TenantId, Id, key));
    }

    public void Update(string name, string? description)
    {
        Name = name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
    }

    public void UpdateName(string name) => Name = name.Trim();

    public void UpdateDescription(string? description) =>
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();

    public void ReplaceUsers(IEnumerable<Guid> userIds)
    {
        _userRoles.Clear();
        AssignUsers(userIds);
    }

    public void ReplacePermissions(IEnumerable<string> permissionKeys)
    {
        _permissions.Clear();
        AssignPermissions(permissionKeys);
    }

    public void AssignMenus(IEnumerable<(Guid MenuId, bool IsDefaultLanding)> menus)
    {
        foreach (var (menuId, isDefaultLanding) in menus)
            _menus.Add(RoleMenu.Create(TenantId, Id, menuId, isDefaultLanding));
    }

    public void ReplaceMenus(IEnumerable<(Guid MenuId, bool IsDefaultLanding)> menus)
    {
        _menus.Clear();
        AssignMenus(menus);
    }

    public void SoftDelete() => IsDeleted = true;
}
