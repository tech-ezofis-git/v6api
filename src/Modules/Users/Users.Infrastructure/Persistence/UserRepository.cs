using Microsoft.EntityFrameworkCore;
using SaaSApp.Users.Application.Contracts;
using SaaSApp.Users.Domain.Entities;

namespace SaaSApp.Users.Infrastructure.Persistence;

public sealed class UserRepository : IUserRepository
{
    private readonly UsersDbContext _context;

    public UserRepository(UsersDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(User user, CancellationToken cancellationToken = default)
    {
        await _context.Users.AddAsync(user, cancellationToken);
    }

    public async Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<User>> GetByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
            return [];

        return await _context.Users
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToListAsync(cancellationToken);
    }

    public async Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        return await _context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email == email.Trim(), cancellationToken);
    }

    public async Task<User?> FindByEmailOrDisplayNameAsync(string emailOrDisplayName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(emailOrDisplayName))
            return null;

        var value = emailOrDisplayName.Trim();
        var byEmail = await GetByEmailAsync(value, cancellationToken);
        if (byEmail != null)
            return byEmail;

        return await _context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.DisplayName == value, cancellationToken);
    }

    public async Task<IReadOnlyList<User>> ListAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Users
            .AsNoTracking()
            .OrderBy(u => u.DisplayName)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> CountExistingByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
            return 0;

        return await _context.Users
            .AsNoTracking()
            .CountAsync(u => ids.Contains(u.Id), cancellationToken);
    }

    public async Task<IReadOnlyList<string>> RenameRoleForUsersAsync(
        string oldRoleName,
        string newRoleName,
        CancellationToken cancellationToken = default)
    {
        var oldNormalized = oldRoleName.Trim().ToLowerInvariant();
        var users = await _context.Users
            .Where(u => u.Role.ToLower() == oldNormalized)
            .ToListAsync(cancellationToken);

        foreach (var user in users)
            user.Update(role: newRoleName);

        return users.Select(u => u.Email).ToList();
    }

    public async Task<IReadOnlyList<(string Email, string Role)>> RemoveRoleNameFromUsersAsync(
        string roleName,
        CancellationToken cancellationToken = default)
    {
        var trimmed = roleName.Trim();
        if (trimmed.Length == 0)
            return [];

        // Broad filter; exact token match is applied in memory for comma-separated Role values.
        var candidates = await _context.Users
            .Where(u => u.Role == trimmed || u.Role.Contains(trimmed))
            .ToListAsync(cancellationToken);

        var updated = new List<(string Email, string Role)>();
        foreach (var user in candidates)
        {
            var remaining = user.Role
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(part => !string.Equals(part, trimmed, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var originalParts = user.Role
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToList();

            if (remaining.Count == originalParts.Count)
                continue;

            var newRole = remaining.Count == 0 ? string.Empty : string.Join(",", remaining);
            user.Update(role: newRole);
            updated.Add((user.Email, newRole));
        }

        return updated;
    }

    public void Update(User user)
    {
        _context.Users.Update(user);
    }

    public void Delete(User user)
    {
        _context.Users.Remove(user);
    }
}
