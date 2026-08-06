using System.Globalization;
using Microsoft.Data.SqlClient;
using SaaSApp.Repository.Application;
using SaaSApp.Repository.Application.Contracts;

namespace SaaSApp.Repository.Infrastructure;

internal static class RepositoryItemListMetadataEnricher
{
    private static readonly Dictionary<string, string> FieldNameToListProperty =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Invoice Number"] = "InvoiceNumber",
            ["Invoice No"] = "InvoiceNumber",
            ["Vendor Name"] = "Supplier",
            ["Vendor"] = "Supplier",
            ["Invoice Date"] = "DocumentDate",
            ["InvoiceDate"] = "InvoiceDate",
            ["Date"] = "DocumentDate",
            ["PO Date"] = "DocumentDate",
            ["PODate"] = "PoDate",
            ["Invoice Amount"] = "Amount",
            ["InvoiceAmount"] = "InvoiceAmount",
            ["PO Amount"] = "Amount",
            ["POAmount"] = "PoAmount",
            ["Invoice Tax Amount"] = "InvoiceTaxAmount",
            ["InvoiceTaxAmount"] = "InvoiceTaxAmount",
            ["Document Type"] = "DocumentType",
            ["Buyer"] = "Buyer",
            ["Terms"] = "Terms",
            ["Supplier Address"] = "SupplierAddress",
            ["SupplierAddress"] = "SupplierAddress",
            ["Ship To Address"] = "ShipToAddress",
            ["ShipToAddress"] = "ShipToAddress",
            ["Pay To Address"] = "PayToAddress",
            ["PayToAddress"] = "PayToAddress",
            ["Matched Status"] = "AiStatus",
            ["OCR Confidence"] = "OcrScore",
            ["AI Validation"] = "AiStatus",
            ["Risk Level"] = "RiskLevel",
            ["Source Channel"] = "Source",
        };

    public static RepositoryItemListDto Enrich(
        RepositoryItemListDto row,
        string? ocrJson,
        string? summaryJson,
        RepositoryDetailDto? repository = null,
        SqlDataReader? reader = null,
        HashSet<string>? tableColumns = null)
    {
        var extracted = RepositoryOcrJsonMetadataExtractor.Extract(ocrJson, summaryJson);
        var repositoryScalars = ReadRepositoryFieldScalars(repository, reader, tableColumns);

        return row with
        {
            DocumentType = Coalesce(row.DocumentType, repositoryScalars, extracted, "DocumentType"),
            Supplier = Coalesce(row.Supplier, repositoryScalars, extracted, "Supplier"),
            InvoiceNumber = Coalesce(row.InvoiceNumber, repositoryScalars, extracted, "InvoiceNumber")
                ?? RepositoryOcrJsonMetadataExtractor.TryInferInvoiceNumberFromFileName(row.FileName),
            PoNumber = Coalesce(row.PoNumber, repositoryScalars, extracted, "PoNumber"),
            DocumentDate = CoalesceDate(row.DocumentDate, repositoryScalars, extracted, "DocumentDate", "Date"),
            Amount = CoalesceDecimal(row.Amount, repositoryScalars, extracted, "Amount"),
            Currency = Coalesce(row.Currency, repositoryScalars, extracted, "Currency"),
            Status = Coalesce(row.Status, repositoryScalars, extracted, "Status"),
            OcrPercent = row.OcrPercent
                ?? RepositoryOcrJsonMetadataExtractor.TryParseOcrScore(
                    Coalesce(null, repositoryScalars, extracted, "OcrScore")),
            AiStatus = Coalesce(row.AiStatus, repositoryScalars, extracted, "AiStatus", "MatchedStatus"),
            RiskLevel = Coalesce(row.RiskLevel, repositoryScalars, extracted, "RiskLevel"),
            Source = Coalesce(row.Source, repositoryScalars, extracted, "Source"),
            Department = Coalesce(row.Department, repositoryScalars, extracted, "Department"),
            InvoiceDate = CoalesceDate(row.InvoiceDate, repositoryScalars, extracted, "InvoiceDate", "DocumentDate", "Date"),
            InvoiceAmount = CoalesceDecimal(row.InvoiceAmount, repositoryScalars, extracted, "InvoiceAmount", "Amount"),
            InvoiceTaxAmount = CoalesceDecimal(row.InvoiceTaxAmount, repositoryScalars, extracted, "InvoiceTaxAmount"),
            PoDate = CoalesceDate(row.PoDate, repositoryScalars, extracted, "PoDate", "PODate"),
            PoAmount = CoalesceDecimal(row.PoAmount, repositoryScalars, extracted, "PoAmount", "POAmount"),
            Buyer = Coalesce(row.Buyer, repositoryScalars, extracted, "Buyer"),
            Terms = Coalesce(row.Terms, repositoryScalars, extracted, "Terms"),
            SupplierAddress = Coalesce(row.SupplierAddress, repositoryScalars, extracted, "SupplierAddress"),
            ShipToAddress = Coalesce(row.ShipToAddress, repositoryScalars, extracted, "ShipToAddress"),
            PayToAddress = Coalesce(row.PayToAddress, repositoryScalars, extracted, "PayToAddress"),
            Fields = row.Fields
        };
    }

    private static Dictionary<string, string> ReadRepositoryFieldScalars(
        RepositoryDetailDto? repository,
        SqlDataReader? reader,
        HashSet<string>? tableColumns)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (repository == null || reader == null || tableColumns == null)
            return values;

        foreach (var field in repository.Fields)
        {
            if (!RepositoryItemTableColumns.Has(tableColumns, field.SqlColumnName))
                continue;
            if (!HasColumn(reader, field.SqlColumnName))
                continue;

            var raw = reader[reader.GetOrdinal(field.SqlColumnName)];
            if (raw == DBNull.Value || raw == null)
                continue;

            var text = Convert.ToString(raw, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            values[field.SqlColumnName] = text;
            if (!string.IsNullOrWhiteSpace(field.Name))
                values[field.Name] = text;
        }

        return values;
    }

    private static bool HasColumn(SqlDataReader reader, string columnName)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string? Coalesce(
        string? current,
        IReadOnlyDictionary<string, string> repositoryScalars,
        IReadOnlyDictionary<string, string> extracted,
        params string[] keys)
    {
        if (!string.IsNullOrWhiteSpace(current))
            return current;

        foreach (var key in keys)
        {
            var value = Get(repositoryScalars, key) ?? Get(extracted, key);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        foreach (var (fieldName, canonical) in FieldNameToListProperty)
        {
            if (!keys.Contains(canonical, StringComparer.OrdinalIgnoreCase))
                continue;

            var value = Get(repositoryScalars, fieldName);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static DateTime? CoalesceDate(
        DateTime? current,
        IReadOnlyDictionary<string, string> repositoryScalars,
        IReadOnlyDictionary<string, string> extracted,
        params string[] keys)
    {
        if (current.HasValue)
            return current;

        var raw = Coalesce(null, repositoryScalars, extracted, keys);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.Date
            : null;
    }

    private static decimal? CoalesceDecimal(
        decimal? current,
        IReadOnlyDictionary<string, string> repositoryScalars,
        IReadOnlyDictionary<string, string> extracted,
        params string[] keys)
    {
        if (current.HasValue)
            return current;

        var raw = Coalesce(null, repositoryScalars, extracted, keys);
        return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount)
            ? amount
            : null;
    }

    private static string? Get(IReadOnlyDictionary<string, string> extracted, string key) =>
        extracted.TryGetValue(key, out var value) ? value : null;
}
