using SaaSApp.Users.Domain.Entities;

namespace SaaSApp.Users.Application.Contracts;

/// <summary>User persistence for the current tenant.</summary>
public interface IUserRepository
{
    /// <summary>Add a new user.</summary>
    Task AddAsync(User user, CancellationToken cancellationToken = default);

    /// <summary>Get user by ID.</summary>
    Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Get users by IDs (excluding soft-deleted).</summary>
    Task<IReadOnlyList<User>> GetByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default);

    /// <summary>Get user by email (case-sensitive, trimmed).</summary>
    Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>Find a user by email or display name (email match preferred).</summary>
    Task<User?> FindByEmailOrDisplayNameAsync(string emailOrDisplayName, CancellationToken cancellationToken = default);

    /// <summary>List all users (excluding soft-deleted) ordered by DisplayName.</summary>
    Task<IReadOnlyList<User>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Count how many of the given user IDs exist in the current tenant.</summary>
    Task<int> CountExistingByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default);

    /// <summary>Updates Users.Role from oldName to newName for users in the current tenant (case-insensitive match).</summary>
    Task<IReadOnlyList<string>> RenameRoleForUsersAsync(
        string oldRoleName,
        string newRoleName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes <paramref name="roleName"/> from comma-separated Users.Role values (case-insensitive).
    /// Leaves remaining roles joined by comma; sets empty string when none remain.
    /// Returns email + resulting Role for each updated user.
    /// </summary>
    Task<IReadOnlyList<(string Email, string Role)>> RemoveRoleNameFromUsersAsync(
        string roleName,
        CancellationToken cancellationToken = default);

    /// <summary>Mark user as modified for persistence.</summary>
    void Update(User user);

    /// <summary>Soft-delete user.</summary>
    void Delete(User user);
}
