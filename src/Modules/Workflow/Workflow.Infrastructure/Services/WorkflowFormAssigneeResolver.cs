using System.Text.Json;
using Npgsql;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>
/// Resolves the next Manual User assignee from form fields such as First Approver / First_Approver
/// when the workflow step has no fixed AssignedToUserId.
/// </summary>
internal static class WorkflowFormAssigneeResolver
{
    private static readonly string[] ApproverLabelAliases =
    [
        "First Approver",
        "FirstApprover",
        "First_Approver",
        "first approver",
        "Next Approver",
        "NextApprover",
        "Next_Approver"
    ];

    private static readonly string[] ApproverColumnAliases =
    [
        "First_Approver",
        "FirstApprover",
        "first_approver",
        "Next_Approver",
        "NextApprover",
        "next_approver"
    ];

    public static async Task<Guid?> TryResolveFromFormAsync(
        NpgsqlConnection connection,
        string? formId,
        string? formDataJson,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(formDataJson))
            return null;

        Dictionary<string, string>? fields;
        try
        {
            fields = ParseFormFields(formDataJson);
        }
        catch (JsonException)
        {
            return null;
        }

        if (fields.Count == 0)
            return null;

        var candidateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in ApproverLabelAliases)
            candidateKeys.Add(alias);
        foreach (var alias in ApproverColumnAliases)
            candidateKeys.Add(alias);

        if (!string.IsNullOrWhiteSpace(formId))
        {
            foreach (var key in await LoadFirstApproverControlKeysAsync(connection, formId.Trim(), cancellationToken))
                candidateKeys.Add(key);
        }

        foreach (var key in candidateKeys)
        {
            if (!fields.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
                continue;

            var userId = await TryResolveUserIdAsync(connection, raw.Trim(), cancellationToken);
            if (userId is Guid id && id != Guid.Empty)
                return id;
        }

        // Fallback: any form key whose label-ish name contains "first" + "approver"
        foreach (var (key, raw) in fields)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            if (!LooksLikeFirstApproverKey(key))
                continue;

            var userId = await TryResolveUserIdAsync(connection, raw.Trim(), cancellationToken);
            if (userId is Guid id && id != Guid.Empty)
                return id;
        }

        return null;
    }

    private static bool LooksLikeFirstApproverKey(string key)
    {
        var normalized = key.Replace("_", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal)
            .Trim();
        return normalized.Contains("firstapprover", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("nextapprover", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ParseFormFields(string formDataJson)
    {
        using var doc = JsonDocument.Parse(formDataJson);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return map;

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var value = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                JsonValueKind.Number => prop.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => string.Empty,
                JsonValueKind.Object or JsonValueKind.Array => prop.Value.GetRawText(),
                _ => prop.Value.ToString()
            };
            if (!string.IsNullOrWhiteSpace(value))
                map[prop.Name] = value;
        }

        return map;
    }

    private static async Task<IReadOnlyList<string>> LoadFirstApproverControlKeysAsync(
        NpgsqlConnection connection,
        string formId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT "jsonId", name, "columnName"
            FROM dbo."wFormControl"
            WHERE "isDeleted" = false
              AND "wFormId" = @FormId
              AND (
                    LOWER(REPLACE(COALESCE("columnName", ''), ' ', '_')) IN
                        ('first_approver', 'firstapprover', 'next_approver', 'nextapprover')
                 OR LOWER(COALESCE(name, '')) IN
                        ('first approver', 'firstapprover', 'next approver', 'nextapprover')
                 OR LOWER(COALESCE(name, '')) LIKE '%first%approver%'
                 OR LOWER(COALESCE(name, '')) LIKE '%next%approver%'
              );
            """;

        var keys = new List<string>();
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@FormId", formId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            AddIfPresent(keys, reader.IsDBNull(0) ? null : reader.GetString(0));
            AddIfPresent(keys, reader.IsDBNull(1) ? null : reader.GetString(1));
            AddIfPresent(keys, reader.IsDBNull(2) ? null : reader.GetString(2));
        }

        return keys;
    }

    private static void AddIfPresent(ICollection<string> keys, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            keys.Add(value.Trim());
    }

    private static async Task<Guid?> TryResolveUserIdAsync(
        NpgsqlConnection connection,
        string raw,
        CancellationToken cancellationToken)
    {
        if (Guid.TryParse(raw, out var direct) && direct != Guid.Empty)
            return await UserExistsAsync(connection, direct, cancellationToken) ? direct : null;

        if (raw.StartsWith('{') || raw.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                    root = root[0];

                if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (var propName in new[] { "id", "Id", "userId", "UserId", "value", "Value" })
                    {
                        if (!root.TryGetProperty(propName, out var el))
                            continue;
                        var text = el.ValueKind == JsonValueKind.String
                            ? el.GetString()
                            : el.GetRawText().Trim('"');
                        if (Guid.TryParse(text, out var nested) && nested != Guid.Empty
                            && await UserExistsAsync(connection, nested, cancellationToken))
                            return nested;
                    }

                    foreach (var propName in new[] { "email", "Email", "userEmail", "UserEmail" })
                    {
                        if (!root.TryGetProperty(propName, out var el) || el.ValueKind != JsonValueKind.String)
                            continue;
                        var email = el.GetString();
                        if (!string.IsNullOrWhiteSpace(email))
                        {
                            var byEmail = await TryResolveUserIdByEmailAsync(connection, email.Trim(), cancellationToken);
                            if (byEmail.HasValue)
                                return byEmail;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // fall through
            }
        }

        if (raw.Contains('@', StringComparison.Ordinal))
            return await TryResolveUserIdByEmailAsync(connection, raw, cancellationToken);

        return null;
    }

    private static async Task<bool> UserExistsAsync(
        NpgsqlConnection connection,
        Guid userId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT 1
            FROM users."Users"
            WHERE "Id" = @UserId AND "IsDeleted" = false
            LIMIT 1;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@UserId", userId);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result != null && result != DBNull.Value;
    }

    private static async Task<Guid?> TryResolveUserIdByEmailAsync(
        NpgsqlConnection connection,
        string email,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT "Id"
            FROM users."Users"
            WHERE "IsDeleted" = false AND LOWER("Email") = LOWER(@Email)
            LIMIT 1;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Email", email.Trim());
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is Guid g && g != Guid.Empty ? g : null;
    }
}
