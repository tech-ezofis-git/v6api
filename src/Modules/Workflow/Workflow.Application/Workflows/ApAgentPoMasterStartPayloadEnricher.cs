using System.Text.Json;
using SaaSApp.Workflow.Application.Connectors;

namespace SaaSApp.Workflow.Application.Workflows;

/// <summary>
/// When PO Master (email-ingest masterSource) is QuickBooks or SAP,
/// inject resource / connector_id / skills into the Hangfire start payload
/// so Python runs po_lookup_* before po_match.
/// InternalForm leaves the payload unchanged (default AP skills).
/// </summary>
public static class ApAgentPoMasterStartPayloadEnricher
{
    /// <summary>Matches orchestrator PHASE1_SKILL_ORDER.</summary>
    public static readonly string[] DefaultApSkills =
    [
        "extract_invoice",
        "po_match",
        "duplicate_detect",
        "vendor_validate",
        "backorder_detect",
        "finalize_decision",
        "workflow_move_next",
    ];

    public const string SkillPoLookupQuickBooks = "po_lookup_quickbooks";
    public const string SkillPoLookupSap = "po_lookup_sap";
    public const string SkillPoMatch = "po_match";

    public static void Enrich(
        IDictionary<string, object?> payload,
        string? masterSource,
        Guid? masterConnectorId)
    {
        if (payload is null)
            throw new ArgumentNullException(nameof(payload));

        var source = (masterSource ?? string.Empty).Trim();
        if (source.Length == 0)
            return;

        if (string.Equals(source, EmailIngestMasterSources.InternalForm, StringComparison.OrdinalIgnoreCase))
            return;

        if (string.Equals(source, EmailIngestMasterSources.QuickBooks, StringComparison.OrdinalIgnoreCase))
        {
            ApplyConnectorMaster(payload, "QUICKBOOKS", masterConnectorId, SkillPoLookupQuickBooks);
            return;
        }

        if (string.Equals(source, EmailIngestMasterSources.Sap, StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, "Sap", StringComparison.OrdinalIgnoreCase))
        {
            ApplyConnectorMaster(payload, "SAP", masterConnectorId, SkillPoLookupSap);
        }
    }

    /// <summary>Read masterSource / masterConnectorId from workflow instance Context JSON.</summary>
    public static void TryReadMasterFromContext(
        string? contextJson,
        out string? masterSource,
        out Guid? masterConnectorId)
    {
        masterSource = null;
        masterConnectorId = null;
        if (string.IsNullOrWhiteSpace(contextJson))
            return;

        try
        {
            using var doc = JsonDocument.Parse(contextJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return;
            var root = doc.RootElement;
            masterSource = ReadString(root, "masterSource", "MasterSource");
            masterConnectorId = ReadGuid(root, "masterConnectorId", "MasterConnectorId");
        }
        catch (JsonException)
        {
            // ignore malformed context
        }
    }

    private static void ApplyConnectorMaster(
        IDictionary<string, object?> payload,
        string resource,
        Guid? masterConnectorId,
        string lookupSkillId)
    {
        // Prefer snake_case keys Python DocumentPayload expects.
        if (!HasNonEmptyString(payload, "resource") && !HasNonEmptyString(payload, "Resource"))
            payload["resource"] = resource;

        if (masterConnectorId is { } cid && cid != Guid.Empty
            && !HasNonEmptyString(payload, "connector_id")
            && !HasNonEmptyString(payload, "connectorId"))
        {
            payload["connector_id"] = cid.ToString("D");
        }

        EnsureLookupSkillBeforePoMatch(payload, lookupSkillId);
    }

    private static void EnsureLookupSkillBeforePoMatch(
        IDictionary<string, object?> payload,
        string lookupSkillId)
    {
        var existing = ReadSkills(payload);
        List<string> skills;
        if (existing is { Count: > 0 })
        {
            skills = new List<string>(existing);
            if (skills.Contains(lookupSkillId, StringComparer.OrdinalIgnoreCase))
            {
                payload["skills"] = skills;
                return;
            }

            var poMatchIndex = skills.FindIndex(s =>
                string.Equals(s, SkillPoMatch, StringComparison.OrdinalIgnoreCase));
            if (poMatchIndex >= 0)
                skills.Insert(poMatchIndex, lookupSkillId);
            else
                skills.Add(lookupSkillId);
        }
        else
        {
            skills = new List<string>(DefaultApSkills);
            var poMatchIndex = skills.FindIndex(s =>
                string.Equals(s, SkillPoMatch, StringComparison.OrdinalIgnoreCase));
            if (poMatchIndex >= 0)
                skills.Insert(poMatchIndex, lookupSkillId);
            else
                skills.Insert(0, lookupSkillId);
        }

        payload["skills"] = skills;
    }

    private static List<string>? ReadSkills(IDictionary<string, object?> payload)
    {
        if (!payload.TryGetValue("skills", out var raw) || raw is null)
            return null;
        if (raw is IEnumerable<string> strings)
            return strings.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        if (raw is IEnumerable<object> objects)
        {
            return objects
                .Select(o => o?.ToString()?.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Cast<string>()
                .ToList();
        }
        if (raw is JsonElement el && el.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(s))
                        list.Add(s);
                }
            }
            return list;
        }
        return null;
    }

    private static bool HasNonEmptyString(IDictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var raw) || raw is null)
            return false;
        var text = raw.ToString()?.Trim();
        return !string.IsNullOrWhiteSpace(text);
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var el))
                continue;
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
            }
        }
        return null;
    }

    private static Guid? ReadGuid(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var el))
                continue;
            if (el.ValueKind == JsonValueKind.String
                && Guid.TryParse(el.GetString(), out var g)
                && g != Guid.Empty)
                return g;
        }
        return null;
    }
}
