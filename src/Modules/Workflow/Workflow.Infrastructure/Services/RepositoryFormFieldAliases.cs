namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>Repository / OCR field name aliases used when matching to wFormControl names.</summary>
internal static class RepositoryFormFieldAliases
{
    internal static readonly Dictionary<string, string> Map =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["InvoiceNo"] = "InvoiceNumber",
            ["Invoice No"] = "InvoiceNumber",
            ["Invoice Number"] = "InvoiceNumber",
            ["PONumber"] = "PoNumber",
            ["PO Number"] = "PoNumber",
            ["PO No"] = "PoNumber",
            ["InvoiceDate"] = "DocumentDate",
            ["Invoice Date"] = "DocumentDate",
            ["PODate"] = "PoDate",
            ["PO Date"] = "PoDate",
            ["PO DATE"] = "PoDate",
            ["VendorName"] = "Supplier",
            ["Vendor Name"] = "Supplier",
            ["Vendor"] = "Supplier",
            ["InvoiceAmount"] = "Amount",
            ["Invoice Amount"] = "Amount",
            ["POAmount"] = "PoAmount",
            ["PO Amount"] = "PoAmount",
            ["Invoice Tax Amount"] = "InvoiceTaxAmount",
            ["InvoiceTaxAmount"] = "InvoiceTaxAmount",
            ["currency"] = "Currency",
            ["Document Type"] = "DocumentType",
            ["DocumentType"] = "DocumentType",
            ["Job No"] = "JobNo",
            ["JobNo"] = "JobNo",
            ["IMO Number"] = "IMONumber",
            ["IMONumber"] = "IMONumber",
            ["Agency Appointment Date"] = "AgencyAppointmentDate",
            ["PDA Number"] = "PDANumber",
            ["FDA Number"] = "FDANumber",
            ["PDA Revision No"] = "PDARevisionNo",
            ["Total PDA"] = "TotalPDA",
            ["Cargo Type"] = "CargoType",
            ["Cargo Quantity"] = "CargoQuantity",
            ["Requested Services"] = "RequestedServices",
            ["Job Status"] = "JobStatus",
            ["Settlement Status"] = "SettlementStatus",
        };

    internal static IEnumerable<string> ExpandKeys(string fieldKey)
    {
        if (string.IsNullOrWhiteSpace(fieldKey))
            yield break;

        var key = fieldKey.Trim();
        yield return key;

        var normalized = Normalize(key);
        if (!string.Equals(normalized, key, StringComparison.OrdinalIgnoreCase))
            yield return normalized;

        if (Map.TryGetValue(key, out var canonical))
        {
            yield return canonical;
            var normCanonical = Normalize(canonical);
            if (!string.Equals(normCanonical, canonical, StringComparison.OrdinalIgnoreCase))
                yield return normCanonical;
        }

        foreach (var (aliasKey, aliasCanonical) in Map)
        {
            if (string.Equals(aliasCanonical, key, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(aliasKey, key, StringComparison.OrdinalIgnoreCase))
            {
                yield return aliasKey;
                yield return Normalize(aliasKey);
            }
        }
    }

    private static string Normalize(string name) =>
        name.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim();
}
