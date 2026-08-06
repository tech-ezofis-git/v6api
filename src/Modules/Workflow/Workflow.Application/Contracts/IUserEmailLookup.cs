namespace SaaSApp.Workflow.Application.Contracts;

/// <summary>Resolves user GUIDs from users.Users on the tenant database.</summary>
public interface IUserEmailLookup
{
    Task<IReadOnlyDictionary<Guid, string>> GetEmailsAsync(
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, UserProfileLookupDto>> GetProfilesAsync(
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken = default);
}

public sealed record UserProfileLookupDto(
    string Email,
    string DisplayName,
    string? FirstName = null,
    string? LastName = null);
