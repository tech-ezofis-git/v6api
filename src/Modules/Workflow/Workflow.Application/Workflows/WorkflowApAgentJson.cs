using System.Text.Json;
using SaaSApp.Workflow.Application.Connectors;

namespace SaaSApp.Workflow.Application.Workflows;

/// <summary>
/// Reads AP Agent settings from workflow designer JSON
/// (<c>Blocks[].Settings.apAgent</c> / <c>poMasterSourceType</c> / <c>formId</c>).
/// </summary>
public static class WorkflowApAgentJson
{
    /// <summary>
    /// Returns the first non-empty <c>apAgent.connectorId</c> found on any block.
    /// </summary>
    public static bool TryReadConnectorId(string? workflowJson, out Guid connectorId)
    {
        connectorId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(workflowJson))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(workflowJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            if (!TryGetArray(doc.RootElement, out var blocks, "Blocks", "blocks"))
                return false;

            foreach (var block in blocks.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object)
                    continue;
                if (!TryGetObject(block, out var settings, "Settings", "settings"))
                    continue;
                if (!TryGetObject(settings, out var apAgent, "apAgent", "ApAgent"))
                    continue;

                var id = ReadGuid(apAgent, "connectorId", "ConnectorId", "connector_id");
                if (id is { } cid && cid != Guid.Empty)
                {
                    connectorId = cid;
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Reads PO Master binding from the first AP_AGENT block:
    /// <c>settings.poMasterSourceType</c>, <c>settings.formId</c> / <c>apAgent.formId</c>,
    /// and <c>settings.connectorId</c> / <c>apAgent.connectorId</c>.
    /// Prefer this over the workflow invoice <c>formId</c> when stamping Hangfire
    /// <c>master_form_id</c>.
    /// </summary>
    public static bool TryReadPoMaster(
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

            if (!TryGetArray(doc.RootElement, out var blocks, "Blocks", "blocks"))
                return false;

            foreach (var block in blocks.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object)
                    continue;
                if (!IsApAgentBlock(block))
                    continue;
                if (!TryGetObject(block, out var settings, "Settings", "settings"))
                    continue;

                JsonElement? apAgent = null;
                if (TryGetObject(settings, out var apAgentObj, "apAgent", "ApAgent"))
                    apAgent = apAgentObj;

                var sourceRaw = ReadString(
                    settings,
                    "poMasterSourceType",
                    "PoMasterSourceType",
                    "masterSource",
                    "MasterSource");
                if (string.IsNullOrWhiteSpace(sourceRaw) && apAgent is { } apForSource)
                {
                    sourceRaw = ReadString(
                        apForSource,
                        "poMasterSourceType",
                        "PoMasterSourceType",
                        "masterSource",
                        "MasterSource",
                        "resource",
                        "Resource");
                }

                var formId = ReadString(settings, "formId", "FormId", "masterFormId", "MasterFormId");
                if (string.IsNullOrWhiteSpace(formId) && apAgent is { } apForForm)
                    formId = ReadString(apForForm, "formId", "FormId", "masterFormId", "MasterFormId");

                var connectorId = ReadGuid(settings, "connectorId", "ConnectorId", "connector_id", "masterConnectorId");
                if ((connectorId is null || connectorId == Guid.Empty) && apAgent is { } apForConn)
                    connectorId = ReadGuid(apForConn, "connectorId", "ConnectorId", "connector_id", "masterConnectorId");

                var normalizedSource = NormalizeMasterSource(sourceRaw, formId, connectorId);
                if (string.IsNullOrWhiteSpace(normalizedSource)
                    && string.IsNullOrWhiteSpace(formId)
                    && (connectorId is null || connectorId == Guid.Empty))
                {
                    continue;
                }

                masterSource = normalizedSource;
                masterFormId = string.IsNullOrWhiteSpace(formId) ? null : formId.Trim();
                masterConnectorId = connectorId is { } c && c != Guid.Empty ? c : null;
                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static bool IsApAgentBlock(JsonElement block)
    {
        if (block.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
        {
            var type = typeEl.GetString();
            if (string.Equals(type, "AP_AGENT", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "APAGENT", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (TryGetObject(block, out var settings, "Settings", "settings")
            && TryGetObject(settings, out _, "apAgent", "ApAgent"))
            return true;

        return false;
    }

    /// <summary>
    /// Maps designer values (<c>internal</c>, <c>FORM</c>, …) to EmailIngest / Agents master_source.
    /// </summary>
    public static string? NormalizeMasterSource(string? raw, string? formId, Guid? connectorId)
    {
        var source = (raw ?? string.Empty).Trim();
        if (source.Length == 0)
        {
            if (!string.IsNullOrWhiteSpace(formId))
                return EmailIngestMasterSources.InternalForm;
            return null;
        }

        if (string.Equals(source, "internal", StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, "InternalForm", StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, "Ezofis", StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, "Form", StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, "FORM", StringComparison.OrdinalIgnoreCase))
        {
            return EmailIngestMasterSources.InternalForm;
        }

        if (string.Equals(source, "quickbooks", StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, "qb", StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, EmailIngestMasterSources.QuickBooks, StringComparison.OrdinalIgnoreCase))
        {
            return EmailIngestMasterSources.QuickBooks;
        }

        if (string.Equals(source, "sap", StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, EmailIngestMasterSources.Sap, StringComparison.OrdinalIgnoreCase))
        {
            return EmailIngestMasterSources.Sap;
        }

        if (string.Equals(source, "hana", StringComparison.OrdinalIgnoreCase)
            || source.Contains("HANA", StringComparison.OrdinalIgnoreCase))
        {
            return "HANA";
        }

        // Unknown label but form-only binding → InternalForm.
        if (!string.IsNullOrWhiteSpace(formId) && (connectorId is null || connectorId == Guid.Empty))
            return EmailIngestMasterSources.InternalForm;

        return source;
    }

    private static bool TryGetArray(JsonElement parent, out JsonElement child, params string[] names)
    {
        foreach (var name in names)
        {
            if (parent.TryGetProperty(name, out child) && child.ValueKind == JsonValueKind.Array)
                return true;
        }
        child = default;
        return false;
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
            if (!obj.TryGetProperty(name, out var el))
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
