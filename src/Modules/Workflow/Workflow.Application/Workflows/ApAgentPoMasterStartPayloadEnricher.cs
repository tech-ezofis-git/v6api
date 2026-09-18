using System.Text.Json;
using SaaSApp.Workflow.Application.Connectors;

namespace SaaSApp.Workflow.Application.Workflows;

/// <summary>
/// When Workflow PO Master is configured, stamp the Hangfire/AP start payload so
/// Agents validate only what Workflow says (no tenant GUID hardcodes).
/// InternalForm → master_source + master_form_id (ezfb /masters/po).
/// QuickBooks / SAP / HANA → master_source + resource + connector_id + po_lookup_* before po_match.
/// </summary>
public static class ApAgentPoMasterStartPayloadEnricher
{
    /// <summary>Matches orchestrator DEFAULT_SKILL_ORDER.</summary>
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
        Guid? masterConnectorId,
        string? masterFormId = null)
    {
        if (payload is null)
            throw new ArgumentNullException(nameof(payload));

        var source = (masterSource ?? string.Empty).Trim();
        var formId = (masterFormId ?? string.Empty).Trim();

        // Form-only binding with empty source → treat as InternalForm.
        if (source.Length == 0 && formId.Length > 0)
            source = EmailIngestMasterSources.InternalForm;

        if (source.Length == 0)
            return;

        if (string.Equals(source, EmailIngestMasterSources.InternalForm, StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, "Ezofis", StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, "Form", StringComparison.OrdinalIgnoreCase))
        {
            ApplyInternalFormMaster(payload, formId);
            return;
        }

        // Always stamp master_source for connector masters (Agents gate on it).
        SetIfMissing(payload, "master_source", source);
        SetIfMissing(payload, "masterSource", source);

        if (string.Equals(source, EmailIngestMasterSources.QuickBooks, StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, "QB", StringComparison.OrdinalIgnoreCase))
        {
            ApplyConnectorMaster(payload, "QUICKBOOKS", masterConnectorId, SkillPoLookupQuickBooks);
            return;
        }

        if (IsSapOrHanaSource(source))
        {
            var resource = source.Contains("HANA", StringComparison.OrdinalIgnoreCase) ? "HANA" : "SAP";
            ApplyConnectorMaster(payload, resource, masterConnectorId, SkillPoLookupSap);
        }
    }

    /// <summary>Read masterSource / masterConnectorId / masterFormId from workflow instance Context JSON.</summary>
    public static void TryReadMasterFromContext(
        string? contextJson,
        out string? masterSource,
        out Guid? masterConnectorId,
        out string? masterFormId)
    {
        masterSource = null;
        masterConnectorId = null;
        masterFormId = null;
        if (string.IsNullOrWhiteSpace(contextJson))
            return;

        try
        {
            using var doc = JsonDocument.Parse(contextJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return;
            var root = doc.RootElement;
            masterSource = ReadString(root, "masterSource", "MasterSource", "master_source");
            masterFormId = ReadString(root, "masterFormId", "MasterFormId", "master_form_id");
            masterConnectorId = ReadGuid(root, "masterConnectorId", "MasterConnectorId", "master_connector_id");
        }
        catch (JsonException)
        {
            // ignore malformed context
        }
    }

    /// <summary>Backward-compatible overload (ignores masterFormId).</summary>
    public static void TryReadMasterFromContext(
        string? contextJson,
        out string? masterSource,
        out Guid? masterConnectorId)
        => TryReadMasterFromContext(contextJson, out masterSource, out masterConnectorId, out _);

    private static bool IsSapOrHanaSource(string source)
    {
        if (string.Equals(source, EmailIngestMasterSources.Sap, StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, "Sap", StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, "HANA", StringComparison.OrdinalIgnoreCase))
            return true;
        var compact = source.Replace(" ", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal);
        return compact.Contains("HANA", StringComparison.OrdinalIgnoreCase)
            || compact.StartsWith("SAP", StringComparison.OrdinalIgnoreCase)
            || string.Equals(compact, "S4", StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyInternalFormMaster(
        IDictionary<string, object?> payload,
        string? masterFormId)
    {
        if (!HasNonEmptyString(payload, "master_source")
            && !HasNonEmptyString(payload, "masterSource")
            && !HasNonEmptyString(payload, "MasterSource"))
        {
            payload["master_source"] = EmailIngestMasterSources.InternalForm;
        }

        var formId = (masterFormId ?? string.Empty).Trim();
        if (formId.Length == 0)
            return;
        if (!HasNonEmptyString(payload, "master_form_id")
            && !HasNonEmptyString(payload, "masterFormId")
            && !HasNonEmptyString(payload, "MasterFormId"))
        {
            payload["master_form_id"] = formId;
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

    private static void SetIfMissing(IDictionary<string, object?> payload, string key, string value)
    {
        if (!HasNonEmptyString(payload, key))
            payload[key] = value;
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
