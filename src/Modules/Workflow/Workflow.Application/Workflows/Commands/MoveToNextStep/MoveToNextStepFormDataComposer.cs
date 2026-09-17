using System.Text;
using System.Text.Json;

namespace SaaSApp.Workflow.Application.Workflows.Commands.MoveToNextStep;

/// <summary>Uses move-next <c>formData</c> from the user for inbox/sent/completed (not AIAGENTResponse).</summary>
public static class MoveToNextStepFormDataComposer
{
    /// <summary>Returns user-submitted formData JSON as stored for mailbox tables.</summary>
    public static string? ForMailbox(string? submittedFormDataJson)
    {
        if (string.IsNullOrWhiteSpace(submittedFormDataJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(submittedFormDataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            return WriteMailboxObject(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Build mailbox JSON from parsed formData fields when body sent formData as object.</summary>
    public static string? FromParsedFields(
        IReadOnlyDictionary<string, string>? parsedFields,
        string? lineItemsJson)
    {
        if (parsedFields == null || parsedFields.Count == 0)
            return string.IsNullOrWhiteSpace(lineItemsJson) ? null : BuildFromLineItemsOnly(lineItemsJson);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in parsedFields)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;
            map[key] = value;
        }

        if (!string.IsNullOrWhiteSpace(lineItemsJson))
        {
            var lineItemKey = map.Keys.FirstOrDefault(k => IsJsonArrayValue(map[k]))
                ?? map.Keys.FirstOrDefault(k => k.Contains('_', StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(lineItemKey))
                map[lineItemKey] = lineItemsJson;
        }

        if (map.Count == 0)
            return null;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in map)
            {
                writer.WritePropertyName(key);
                WriteMailboxValue(writer, value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string WriteMailboxObject(JsonElement root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteExpanded(writer, root);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteExpanded(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject())
                {
                    writer.WritePropertyName(prop.Name);
                    WriteExpanded(writer, prop.Value);
                }

                writer.WriteEndObject();
                return;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteExpanded(writer, item);
                writer.WriteEndArray();
                return;
            case JsonValueKind.String:
                WriteMailboxValue(writer, element.GetString() ?? string.Empty);
                return;
            default:
                element.WriteTo(writer);
                return;
        }
    }

    private static void WriteMailboxValue(Utf8JsonWriter writer, string value)
    {
        var trimmed = value.Trim();
        if (IsJsonArrayValue(trimmed) || (trimmed.StartsWith('{') && trimmed.EndsWith('}')))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                {
                    doc.RootElement.WriteTo(writer);
                    return;
                }
            }
            catch (JsonException)
            {
            }
        }

        writer.WriteStringValue(value);
    }

    private static string? BuildFromLineItemsOnly(string lineItemsJson) =>
        string.IsNullOrWhiteSpace(lineItemsJson) ? null : "{\"Line Item\":" + lineItemsJson + "}";

    private static bool IsJsonArrayValue(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("[", StringComparison.Ordinal)
            && trimmed.EndsWith("]", StringComparison.Ordinal);
    }
}
