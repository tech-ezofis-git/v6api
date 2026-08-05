using SaaSApp.Repository.Application.Contracts;

namespace SaaSApp.Repository.Infrastructure;

/// <summary>Read-only timeline entries inferred from item columns (ingest source, OCR, AI validation).</summary>
internal static class RepositoryItemTimelineDeriver
{
    public static IReadOnlyList<RepositoryItemTimelineEventDto> Derive(
        IReadOnlyDictionary<string, object?> fields,
        string? createdByDisplayName = null)
    {
        var events = new List<RepositoryItemTimelineEventDto>();

        if (TryGetDate(fields, "CreatedAtUtc", out var createdAt))
        {
            var (title, actorType, actorName) = DescribeIngest(fields, createdByDisplayName);
            events.Add(new RepositoryItemTimelineEventDto(
                Guid.Empty,
                "system",
                title,
                null,
                actorType,
                actorName,
                createdAt,
                IsDerived: true));
        }

        if (TryGetByte(fields, "OcrScore", out var ocrScore) && TryGetDate(fields, "CreatedAtUtc", out var ocrAt))
        {
            events.Add(new RepositoryItemTimelineEventDto(
                Guid.Empty,
                "ai",
                $"OCR extraction complete — {ocrScore}% confidence",
                null,
                "AI Engine",
                "AI Engine",
                ocrAt.AddMinutes(1),
                IsDerived: true));
        }

        if (HasAiValidation(fields) && TryGetDate(fields, "CreatedAtUtc", out var aiAt))
        {
            var matched = GetString(fields, "MatchedStatus");
            var detail = string.IsNullOrWhiteSpace(matched) || matched.Equals("Clean", StringComparison.OrdinalIgnoreCase)
                ? "Metadata validated, no duplicates found"
                : $"Metadata validated, duplicates: {matched}";

            events.Add(new RepositoryItemTimelineEventDto(
                Guid.Empty,
                "ai",
                detail,
                null,
                "AI Engine",
                "AI Engine",
                aiAt.AddMinutes(2),
                IsDerived: true));
        }

        if (TryGetGuid(fields, "WorkflowInstanceId", out var instanceId)
            && TryGetDate(fields, "CreatedAtUtc", out var linkedAt))
        {
            events.Add(new RepositoryItemTimelineEventDto(
                Guid.Empty,
                "system",
                "File linked to workflow instance",
                instanceId.ToString("D"),
                "System",
                "System",
                linkedAt.AddSeconds(30),
                IsDerived: true));
        }

        return events.OrderBy(e => e.CreatedAtUtc).ToList();
    }

    private static (string Title, string ActorType, string ActorName) DescribeIngest(
        IReadOnlyDictionary<string, object?> fields,
        string? createdByDisplayName)
    {
        var source = (GetString(fields, "Source") ?? string.Empty).Trim();
        if (IsEmailSource(source))
        {
            return ("Document ingested via email", "System", "System (Email)");
        }

        var actor = string.IsNullOrWhiteSpace(createdByDisplayName) ? "System" : createdByDisplayName.Trim();
        return ("Document uploaded manually", "User", actor);
    }

    private static bool IsEmailSource(string source) =>
        source.Contains("mail", StringComparison.OrdinalIgnoreCase)
        || source.Contains("email", StringComparison.OrdinalIgnoreCase)
        || source.Contains("ingest", StringComparison.OrdinalIgnoreCase)
        || source.Equals("smtp", StringComparison.OrdinalIgnoreCase);

    private static bool HasAiValidation(IReadOnlyDictionary<string, object?> fields) =>
        fields.ContainsKey("AiStatus") && fields["AiStatus"] != null ||
        fields.ContainsKey("MatchedStatus") && fields["MatchedStatus"] != null;

    private static string? GetString(IReadOnlyDictionary<string, object?> fields, string key) =>
        fields.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static bool TryGetDate(IReadOnlyDictionary<string, object?> fields, string key, out DateTime value)
    {
        value = default;
        if (!fields.TryGetValue(key, out var raw) || raw == null)
            return false;

        if (raw is DateTime dt)
        {
            value = dt;
            return true;
        }

        return DateTime.TryParse(raw.ToString(), out value);
    }

    private static bool TryGetByte(IReadOnlyDictionary<string, object?> fields, string key, out byte value)
    {
        value = default;
        if (!fields.TryGetValue(key, out var raw) || raw == null)
            return false;

        if (raw is byte b)
        {
            value = b;
            return true;
        }

        return byte.TryParse(raw.ToString(), out value);
    }

    private static bool TryGetGuid(IReadOnlyDictionary<string, object?> fields, string key, out Guid value)
    {
        value = default;
        if (!fields.TryGetValue(key, out var raw) || raw == null)
            return false;

        if (raw is Guid g)
        {
            value = g;
            return g != Guid.Empty;
        }

        return Guid.TryParse(raw.ToString(), out value) && value != Guid.Empty;
    }
}
