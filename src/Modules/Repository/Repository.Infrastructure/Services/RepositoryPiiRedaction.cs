using System.Text.Json;
using Npgsql;
using SaaSApp.Repository.Application.Contracts;

namespace SaaSApp.Repository.Infrastructure.Services;

internal static class RepositoryPiiRedaction
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static async Task EnsureColumnsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            ALTER TABLE repository."Repositories" ADD COLUMN IF NOT EXISTS "PiiRedactionEnabled" boolean NOT NULL DEFAULT false;
            ALTER TABLE repository."Repositories" ADD COLUMN IF NOT EXISTS "PiiRedactionFieldIds" text NULL;
            ALTER TABLE repository."Repositories" ADD COLUMN IF NOT EXISTS "PiiRedactionUsers" text NULL;
            ALTER TABLE repository."Repositories" ADD COLUMN IF NOT EXISTS "PiiRedactionLevel" varchar(32) NULL;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Each token may be a field id or a field name. Names match <paramref name="fields"/> (case-insensitive).
    /// </summary>
    public static IReadOnlyList<Guid> ResolveFieldIds(
        IReadOnlyList<string>? tokens,
        IEnumerable<(Guid Id, string Name)> fields)
    {
        var byName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            if (field.Id == Guid.Empty || string.IsNullOrWhiteSpace(field.Name))
                continue;
            byName.TryAdd(field.Name.Trim(), field.Id);
        }

        var resolved = new List<Guid>();
        var unknown = new List<string>();
        foreach (var raw in tokens ?? Array.Empty<string>())
        {
            var token = raw?.Trim();
            if (string.IsNullOrEmpty(token))
                continue;

            if (Guid.TryParse(token, out var id) && id != Guid.Empty)
            {
                resolved.Add(id);
                continue;
            }

            if (byName.TryGetValue(token, out var namedId))
                resolved.Add(namedId);
            else
                unknown.Add(token);
        }

        if (unknown.Count > 0)
        {
            throw new ArgumentException(
                "Unknown piiRedactionFieldIds: " + string.Join(", ", unknown) + ". Use a field id or a field name from fields.");
        }

        return resolved.Distinct().ToList();
    }

    public static string? NormalizeLevel(string? level)
    {
        if (string.IsNullOrWhiteSpace(level))
            return null;
        return level.Trim();
    }

    public static string SerializeFieldIds(IReadOnlyList<Guid>? fieldIds)
    {
        var ids = (fieldIds ?? Array.Empty<Guid>())
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        return JsonSerializer.Serialize(ids, JsonOptions);
    }

    public static string SerializeUsers(IReadOnlyList<PiiRedactionUserDto> users) =>
        JsonSerializer.Serialize(users, JsonOptions);

    public static IReadOnlyList<PiiRedactionUserDto> MergeUsers(
        IReadOnlyList<PiiRedactionUserDto>? users,
        IReadOnlyList<Guid>? userIds,
        IReadOnlyList<PiiRedactionUserDto>? existing)
    {
        var byId = new Dictionary<Guid, PiiRedactionUserDto>();
        if (users != null)
        {
            foreach (var user in users)
            {
                if (user.UserId == Guid.Empty)
                    continue;
                byId[user.UserId] = new PiiRedactionUserDto(user.UserId, user.Password ?? string.Empty);
            }
        }
        else if (existing != null)
        {
            foreach (var user in existing)
            {
                if (user.UserId != Guid.Empty)
                    byId[user.UserId] = user;
            }
        }

        if (userIds != null && users == null)
        {
            var kept = new Dictionary<Guid, PiiRedactionUserDto>();
            foreach (var id in userIds.Where(id => id != Guid.Empty).Distinct())
            {
                kept[id] = byId.TryGetValue(id, out var current)
                    ? current
                    : new PiiRedactionUserDto(id, string.Empty);
            }

            return kept.Values.ToList();
        }

        if (userIds != null)
        {
            foreach (var id in userIds.Where(id => id != Guid.Empty).Distinct())
            {
                if (!byId.ContainsKey(id))
                    byId[id] = new PiiRedactionUserDto(id, string.Empty);
            }
        }

        return byId.Values.ToList();
    }

    public static (bool Enabled, IReadOnlyList<Guid> FieldIds, IReadOnlyList<Guid> UserIds, IReadOnlyList<PiiRedactionUserDto> Users) Read(
        bool enabled,
        string? fieldIdsJson,
        string? usersJson)
    {
        var fieldIds = ReadGuidList(fieldIdsJson);
        var users = ReadUsers(usersJson);
        var userIds = users.Select(user => user.UserId).Distinct().ToArray();
        return (enabled, fieldIds, userIds, users);
    }

    public static IReadOnlyList<PiiRedactionUserDto> ReadUsers(string? usersJson)
    {
        if (string.IsNullOrWhiteSpace(usersJson))
            return Array.Empty<PiiRedactionUserDto>();

        try
        {
            var users = JsonSerializer.Deserialize<List<PiiRedactionUserDto>>(usersJson, JsonOptions);
            return users?.Where(user => user.UserId != Guid.Empty).ToArray()
                ?? Array.Empty<PiiRedactionUserDto>();
        }
        catch (JsonException)
        {
            return Array.Empty<PiiRedactionUserDto>();
        }
    }

    private static IReadOnlyList<Guid> ReadGuidList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<Guid>();

        try
        {
            var ids = JsonSerializer.Deserialize<List<Guid>>(json, JsonOptions);
            return ids?.Where(id => id != Guid.Empty).Distinct().ToArray() ?? Array.Empty<Guid>();
        }
        catch (JsonException)
        {
            return Array.Empty<Guid>();
        }
    }
}
