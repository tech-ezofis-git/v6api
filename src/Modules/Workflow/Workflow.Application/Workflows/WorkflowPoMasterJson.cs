using System.Text.Json;
using System.Text.Json.Nodes;

namespace SaaSApp.Workflow.Application.Workflows;

/// <summary>
/// PO Master binding stored on workflow designer JSON (Settings.PoMaster)
/// so DOCUMENT / DOCUMENT_FORM starts work without an EmailIngestMailbox.
/// </summary>
public static class WorkflowPoMasterJson
{
    public const string SettingsProperty = "Settings";
    public const string PoMasterProperty = "PoMaster";

    public static bool TryRead(
        string? workflowJson,
        out string? masterSource,
        out Guid? masterConnectorId,
        out string? masterFormId)
    {
        masterSource = null;
        masterConnectorId = null;
        masterFormId = null;
        if (string.IsNullOrWhiteSpace(workflowJson))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(workflowJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            if (!TryGetObject(doc.RootElement, out var settings, "Settings", "settings")
                || !TryGetObject(settings, out var poMaster, "PoMaster", "poMaster", "POMaster"))
            {
                // Also accept flat Settings.General keys used by some UIs.
                if (TryGetObject(doc.RootElement, out settings, "Settings", "settings")
                    && TryGetObject(settings, out var general, "General", "general"))
                {
                    masterSource = ReadString(general, "masterSource", "MasterSource");
                    masterFormId = ReadString(general, "masterFormId", "MasterFormId");
                    masterConnectorId = ReadGuid(general, "masterConnectorId", "MasterConnectorId");
                    return !string.IsNullOrWhiteSpace(masterSource) || masterConnectorId != null;
                }
                return false;
            }

            masterSource = ReadString(poMaster, "masterSource", "MasterSource");
            masterFormId = ReadString(poMaster, "masterFormId", "MasterFormId");
            masterConnectorId = ReadGuid(poMaster, "masterConnectorId", "MasterConnectorId");
            return !string.IsNullOrWhiteSpace(masterSource) || masterConnectorId != null || !string.IsNullOrWhiteSpace(masterFormId);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Merge PO master fields into workflow JSON; returns updated JSON string.</summary>
    public static string Upsert(string? existingJson, string? masterSource, Guid? masterConnectorId, string? masterFormId)
    {
        JsonObject root;
        if (string.IsNullOrWhiteSpace(existingJson))
        {
            root = new JsonObject();
        }
        else
        {
            try
            {
                root = JsonNode.Parse(existingJson) as JsonObject ?? new JsonObject();
            }
            catch (JsonException)
            {
                root = new JsonObject();
            }
        }

        var settings = root[SettingsProperty] as JsonObject
            ?? root["settings"] as JsonObject
            ?? new JsonObject();
        root[SettingsProperty] = settings;

        var poMaster = settings[PoMasterProperty] as JsonObject ?? new JsonObject();
        settings[PoMasterProperty] = poMaster;

        if (!string.IsNullOrWhiteSpace(masterSource))
            poMaster["masterSource"] = masterSource.Trim();
        if (masterConnectorId is { } cid && cid != Guid.Empty)
            poMaster["masterConnectorId"] = cid.ToString("D");
        if (masterFormId != null)
            poMaster["masterFormId"] = string.IsNullOrWhiteSpace(masterFormId) ? null : masterFormId.Trim();

        return root.ToJsonString();
    }

    private static bool TryGetObject(JsonElement parent, out JsonElement child, params string[] names)
    {
        foreach (var name in names)
        {
            if (parent.TryGetProperty(name, out child) && child.ValueKind == JsonValueKind.Object)
                return true;
        }
        child = default;
        return false;
    }

    private static string? ReadString(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
                continue;
            var s = el.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(s))
                return s;
        }
        return null;
    }

    private static Guid? ReadGuid(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var el))
                continue;
            if (el.ValueKind == JsonValueKind.String
                && Guid.TryParse(el.GetString(), out var g)
                && g != Guid.Empty)
                return g;
        }
        return null;
    }
}
