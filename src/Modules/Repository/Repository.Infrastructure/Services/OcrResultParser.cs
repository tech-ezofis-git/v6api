using System.Text.Json;
using SaaSApp.Repository.Application.Contracts;

namespace SaaSApp.Repository.Infrastructure.Services;

internal static class OcrResultParser
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<UploadIndexFieldDto>? TryParseFieldList(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
                return ParseFieldArray(root);

            if (root.TryGetProperty("ocrResult", out var ocrResult))
                return ParseFieldArray(ocrResult);

            if (root.TryGetProperty("ocrFieldList", out var ocrFieldList))
                return ParseFieldArray(ocrFieldList);

            if (root.TryGetProperty("fields", out var fields))
                return ParseFieldArray(fields);

            if (root.TryGetProperty("data", out var data))
                return ParseFieldArray(data);
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    public static string? TryParseOcrText(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            return TryParseOcrText(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryParseOcrText(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.String)
        {
            var inner = root.GetString();
            if (string.IsNullOrWhiteSpace(inner))
                return null;

            try
            {
                using var innerDoc = JsonDocument.Parse(inner);
                return TryParseOcrText(innerDoc.RootElement);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (TryGetOcrTextProperty(root, "ocrText", out var text)
            || TryGetOcrTextProperty(root, "OcrText", out text)
            || TryGetOcrTextProperty(root, "ocr_text", out text))
        {
            return text;
        }

        return null;
    }

    private static bool TryGetOcrTextProperty(JsonElement root, string propertyName, out string? text)
    {
        text = null;
        if (!root.TryGetProperty(propertyName, out var prop))
            return false;

        if (prop.ValueKind != JsonValueKind.String)
            return false;

        text = prop.GetString();
        return !string.IsNullOrWhiteSpace(text);
    }

    private static IReadOnlyList<UploadIndexFieldDto>? ParseFieldArray(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var inner = element.GetString();
            if (string.IsNullOrWhiteSpace(inner))
                return null;
            return TryParseFieldList(inner);
        }

        if (element.ValueKind != JsonValueKind.Array)
            return null;

        var list = new List<UploadIndexFieldDto>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var inner = item.GetString();
                if (string.IsNullOrWhiteSpace(inner))
                    continue;

                var nested = TryParseFieldList(inner) ?? ParseSingleFieldObject(inner);
                if (nested != null)
                    list.AddRange(nested);
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
                continue;

            if (TryReadField(item, out var field))
                list.Add(field);
        }

        return list.Count > 0 ? list : null;
    }

    /// <summary>One metadata object: <c>{"name":"Year","value":"2026","type":"SHORT_TEXT"}</c>.</summary>
    private static IReadOnlyList<UploadIndexFieldDto>? ParseSingleFieldObject(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object && TryReadField(doc.RootElement, out var field)
                ? new[] { field }
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryReadField(JsonElement item, out UploadIndexFieldDto field)
    {
        field = new UploadIndexFieldDto(string.Empty, string.Empty);
        var name = GetString(item, "name") ?? GetString(item, "Name") ?? GetString(item, "fieldName");
        if (string.IsNullOrWhiteSpace(name))
            return false;

        // A comma-split fragment looks like {"name":"Year" — do not store that as a column name.
        if (name.TrimStart().StartsWith('{'))
            return false;

        var value = GetString(item, "value") ?? GetString(item, "Value") ?? string.Empty;
        var type = GetString(item, "type") ?? GetString(item, "Type") ?? GetString(item, "dataType");
        field = new UploadIndexFieldDto(name.Trim(), value, type);
        return true;
    }

    private static string? GetString(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var prop))
            return null;

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => null
        };
    }
}
