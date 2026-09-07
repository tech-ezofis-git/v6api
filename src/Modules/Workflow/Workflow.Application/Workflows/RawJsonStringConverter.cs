using System.Text.Json;
using System.Text.Json.Serialization;

namespace SaaSApp.Workflow.Application.Workflows;

/// <summary>
/// Serializes a JSON document stored as <see cref="string"/> as a raw JSON object/array
/// instead of an escaped JSON string (avoids <c>\u0022</c> for TABLE / MULTI_SELECT fields).
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
        if (trimmed.Length >= 2
            && ((trimmed[0] == '[' && trimmed[^1] == ']')
                || (trimmed[0] == '{' && trimmed[^1] == '}')))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                doc.RootElement.WriteTo(writer);
                return;
            }
            catch (JsonException)
            {
                // fall through to plain string
            }
        }

        writer.WriteStringValue(value);
    }
}
