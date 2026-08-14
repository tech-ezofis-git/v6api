using System.Text.Json;

namespace SaaSApp.ActivityLog.Application;

/// <summary>Optional request-subject fields used to enrich EventTitle (never passwords).</summary>
public sealed class EventLogSubject
{
    public string? Email { get; init; }
    public string? Name { get; init; }
    public string? FileName { get; init; }
    public string? RoleName { get; init; }
    /// <summary>Assigned user role from update payloads (e.g. Admin).</summary>
    public string? Role { get; init; }
    public string? GroupName { get; init; }

    public static EventLogSubject Empty { get; } = new();

    public EventLogSubject With(
        string? email = null,
        string? name = null,
        string? fileName = null,
        string? roleName = null,
        string? role = null,
        string? groupName = null) =>
        new()
        {
            Email = First(email, Email),
            Name = First(name, Name),
            FileName = First(fileName, FileName),
            RoleName = First(roleName, RoleName),
            Role = First(role, Role),
            GroupName = First(groupName, GroupName)
        };

    public static EventLogSubject ParseAllowlistedJson(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.IsEmpty)
            return Empty;

        try
        {
            using var doc = JsonDocument.Parse(utf8Json.ToArray());
            return FromElement(doc.RootElement);
        }
        catch
        {
            return Empty;
        }
    }

    public static EventLogSubject ParseAllowlistedJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Empty;

        try
        {
            using var doc = JsonDocument.Parse(json);
            return FromElement(doc.RootElement);
        }
        catch
        {
            return Empty;
        }
    }

    private static EventLogSubject FromElement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return Empty;

        string? email = null;
        string? name = null;
        string? fileName = null;
        string? roleName = null;
        string? role = null;
        string? groupName = null;
        string? userName = null;
        string? firstName = null;
        string? lastName = null;

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.String)
            {
                if (string.Equals(prop.Name, "settings", StringComparison.OrdinalIgnoreCase)
                    && prop.Value.ValueKind == JsonValueKind.Object
                    && TryGetNestedName(prop.Value, out var nestedName))
                {
                    name ??= nestedName;
                }

                continue;
            }

            var value = prop.Value.GetString();
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var trimmed = value.Trim();

            if (prop.Name.Equals("email", StringComparison.OrdinalIgnoreCase))
                email ??= trimmed;
            else if (prop.Name.Equals("displayName", StringComparison.OrdinalIgnoreCase))
                name ??= trimmed;
            else if (prop.Name.Equals("name", StringComparison.OrdinalIgnoreCase))
                name ??= trimmed;
            else if (prop.Name.Equals("userName", StringComparison.OrdinalIgnoreCase))
                userName ??= trimmed;
            else if (prop.Name.Equals("firstName", StringComparison.OrdinalIgnoreCase))
                firstName ??= trimmed;
            else if (prop.Name.Equals("lastName", StringComparison.OrdinalIgnoreCase))
                lastName ??= trimmed;
            else if (prop.Name.Equals("fileName", StringComparison.OrdinalIgnoreCase))
                fileName ??= trimmed;
            else if (prop.Name.Equals("roleName", StringComparison.OrdinalIgnoreCase))
                roleName ??= trimmed;
            else if (prop.Name.Equals("role", StringComparison.OrdinalIgnoreCase))
                role ??= trimmed;
            else if (prop.Name.Equals("groupName", StringComparison.OrdinalIgnoreCase))
                groupName ??= trimmed;
        }

        if (name == null && !string.IsNullOrWhiteSpace(userName))
            name = userName;

        if (name == null && (!string.IsNullOrWhiteSpace(firstName) || !string.IsNullOrWhiteSpace(lastName)))
            name = string.Join(' ', new[] { firstName, lastName }.Where(s => !string.IsNullOrWhiteSpace(s)));

        if (name == null && TryGetNestedName(root, out var settingsName))
            name = settingsName;

        return new EventLogSubject
        {
            Email = email,
            Name = name,
            FileName = fileName,
            RoleName = roleName,
            Role = role,
            GroupName = groupName
        };
    }

    private static string? First(string? preferred, string? fallback) =>
        !string.IsNullOrWhiteSpace(preferred) ? preferred.Trim() : fallback;

    private static bool TryGetNestedName(JsonElement root, out string? name)
    {
        name = null;
        if (!TryGetPropertyIgnoreCase(root, "settings", out var settings)
            || settings.ValueKind != JsonValueKind.Object)
            return false;

        if (!TryGetPropertyIgnoreCase(settings, "general", out var general)
            || general.ValueKind != JsonValueKind.Object)
            return false;

        if (!TryGetPropertyIgnoreCase(general, "name", out var nameEl)
            || nameEl.ValueKind != JsonValueKind.String)
            return false;

        name = nameEl.GetString()?.Trim();
        return !string.IsNullOrWhiteSpace(name);
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
