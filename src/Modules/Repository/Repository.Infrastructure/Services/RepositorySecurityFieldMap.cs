using SaaSApp.Repository.Application.Contracts;

namespace SaaSApp.Repository.Infrastructure.Services;

public static class RepositorySecurityFieldMap
{
    public static IReadOnlyDictionary<string, string?> FromListItem(RepositoryItemListDto item)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ItemId"] = item.Id.ToString("D"),
            ["Id"] = item.Id.ToString("D"),
            ["FileName"] = item.FileName,
            ["CreatedBy"] = item.CreatedByUserId?.ToString("D") ?? item.CreatedBy,
            ["DocumentType"] = item.DocumentType,
            ["Supplier"] = item.Supplier,
            ["InvoiceNumber"] = item.InvoiceNumber,
            ["PoNumber"] = item.PoNumber,
            ["Status"] = item.Status,
            ["AiStatus"] = item.AiStatus,
            ["RiskLevel"] = item.RiskLevel,
            ["Source"] = item.Source,
            ["Department"] = item.Department,
            ["Currency"] = item.Currency,
            ["Buyer"] = item.Buyer,
            ["Terms"] = item.Terms,
            ["SupplierAddress"] = item.SupplierAddress,
            ["ShipToAddress"] = item.ShipToAddress,
            ["PayToAddress"] = item.PayToAddress,
            ["DocumentDate"] = item.DocumentDate?.ToString("O"),
            ["InvoiceDate"] = item.InvoiceDate?.ToString("O"),
            ["PoDate"] = item.PoDate?.ToString("O"),
            ["Amount"] = item.Amount?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["InvoiceAmount"] = item.InvoiceAmount?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["InvoiceTaxAmount"] = item.InvoiceTaxAmount?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["PoAmount"] = item.PoAmount?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["OcrPercent"] = item.OcrPercent?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        return map;
    }

    public static IReadOnlyDictionary<string, string?> FromDetail(RepositoryItemDetailDto item)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ItemId"] = item.Id.ToString("D"),
            ["Id"] = item.Id.ToString("D"),
            ["FileName"] = item.FileName,
            ["FileType"] = item.FileType
        };

        foreach (var kv in item.Fields)
            map[kv.Key] = kv.Value?.ToString();

        // Prefer GUID CreatedBy for share grants (email may overwrite in query enrichment).
        if (item.Fields.TryGetValue("CreatedBy", out var createdBy) && createdBy != null)
            map["CreatedBy"] = createdBy.ToString();

        return map;
    }

    public static IReadOnlyDictionary<string, string?> FromWorkspace(RepositoryItemWorkspaceDto item)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ItemId"] = item.Id.ToString("D"),
            ["Id"] = item.Id.ToString("D"),
            ["FileName"] = item.FileName,
            ["FileType"] = item.FileType
        };

        foreach (var section in item.DetailsRow)
        {
            foreach (var field in section.Fields)
            {
                if (!string.IsNullOrWhiteSpace(field.Key))
                    map[field.Key] = field.Value;
                if (!string.IsNullOrWhiteSpace(field.Label))
                    map[field.Label] = field.Value;
            }
        }

        return map;
    }
}
