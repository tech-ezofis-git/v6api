using Microsoft.EntityFrameworkCore;
using SaaSApp.Users.Application.Contracts;
using SaaSApp.Users.Domain.Entities;

namespace SaaSApp.Users.Infrastructure.Persistence;

public sealed class RoleRepository : IRoleRepository
{
    private readonly UsersDbContext _context;

    public RoleRepository(UsersDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(Role role, CancellationToken cancellationToken = default)
    {
        await _context.Roles.AddAsync(role, cancellationToken);
    }

    public async Task<bool> ExistsByNameAsync(string name, Guid? excludeRoleId = null, CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        var query = _context.Roles.AsNoTracking().Where(r => r.Name.Trim() == trimmed);
        if (excludeRoleId != null)
            query = query.Where(r => r.Id != excludeRoleId.Value);

        return await query.AnyAsync(cancellationToken);
    }

    public async Task<Role?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Roles
            .Include(r => r.UserRoles)
            .Include(r => r.Permissions)
            .Include(r => r.Menus)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    }

    public async Task<RoleDetailItem?> GetDetailByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Roles
            .AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => new RoleDetailItem(
                r.Id,
                r.Name,
                r.Description,
                r.CreatedAtUtc,
                r.UserRoles.Select(ur => ur.UserId).ToList(),
                r.Permissions.Select(p => p.PermissionKey).OrderBy(k => k).ToList()))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RoleListItem>> ListAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Roles
            .AsNoTracking()
            .OrderBy(r => r.Name)
            .Select(r => new RoleListItem(
                r.Id,
                r.Name,
                r.Description,
                r.UserRoles.Count,
                r.Permissions.Count))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListPermissionKeysForUserAsync(
        Guid userId,
        string? userRoleName,
        CancellationToken cancellationToken = default)
    {
        var keysFromAssignments = _context.UserRoles
            .AsNoTracking()
            .Where(ur => ur.UserId == userId)
            .Join(
                _context.RolePermissions,
                ur => ur.RoleId,
                rp => rp.RoleId,
                (ur, rp) => rp.PermissionKey);

        IQueryable<string> query = keysFromAssignments;

        if (!string.IsNullOrWhiteSpace(userRoleName))
        {
            // Users.Role may be a single custom name ("FTL User") or comma-separated names.
            var roleNames = userRoleName
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (roleNames.Length > 0)
            {
                var keysFromRoleName = _context.Roles
                    .AsNoTracking()
                    .Where(r => roleNames.Contains(r.Name))
                    .SelectMany(r => r.Permissions.Select(p => p.PermissionKey));

                query = keysFromAssignments.Union(keysFromRoleName);
            }
        }

        return await query.Distinct().OrderBy(k => k).ToListAsync(cancellationToken);
    }
}
