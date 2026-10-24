using System.Text.Json;

namespace SaaSApp.Workflow.Application.Workflows;

/// <summary>One <c>fieldMappings[]</c> entry on a block's <c>settings.endSync</c>.</summary>
public sealed record WorkflowEndSyncFieldMapping(
    string WorkflowFieldId,
    string MasterFieldId,
    bool IsMainField);

/// <summary>A block's <c>settings.endSync</c> configuration (designer: sync to a master form on step complete).</summary>
public sealed record WorkflowEndSyncConfig(
    bool Enabled,
    string? Target,
    string? MasterFormId,
    IReadOnlyList<WorkflowEndSyncFieldMapping> FieldMappings);

/// <summary>
/// Reads <c>Blocks[].Settings.endSync</c> from workflow designer JSON: when enabled with
/// <c>target: MASTER_FORM</c>, the completed step's mapped field values are copied onto
/// <c>masterFormId</c> (insert or update, keyed by the mapping marked <c>isMainField</c>).
/// </summary>
public static class WorkflowEndSyncJson
{
    /// <summary>Finds the block whose <c>id</c> matches <paramref name="blockId"/> and reads its endSync config.</summary>
    public static bool TryRead(string? workflowJson, string blockId, out WorkflowEndSyncConfig? config)
    {
        config = null;
        if (string.IsNullOrWhiteSpace(workflowJson) || string.IsNullOrWhiteSpace(blockId))
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

                var id = ReadString(block, "id", "Id");
                if (string.IsNullOrWhiteSpace(id) || !string.Equals(id, blockId, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!TryGetObject(block, out var settings, "Settings", "settings"))
                    return false;
                if (!TryGetObject(settings, out var endSync, "endSync", "EndSync"))
                    return false;

                config = ParseConfig(endSync);
                return config != null;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static WorkflowEndSyncConfig? ParseConfig(JsonElement endSync)
    {
        var enabled = ReadBool(endSync, "enabled", "Enabled") ?? false;
        var target = ReadString(endSync, "target", "Target");
        var masterFormId = ReadString(endSync, "masterFormId", "MasterFormId");

        var mappings = new List<WorkflowEndSyncFieldMapping>();
        if (TryGetArray(endSync, out var mappingsArray, "fieldMappings", "FieldMappings"))
        {
            foreach (var m in mappingsArray.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object)
                    continue;

                var workflowFieldId = ReadString(m, "workflowFieldId", "WorkflowFieldId");
                var masterFieldId = ReadString(m, "masterFieldId", "MasterFieldId");
                if (string.IsNullOrWhiteSpace(workflowFieldId) || string.IsNullOrWhiteSpace(masterFieldId))
                    continue;

                var isMain = ReadBool(m, "IsMainField", "isMainField") ?? false;
                mappings.Add(new WorkflowEndSyncFieldMapping(workflowFieldId, masterFieldId, isMain));
            }
        }

        return new WorkflowEndSyncConfig(enabled, target, masterFormId, mappings);
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

    private static bool? ReadBool(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var el))
                continue;
            if (el.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return el.GetBoolean();
        }
        return null;
    }
}
