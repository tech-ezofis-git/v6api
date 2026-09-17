using System.Text.Json;
using System.Text.Json.Serialization;

namespace SaaSApp.Workflow.Application.Workflows;

/// <summary>
/// Serializes a JSON document stored as <see cref="string"/> as a raw JSON object/array
/// instead of an escaped JSON string (avoids <c>\u0022</c> for TABLE / MULTI_SELECT fields).
/// Nested JSON stored as strings (table rows) is expanded to real arrays/objects.
/// </summary>
public sealed class RawJsonStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType == JsonTokenType.String)
            return reader.GetString();

        using var doc = JsonDocument.ParseValue(ref reader);
        return doc.RootElement.GetRawText();
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            writer.WriteNullValue();
            return;
        }

        var trimmed = value.Trim();
        if (!LooksLikeJsonContainer(trimmed))
        {
            writer.WriteStringValue(value);
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            WriteExpanded(writer, doc.RootElement);
        }
        catch (JsonException)
        {
            writer.WriteStringValue(value);
        }
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
                var text = element.GetString();
                if (TryParseEmbeddedJson(text, out var nested) && nested is not null)
                {
                    using (nested)
                        WriteExpanded(writer, nested.RootElement);
                    return;
                }

                writer.WriteStringValue(text);
                return;
            default:
                element.WriteTo(writer);
                return;
        }
    }

    private static bool TryParseEmbeddedJson(string? text, out JsonDocument? doc)
    {
        doc = null;
        if (string.IsNullOrWhiteSpace(text) || !LooksLikeJsonContainer(text.Trim()))
            return false;

        try
        {
            var parsed = JsonDocument.Parse(text.Trim());
            if (parsed.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                doc = parsed;
                return true;
            }

            parsed.Dispose();
        }
        catch (JsonException)
        {
        }

        return false;
    }

    private static bool LooksLikeJsonContainer(string trimmed) =>
        trimmed.Length >= 2
        && ((trimmed[0] == '{' && trimmed[^1] == '}')
            || (trimmed[0] == '[' && trimmed[^1] == ']'));
}
