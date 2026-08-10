using System.Text.Json;
using System.Text.Json.Nodes;

namespace SaaSApp.Repository.Infrastructure;

internal static class RepositoryItemLineItemsParser
{
    /// <summary>
    /// Returns the invoice line-item JSON array as <see cref="JsonElement"/> (no rows/grandTotal wrapper).
    /// Sources must be dedicated line-item fields only (not SummaryJson / AI key-facts).
    /// </summary>
    public static JsonElement? TryParseArray(params string?[] jsonSources)
    {
        foreach (var json in jsonSources)
        {
            if (string.IsNullOrWhiteSpace(json))
                continue;

            if (TryExtractArrayJson(json, out var arrayJson) && !string.IsNullOrWhiteSpace(arrayJson))
            {
                using var doc = JsonDocument.Parse(arrayJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                    return doc.RootElement.Clone();
            }
        }

        return null;
    }

    private static bool TryExtractArrayJson(string json, out string? arrayJson)
    {
        arrayJson = null;
        try
        {
            var node = JsonNode.Parse(json);
            if (node is JsonArray rootArray && rootArray.Count > 0)
            {
                arrayJson = rootArray.ToJsonString();
                return true;
            }

            if (node is not JsonObject rootObj)
                return false;

            // Only known line-item property names — do not grab AI summary arrays.
            foreach (var name in new[]
                     {
                         "lineItems", "line_items", "LineItems", "items", "invoiceLines",
                         "InvoiceExtractedLineItem", "invoiceExtractedLineItem",
                         "POLineItem", "PoLineItem", "po_line_item"
                     })
            {
                if (TryTakeArray(rootObj[name], out arrayJson))
                    return true;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryTakeArray(JsonNode? node, out string? arrayJson)
    {
        arrayJson = null;
        if (node is JsonArray arr && arr.Count > 0)
        {
            arrayJson = arr.ToJsonString();
            return true;
        }

        if (node is JsonValue v &&
            v.TryGetValue<string>(out var text) &&
            !string.IsNullOrWhiteSpace(text) &&
            text.TrimStart().StartsWith('['))
        {
            try
            {
                if (JsonNode.Parse(text) is JsonArray inner && inner.Count > 0)
                {
                    arrayJson = inner.ToJsonString();
                    return true;
                }
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return false;
    }
}
