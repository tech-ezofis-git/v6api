using System.Text.Json;

namespace SaaSApp.Workflow.Application.Workflows;

/// <summary>
/// Reads AP Agent settings from workflow designer JSON
/// (<c>Blocks[].Settings.apAgent</c>).
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
