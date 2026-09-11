using System.Text.Json;
using SaaSApp.Workflow.Application.Connectors;

namespace SaaSApp.Workflow.Infrastructure.Services;

internal static class SapPurchaseOrderMapper
{
    public static IReadOnlyList<ConnectorSapXsuaaPurchaseOrderDto> FromListRawJson(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        JsonElement results = default;
        var hasResults = false;

        if (root.TryGetProperty("d", out var d) && d.TryGetProperty("results", out var dResults))
        {
            results = dResults;
            hasResults = true;
        }
        else if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            results = value;
            hasResults = true;
        }

        if (!hasResults || results.ValueKind != JsonValueKind.Array)
            return Array.Empty<ConnectorSapXsuaaPurchaseOrderDto>();

        var list = new List<ConnectorSapXsuaaPurchaseOrderDto>();
        foreach (var item in results.EnumerateArray())
            list.Add(FromEntity(item, item.GetRawText()));
        return list;
    }

    public static ConnectorSapXsuaaPurchaseOrderDto FromRawJson(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        var entity = root;
        if (root.TryGetProperty("d", out var d))
            entity = d;
        return FromEntity(entity, rawJson);
    }

    private static ConnectorSapXsuaaPurchaseOrderDto FromEntity(JsonElement entity, string rawJson)
    {
        var poNumber = GetString(entity, "PurchaseOrder");
        var vendor = GetString(entity, "Supplier") ?? GetString(entity, "SupplierName");
        var vendorName = GetString(entity, "SupplierName") ?? vendor;
        var poDate = GetString(entity, "PurchaseOrderDate") ?? GetString(entity, "CreationDate");
        var currency = GetString(entity, "DocumentCurrency");
        var companyCode = GetString(entity, "CompanyCode");
        var purchasingOrg = GetString(entity, "PurchasingOrganization");
        var status = GetString(entity, "PurchasingProcessingStatus")
            ?? GetString(entity, "PurchasingDocumentDeletionCode");

        decimal? amount = null;
        var lines = new List<ConnectorSapXsuaaPoLineDto>();
        if (entity.TryGetProperty("to_PurchaseOrderItem", out var itemsNav))
        {
            JsonElement items = itemsNav;
            if (itemsNav.TryGetProperty("results", out var itemResults))
                items = itemResults;
            if (items.ValueKind == JsonValueKind.Array)
            {
                decimal sum = 0;
                var any = false;
                foreach (var item in items.EnumerateArray())
                {
                    if (TryGetDecimal(item, "NetAmount", out var line) || TryGetDecimal(item, "GrossAmount", out line))
                    {
                        sum += line;
                        any = true;
                    }

                    TryGetDecimal(item, "OrderQuantity", out var qty);
                    TryGetDecimal(item, "NetPriceAmount", out var price);
                    TryGetDecimal(item, "NetAmount", out var lineAmount);
                    lines.Add(new ConnectorSapXsuaaPoLineDto(
                        GetString(item, "PurchaseOrderItem"),
                        GetString(item, "Material") ?? GetString(item, "PurchaseOrderItemText"),
                        GetString(item, "PurchaseOrderItemText"),
                        qty == 0 ? null : qty,
                        GetString(item, "PurchaseOrderQuantityUnit"),
                        price == 0 ? null : price,
                        lineAmount == 0 ? null : lineAmount));
                }

                if (any)
                    amount = sum;
            }
        }

        return new ConnectorSapXsuaaPurchaseOrderDto(
            poNumber,
            vendorName,
            vendor,
            poDate,
            amount,
            currency,
            companyCode,
            purchasingOrg,
            status,
            lines,
            rawJson);
    }

    private static string? GetString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p))
            return null;
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Number => p.ToString(),
            _ => null
        };
    }

    private static bool TryGetDecimal(JsonElement el, string name, out decimal value)
    {
        value = 0;
        if (!el.TryGetProperty(name, out var p))
            return false;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out value))
            return true;
        if (p.ValueKind == JsonValueKind.String && decimal.TryParse(p.GetString(), out value))
            return true;
        return false;
    }
}
