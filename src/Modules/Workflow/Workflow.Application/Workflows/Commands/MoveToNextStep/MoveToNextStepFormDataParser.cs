using System.Text.Json;
using SaaSApp.Workflow.Application;

namespace SaaSApp.Workflow.Application.Workflows.Commands.MoveToNextStep;

/// <summary>Parses move-next <c>formData</c> (scalar fields + line items for DYNAMIC_TABLE controls).</summary>
public static class MoveToNextStepFormDataParser
{
    private static readonly HashSet<string> ReservedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "formId", "formEntryId", "formentryId", "workflowId", "instanceId", "transactionId"
    };

    public sealed record ParseResult(
        IReadOnlyDictionary<string, string>? Fields,
        string? LineItemsJson);

    public static ParseResult Parse(JsonElement? formData)
    {
        if (!formData.HasValue)
            return new ParseResult(null, null);

        var root = formData.Value;
        if (root.ValueKind == JsonValueKind.String)
        {
            var text = root.GetString();
            if (string.IsNullOrWhiteSpace(text))
                return new ParseResult(null, null);

            using var doc = JsonDocument.Parse(text);
            return ParseObject(doc.RootElement);
        }

        if (root.ValueKind == JsonValueKind.Object)
            return ParseObject(root);

        return new ParseResult(null, null);
    }

    public static IReadOnlyDictionary<string, string>? ParseFieldValues(JsonElement? formData) =>
        Parse(formData).Fields;

    private static ParseResult ParseObject(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return new ParseResult(null, null);

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? lineItemsJson = ApAgentMetadataParser.TryGetRootLineItemsJson(obj);

        foreach (var prop in obj.EnumerateObject())
        {
            if (ReservedKeys.Contains(prop.Name))
                continue;

            if (prop.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            {
                dict[prop.Name] = prop.Value.GetRawText();
                continue;
            }

            if (TryExtractNamedLineItemsJson(prop.Name, prop.Value, out var namedLines))
            {
                lineItemsJson ??= namedLines;
                continue;
            }

            if (!TryGetScalarString(prop.Value, out var value) || string.IsNullOrWhiteSpace(value))
                continue;

            dict[prop.Name] = value;
        }

        IReadOnlyDictionary<string, string>? fields = dict.Count > 0 ? dict : null;
        return new ParseResult(fields, lineItemsJson);
    }

    private static bool TryExtractNamedLineItemsJson(string key, JsonElement value, out string? lineItemsJson)
    {
        lineItemsJson = null;
        if (!ApAgentMetadataParser.IsLineItemSectionName(key))
            return false;

        if (value.ValueKind == JsonValueKind.Array)
        {
            lineItemsJson = value.GetRawText();
            return true;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (string.IsNullOrWhiteSpace(text))
                return false;

            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    lineItemsJson = doc.RootElement.GetRawText();
                    return true;
                }
            }
            catch (JsonException)
            {
                return false;
            }
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var nested in value.EnumerateObject())
            {
                if (nested.Value.ValueKind != JsonValueKind.Array)
                    continue;

                lineItemsJson = nested.Value.GetRawText();
                return true;
            }
        }

        return false;
    }

    private static bool TryGetScalarString(JsonElement value, out string? result)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                result = null;
                return false;
            case JsonValueKind.String:
                result = value.GetString();
                return true;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                result = value.GetRawText();
                return true;
            default:
                result = null;
                return false;
        }
    }
}
