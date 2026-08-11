using Npgsql;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Infrastructure.Services;

public sealed class UserEmailLookup : IUserEmailLookup
{
    private readonly ITenantContext _tenantContext;

    public UserEmailLookup(ITenantContext tenantContext)
    {
        _tenantContext = tenantContext;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetEmailsAsync(
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken = default)
    {
        var profiles = await GetProfilesAsync(userIds, cancellationToken);
        return profiles.ToDictionary(kv => kv.Key, kv => kv.Value.Email, EqualityComparer<Guid>.Default);
    }

    public async Task<IReadOnlyDictionary<Guid, UserProfileLookupDto>> GetProfilesAsync(
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken = default)
    {
        var ids = userIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, UserProfileLookupDto>();

        var connectionString = _tenantContext.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        const string sql = """
            SELECT "Id", "Email", "DisplayName", "FirstName", "LastName"
            FROM users."Users"
            WHERE "IsDeleted" = false AND "Id" = ANY(@Ids);
            """;

        var map = new Dictionary<Guid, UserProfileLookupDto>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Ids", ids.ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetGuid(0);
            var email = reader.GetString(1);
            var displayName = reader.IsDBNull(2) ? email : reader.GetString(2);
            var firstName = reader.IsDBNull(3) ? null : reader.GetString(3);
            var lastName = reader.IsDBNull(4) ? null : reader.GetString(4);
            map[id] = new UserProfileLookupDto(email, displayName, firstName, lastName);
        }

        return map;
    }
}
