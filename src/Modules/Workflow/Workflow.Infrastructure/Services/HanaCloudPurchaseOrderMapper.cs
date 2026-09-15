using System.Globalization;
using System.Text.Json;
using SaaSApp.Workflow.Application.Connectors;

namespace SaaSApp.Workflow.Infrastructure.Services;

internal static class HanaCloudPurchaseOrderMapper
{
    public static IReadOnlyList<ConnectorHanaPoLineDto> ParseItems(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<ConnectorHanaPoLineDto>();

        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return Array.Empty<ConnectorHanaPoLineDto>();

        var lines = new List<ConnectorHanaPoLineDto>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            lines.Add(new ConnectorHanaPoLineDto(
                ReadInt(item, "item_number"),
                ReadString(item, "item_category"),
                ReadString(item, "material_id"),
                ReadString(item, "material_description"),
                ReadString(item, "material_group"),
                ReadString(item, "plant"),
                ReadDecimal(item, "order_quantity"),
                ReadString(item, "unit_of_measure"),
                ReadDecimal(item, "net_price"),
                ReadDecimal(item, "price_unit"),
                ReadDecimal(item, "net_value")));
        }

        return lines;
    }

    public static decimal? SumNetValue(IReadOnlyList<ConnectorHanaPoLineDto> lines)
    {
        decimal total = 0;
        var any = false;
        foreach (var line in lines)
        {
            if (line.NetValue is null)
                continue;
            total += line.NetValue.Value;
            any = true;
        }

        return any ? total : null;
    }

    public static string? SerializeItems(IReadOnlyList<ConnectorHanaPoLineDto>? lines)
    {
        if (lines is null || lines.Count == 0)
            return null;

        var payload = lines.Select(line => new Dictionary<string, object?>
        {
            ["item_number"] = line.ItemNumber,
            ["item_category"] = line.ItemCategory,
            ["material_id"] = line.MaterialId,
            ["material_description"] = line.MaterialDescription,
            ["material_group"] = line.MaterialGroup,
            ["plant"] = line.Plant,
            ["order_quantity"] = line.OrderQuantity,
            ["unit_of_measure"] = line.UnitOfMeasure,
            ["net_price"] = line.NetPrice,
            ["price_unit"] = line.PriceUnit,
            ["net_value"] = line.NetValue
        });

        return JsonSerializer.Serialize(payload);
    }

    private static string? ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n))
            return n;
        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
            return n;
        return null;
    }

    private static decimal? ReadDecimal(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var n))
            return n;
        if (value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out n))
            return n;
        return null;
    }
}
