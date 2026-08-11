using Npgsql;

namespace SaaSApp.Repository.Infrastructure;

internal static class RepositoryUserNameResolver
{
    public static async Task<string?> ResolveEmailAsync(
        NpgsqlConnection connection,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var profile = await ResolveProfileAsync(connection, userId, cancellationToken);
        return profile?.Email;
    }

    public static async Task<(string? Email, string? DisplayName)?> ResolveProfileAsync(
        NpgsqlConnection connection,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
            return null;

        const string sql = """
            SELECT "Email", "DisplayName"
            FROM users."Users"
            WHERE "Id" = @UserId AND "IsDeleted" = false;
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@UserId", userId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var email = reader.IsDBNull(0) ? null : reader.GetString(0).Trim();
        var displayName = reader.IsDBNull(1) ? null : reader.GetString(1).Trim();
        return (email, displayName);
    }

    public static async Task<Dictionary<Guid, string>> ResolveDisplayNamesAsync(
        NpgsqlConnection connection,
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken = default)
    {
        var profiles = await ResolveProfilesAsync(connection, userIds, cancellationToken);
        var map = new Dictionary<Guid, string>();
        foreach (var (id, profile) in profiles)
            map[id] = PreferEmail(profile.Email, profile.DisplayName, id);
        return map;
    }

    public static async Task<Dictionary<Guid, (string? Email, string? DisplayName)>> ResolveProfilesAsync(
        NpgsqlConnection connection,
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken = default)
    {
        var ids = userIds.Where(id => id != Guid.Empty).Distinct().ToList();
        var map = new Dictionary<Guid, (string? Email, string? DisplayName)>();
        if (ids.Count == 0)
            return map;

        const string sql = """
            SELECT "Id", "Email", "DisplayName"
            FROM users."Users"
            WHERE "IsDeleted" = false
              AND "Id" = ANY(@Ids);
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Ids", ids.ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetGuid(0);
            var email = reader.IsDBNull(1) ? null : reader.GetString(1).Trim();
            var displayName = reader.IsDBNull(2) ? null : reader.GetString(2).Trim();
            map[id] = (email, displayName);
        }

        return map;
    }

    /// <summary>Prefer email for FE actor labels; fall back to display name then GUID.</summary>
    public static string PreferEmail(string? email, string? displayName, Guid? userId = null)
    {
        if (!string.IsNullOrWhiteSpace(email))
            return email.Trim();
        if (!string.IsNullOrWhiteSpace(displayName))
            return displayName.Trim();
        return userId is { } id && id != Guid.Empty ? id.ToString("D") : "Unknown";
    }

    [Obsolete("Use PreferEmail for timeline/comments actor labels.")]
    public static string PreferDisplayName(string? displayName, string? email, Guid? userId = null) =>
        PreferEmail(email, displayName, userId);

    public static bool TryParseUserId(object? value, out Guid userId)
    {
        userId = Guid.Empty;
        if (value == null)
            return false;

        if (value is Guid guid)
        {
            userId = guid;
            return userId != Guid.Empty;
        }

        return Guid.TryParse(value.ToString(), out userId) && userId != Guid.Empty;
    }
}
