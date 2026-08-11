using System.Text;
using System.Text.Json;
using SaaSApp.Workflow.Application.Connectors;

namespace SaaSApp.Workflow.Infrastructure.Services;

internal static class QuickBooksPurchaseOrderMapper
{
    public static ConnectorQuickBooksPurchaseOrderDto FromRawJson(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var el = doc.RootElement;

        string? docNumber = el.TryGetProperty("DocNumber", out var dn) ? dn.GetString() : null;
        string? txnDate = el.TryGetProperty("TxnDate", out var td) ? td.GetString() : null;

        string? vendorId = null;
        string? vendorName = null;
        if (el.TryGetProperty("VendorRef", out var vr))
        {
            if (vr.TryGetProperty("value", out var v)) vendorId = v.GetString();
            if (vr.TryGetProperty("name", out var n)) vendorName = n.GetString();
        }

        decimal? total = el.TryGetProperty("TotalAmt", out var ta) && ta.TryGetDecimal(out var amt) ? amt : null;
        string? currency = null;
        if (el.TryGetProperty("CurrencyRef", out var cr) && cr.TryGetProperty("value", out var cv))
            currency = cv.GetString();

        string? terms = null;
        if (el.TryGetProperty("SalesTermRef", out var str) && str.TryGetProperty("name", out var stn))
            terms = stn.GetString();

        // QBO PurchaseOrder has no dedicated Buyer field; leave null unless a named custom field exists.
        string? buyer = TryGetCustomField(el, "Buyer");

        string? vendorAddress = el.TryGetProperty("VendorAddr", out var va) ? FormatAddress(va) : null;
        string? shipToAddress = el.TryGetProperty("ShipAddr", out var sa) ? FormatAddress(sa) : null;

        string? notes = el.TryGetProperty("PrivateNote", out var pn) ? pn.GetString() : null;
        if (string.IsNullOrWhiteSpace(notes) && el.TryGetProperty("Memo", out var memoEl))
            notes = memoEl.GetString();

        string? poStatus = el.TryGetProperty("POStatus", out var ps) ? ps.GetString() : null;

        var lines = new List<ConnectorQuickBooksPoLineDto>();
        if (el.TryGetProperty("Line", out var lineArr) && lineArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var line in lineArr.EnumerateArray())
            {
                var detailType = line.TryGetProperty("DetailType", out var dt) ? dt.GetString() : null;
                if (string.Equals(detailType, "SubTotalLineDetail", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Only emit expense/item lines (skip empty description-only rows without amounts).
                var hasItem = line.TryGetProperty("ItemBasedExpenseLineDetail", out var itemDetail);
                var hasAcct = line.TryGetProperty("AccountBasedExpenseLineDetail", out var acctDetail);
                if (!hasItem && !hasAcct)
                    continue;

                int? lineNo = line.TryGetProperty("LineNum", out var ln) && ln.TryGetInt32(out var n) ? n : null;
                string? description = line.TryGetProperty("Description", out var desc) ? desc.GetString() : null;
                decimal? lineAmount = line.TryGetProperty("Amount", out var a) && a.TryGetDecimal(out var am) ? am : null;

                string? itemNo = null;
                decimal? qty = null;
                decimal? rate = null;
                string? uom = null;

                if (hasItem)
                {
                    if (itemDetail.TryGetProperty("ItemRef", out var ir) && ir.TryGetProperty("name", out var iname))
                        itemNo = iname.GetString();
                    if (itemDetail.TryGetProperty("Qty", out var q) && q.TryGetDecimal(out var qd))
                        qty = qd;
                    if (itemDetail.TryGetProperty("UnitPrice", out var up) && up.TryGetDecimal(out var upd))
                        rate = upd;
                    if (itemDetail.TryGetProperty("UnitOfMeasureRef", out var uomRef))
                    {
                        if (uomRef.TryGetProperty("name", out var uomName))
                            uom = uomName.GetString();
                        else if (uomRef.TryGetProperty("value", out var uomVal))
                            uom = uomVal.GetString();
                    }

                    // AP Agent expects a UOM; QBO sandbox often omits UnitOfMeasureRef.
                    if (string.IsNullOrWhiteSpace(uom))
                        uom = "EA";
                }

                lines.Add(new ConnectorQuickBooksPoLineDto
                {
                    LineNo = lineNo,
                    ItemNo = itemNo,
                    Description = description,
                    Quantity = qty,
                    Uom = uom,
                    Rate = rate,
                    LineAmount = lineAmount
                });
            }
        }

        return new ConnectorQuickBooksPurchaseOrderDto
        {
            PoNumber = docNumber,
            VendorName = vendorName,
            Vendor = vendorId,
            PoDate = txnDate,
            PoAmount = total,
            Currency = currency,
            Terms = terms,
            Buyer = buyer,
            VendorAddress = vendorAddress,
            ShipToAddress = shipToAddress,
            Notes = notes,
            PoStatus = poStatus,
            PoLineItem = lines
        };
    }

    private static string? TryGetCustomField(JsonElement el, string fieldName)
    {
        if (!el.TryGetProperty("CustomField", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var field in arr.EnumerateArray())
        {
            var name = field.TryGetProperty("Name", out var n) ? n.GetString() : null;
            if (!string.Equals(name, fieldName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (field.TryGetProperty("StringValue", out var sv))
                return sv.GetString();
        }

        return null;
    }

    private static string? FormatAddress(JsonElement addr)
    {
        if (addr.ValueKind != JsonValueKind.Object)
            return null;

        var parts = new List<string>();
        void Add(string prop)
        {
            if (addr.TryGetProperty(prop, out var p))
            {
                var s = p.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    parts.Add(s.Trim());
            }
        }

        Add("Line1");
        Add("Line2");
        Add("Line3");
        Add("Line4");
        Add("Line5");

        var city = addr.TryGetProperty("City", out var c) ? c.GetString() : null;
        var region = addr.TryGetProperty("CountrySubDivisionCode", out var r) ? r.GetString() : null;
        var postal = addr.TryGetProperty("PostalCode", out var z) ? z.GetString() : null;
        var country = addr.TryGetProperty("Country", out var co) ? co.GetString() : null;

        var cityLine = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(city)) cityLine.Append(city);
        if (!string.IsNullOrWhiteSpace(region))
        {
            if (cityLine.Length > 0) cityLine.Append(", ");
            cityLine.Append(region);
        }
        if (!string.IsNullOrWhiteSpace(postal))
        {
            if (cityLine.Length > 0) cityLine.Append(' ');
            cityLine.Append(postal);
        }
        if (cityLine.Length > 0)
            parts.Add(cityLine.ToString());
        if (!string.IsNullOrWhiteSpace(country))
            parts.Add(country!);

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }
}
