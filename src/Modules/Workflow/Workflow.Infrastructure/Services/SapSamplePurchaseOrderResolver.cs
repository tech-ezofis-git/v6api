using System.Globalization;
using System.Text.Json;
using SaaSApp.Workflow.Application.Connectors;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>Shared SAP provider-code matching (SAP, SAP_XSUAA, SAP_*).</summary>
public static class SapConnectorProviderCodes
{
    public static bool IsSap(string? providerCode)
    {
        var code = (providerCode ?? string.Empty).Trim().ToUpperInvariant();
        if (code.Length == 0)
            return false;
        if (code == "SAP" || code == "SAP_XSUAA")
            return true;
        return code.StartsWith("SAP_", StringComparison.Ordinal);
    }
}

/// <summary>Resolve PO masters from connector ConfigJson.samplePurchaseOrders (mode=sample).</summary>
public static class SapSamplePurchaseOrderResolver
{
    public const string SampleSource = "sap_sample";

    public static bool TryResolve(
        string? configJson,
        string poNumber,
        out ConnectorSapPurchaseOrderDto? purchaseOrder,
        out string? source)
    {
        purchaseOrder = null;
        source = null;
        var needle = (poNumber ?? string.Empty).Trim();
        if (needle.Length == 0 || string.IsNullOrWhiteSpace(configJson))
            return false;

        using var doc = JsonDocument.Parse(configJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return false;

        var root = doc.RootElement;
        var mode = GetString(root, "mode");
        var hasSamples = root.TryGetProperty("samplePurchaseOrders", out var samples)
            && samples.ValueKind == JsonValueKind.Array
            && samples.GetArrayLength() > 0;

        // Sample path when mode=sample OR samplePurchaseOrders is present.
        if (!string.Equals(mode, "sample", StringComparison.OrdinalIgnoreCase) && !hasSamples)
            return false;
        if (!hasSamples)
            return false;

        foreach (var item in samples.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            var candidate = GetString(item, "po_number", "poNumber", "PO Number", "DocNumber");
            if (!string.Equals(candidate, needle, StringComparison.OrdinalIgnoreCase))
                continue;

            purchaseOrder = MapPurchaseOrder(item, needle);
            source = SampleSource;
            return true;
        }

        return false;
    }

    private static ConnectorSapPurchaseOrderDto MapPurchaseOrder(JsonElement item, string fallbackPo)
    {
        var po = GetString(item, "po_number", "poNumber", "PO Number") ?? fallbackPo;
        var vendor = GetString(item, "vendor", "Vendor", "Vendor Name", "vendorName");
        var currency = GetString(item, "currency", "Currency");
        var total = GetDecimal(item, "total", "PO Amount", "amount", "TotalAmt");

        var lines = new List<ConnectorSapPoLineDto>();
        if (item.TryGetProperty("lines", out var linesEl) && linesEl.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var line in linesEl.EnumerateArray())
            {
                i++;
                if (line.ValueKind != JsonValueKind.Object)
                    continue;
                lines.Add(new ConnectorSapPoLineDto
                {
                    LineNo = GetInt(line, "line_no", "lineNo") ?? i,
                    ItemNo = GetString(line, "item_no", "itemNo", "item"),
                    Description = GetString(line, "description", "Description"),
                    Qty = GetDecimal(line, "qty", "quantity", "Quantity"),
                    UnitPrice = GetDecimal(line, "unit_price", "unitPrice", "rate", "Rate"),
                    Amount = GetDecimal(line, "amount", "line_amount", "LineAmount"),
                });
            }
        }

        return new ConnectorSapPurchaseOrderDto
        {
            PoNumber = po,
            Vendor = vendor,
            Total = total,
            Currency = currency,
            Lines = lines,
        };
    }

    private static string? GetString(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var el))
            {
                // case-insensitive fallback
                foreach (var prop in obj.EnumerateObject())
                {
                    if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        el = prop.Value;
                        goto found;
                    }
                }
                continue;
            }
            found:
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    return s.Trim();
            }
            else if (el.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                return el.ToString();
            }
        }
        return null;
    }

    private static decimal? GetDecimal(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyIgnoreCase(obj, name, out var el))
                continue;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var d))
                return d;
            if (el.ValueKind == JsonValueKind.String
                && decimal.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        return null;
    }

    private static int? GetInt(JsonElement obj, params string[] names)
    {
        var d = GetDecimal(obj, names);
        if (d is null)
            return null;
        return (int)decimal.Truncate(d.Value);
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement el)
    {
        if (obj.TryGetProperty(name, out el))
            return true;
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                el = prop.Value;
                return true;
            }
        }
        el = default;
        return false;
    }
}
