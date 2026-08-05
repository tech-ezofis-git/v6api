using System.Globalization;
using Microsoft.Data.SqlClient;
using SaaSApp.Repository.Application;
using SaaSApp.Repository.Application.Contracts;
using SaaSApp.Repository.Infrastructure.Storage;

namespace SaaSApp.Repository.Infrastructure;

internal static class RepositoryItemListReader
{
    private static readonly string[] OptionalListColumns =
    [
        "DocumentType", "Supplier", "InvoiceNumber", "PoNumber", "DocumentDate",
        "Amount", "Currency", "RiskLevel", "Source", "Department", "Status", "OcrScore", "AiStatus",
        "FileVersion", "InvoiceDate", "InvoiceAmount", "InvoiceTaxAmount", "PODate", "POAmount",
        "Buyer", "Terms", "SupplierAddress", "ShipToAddress", "PayToAddress", "FileType"
    ];

    private static readonly string[] JsonListColumns = ["OcrJson", "SummaryJson"];

    private static readonly string[][] ScalarAliasGroups =
    [
        ["Supplier", "VendorName", "Vendor"],
        ["InvoiceNumber", "InvoiceNo"],
        ["DocumentDate", "InvoiceDate", "PODate", "Date"],
        ["Amount", "InvoiceAmount", "POAmount"],
        ["AiStatus", "MatchedStatus"],
        ["Status", "StageStatus"],
    ];

    private static readonly Dictionary<string, string> SqlColumnToListProperty =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["InvoiceNumber"] = "InvoiceNumber",
            ["InvoiceNo"] = "InvoiceNumber",
            ["Supplier"] = "Supplier",
            ["VendorName"] = "Supplier",
            ["Vendor"] = "Supplier",
            ["PoNumber"] = "PoNumber",
            ["PONumber"] = "PoNumber",
            ["DocumentDate"] = "DocumentDate",
            ["InvoiceDate"] = "DocumentDate",
            ["PODate"] = "DocumentDate",
            ["Date"] = "DocumentDate",
            ["Amount"] = "Amount",
            ["InvoiceAmount"] = "Amount",
            ["POAmount"] = "Amount",
            ["InvoiceTaxAmount"] = "InvoiceTaxAmount",
            ["DocumentType"] = "DocumentType",
            ["Currency"] = "Currency",
            ["Buyer"] = "Buyer",
            ["Terms"] = "Terms",
            ["SupplierAddress"] = "SupplierAddress",
            ["ShipToAddress"] = "ShipToAddress",
            ["PayToAddress"] = "PayToAddress",
            ["Status"] = "Status",
            ["StageStatus"] = "Status",
            ["OcrScore"] = "OcrScore",
            ["AiStatus"] = "AiStatus",
            ["MatchedStatus"] = "AiStatus",
            ["RiskLevel"] = "RiskLevel",
            ["Source"] = "Source",
            ["Department"] = "Department",
        };

    public static string BuildSelectList(HashSet<string> tableColumns, RepositoryDetailDto? repository = null)
    {
        var parts = new List<string> { "i.Id", "i.FileName" };
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Id", "FileName" };

        void AddColumn(string column)
        {
            if (!RepositoryItemTableColumns.Has(tableColumns, column) || !added.Add(column))
                return;

            parts.Add($"i.[{column}]");
        }

        foreach (var col in OptionalListColumns)
            AddColumn(col);

        foreach (var group in ScalarAliasGroups)
        {
            foreach (var col in group)
                AddColumn(col);
        }

        foreach (var col in JsonListColumns)
            AddColumn(col);

        if (repository != null)
        {
            // Always select this repository's configured fields (HR, custom, AP, …).
            foreach (var field in repository.Fields)
                AddColumn(field.SqlColumnName);
        }

        parts.Add("i.StorageProviderId");
        parts.Add("sp.Code");
        AddColumn("FilePath");
        AddColumn("WorkflowInstanceId");
        AddColumn("CreatedBy");

        return string.Join(", ", parts);
    }

    public static RepositoryItemListDto ReadRow(
        SqlDataReader reader,
        HashSet<string> tableColumns,
        RepositoryDetailDto? repository = null)
    {
        var id = reader.GetGuid(reader.GetOrdinal("Id"));
        var rawFileName = GetString(reader, "FileName");
        var filePath = GetString(reader, tableColumns, "FilePath");
        var fileType = GetString(reader, tableColumns, "FileType");
        var fileName = RepositoryFilePathHelper.EnsureFileNameHasExtension(rawFileName, fileType, filePath);
        var ocrJson = GetString(reader, tableColumns, "OcrJson");
        var summaryJson = GetString(reader, tableColumns, "SummaryJson");
        Guid? workflowInstanceId = null;
        if (RepositoryItemTableColumns.Has(tableColumns, "WorkflowInstanceId")
            && !reader.IsDBNull(reader.GetOrdinal("WorkflowInstanceId")))
        {
            workflowInstanceId = reader.GetGuid(reader.GetOrdinal("WorkflowInstanceId"));
        }

        Guid? createdByUserId = null;
        if (RepositoryItemTableColumns.Has(tableColumns, "CreatedBy")
            && !reader.IsDBNull(reader.GetOrdinal("CreatedBy")))
        {
            var ordinal = reader.GetOrdinal("CreatedBy");
            if (reader.GetFieldType(ordinal) == typeof(Guid))
                createdByUserId = reader.GetGuid(ordinal);
            else if (Guid.TryParse(reader.GetValue(ordinal)?.ToString(), out var parsedCreatedBy))
                createdByUserId = parsedCreatedBy;
        }

        var row = new RepositoryItemListDto(
            id,
            fileName,
            GetInt32(reader, tableColumns, "FileVersion"),
            CoalesceString(reader, tableColumns, "DocumentType"),
            CoalesceString(reader, tableColumns, "Supplier", "VendorName", "Vendor"),
            CoalesceString(reader, tableColumns, "InvoiceNumber", "InvoiceNo"),
            CoalesceString(reader, tableColumns, "PoNumber", "PONumber"),
            CoalesceDateTime(reader, tableColumns, "DocumentDate", "InvoiceDate", "PODate", "Date"),
            CoalesceDecimal(reader, tableColumns, "Amount", "InvoiceAmount", "POAmount"),
            CoalesceString(reader, tableColumns, "Currency"),
            CoalesceString(reader, tableColumns, "Status", "StageStatus"),
            CoalesceByte(reader, tableColumns, "OcrScore"),
            CoalesceString(reader, tableColumns, "AiStatus", "MatchedStatus"),
            CoalesceString(reader, tableColumns, "RiskLevel"),
            CoalesceString(reader, tableColumns, "Source"),
            CoalesceString(reader, tableColumns, "Department"),
            CoalesceDateTime(reader, tableColumns, "InvoiceDate", "DocumentDate", "Date"),
            CoalesceDecimal(reader, tableColumns, "InvoiceAmount", "Amount"),
            CoalesceDecimal(reader, tableColumns, "InvoiceTaxAmount"),
            CoalesceDateTime(reader, tableColumns, "PODate"),
            CoalesceDecimal(reader, tableColumns, "POAmount"),
            CoalesceString(reader, tableColumns, "Buyer"),
            CoalesceString(reader, tableColumns, "Terms"),
            CoalesceString(reader, tableColumns, "SupplierAddress"),
            CoalesceString(reader, tableColumns, "ShipToAddress"),
            CoalesceString(reader, tableColumns, "PayToAddress"),
            reader.GetGuid(reader.GetOrdinal("StorageProviderId")),
            reader.GetString(reader.GetOrdinal("Code")),
            !string.IsNullOrWhiteSpace(filePath),
            workflowInstanceId,
            CreatedBy: createdByUserId?.ToString("D"), // resolved to email after list load
            CreatedByUserId: createdByUserId,
            BuildRepositoryFields(reader, tableColumns, repository));

        return RepositoryItemListMetadataEnricher.Enrich(row, ocrJson, summaryJson, repository, reader, tableColumns);
    }

    /// <summary>Repository-configured field values for list grid — camelCase sqlColumnName keys only.</summary>
    public static IReadOnlyDictionary<string, object?> BuildRepositoryFields(
        SqlDataReader reader,
        HashSet<string> tableColumns,
        RepositoryDetailDto? repository)
    {
        var fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (repository == null)
            return fields;

        foreach (var field in repository.Fields)
        {
            var column = field.SqlColumnName;
            if (string.IsNullOrWhiteSpace(column))
                continue;
            if (!RepositoryItemTableColumns.Has(tableColumns, column) || !HasColumn(reader, column))
                continue;

            var ordinal = reader.GetOrdinal(column);
            object? value = reader.IsDBNull(ordinal) ? null : NormalizeFieldValue(reader.GetValue(ordinal));
            fields[ToCamelCase(column)] = value;
        }

        return fields;
    }

    /// <summary>
    /// Set top-level <c>createdBy</c> (and fields.createdBy) to user email for FE grid display.
    /// Keeps <c>createdByUserId</c> for ACL.
    /// </summary>
    public static async Task ResolveCreatedByEmailsAsync(
        SqlConnection connection,
        IList<RepositoryItemListDto> items,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
            return;

        var userIds = items
            .Select(i => i.CreatedByUserId)
            .Where(id => id is Guid g && g != Guid.Empty)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        if (userIds.Count == 0)
            return;

        var emails = await RepositoryUserNameResolver.ResolveDisplayNamesAsync(
            connection, userIds, cancellationToken);

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.CreatedByUserId is not Guid uid || uid == Guid.Empty)
                continue;
            if (!emails.TryGetValue(uid, out var email) || string.IsNullOrWhiteSpace(email))
                continue;

            IReadOnlyDictionary<string, object?>? fields = item.Fields;
            if (fields != null && fields.Count > 0)
            {
                var updated = new Dictionary<string, object?>(fields, StringComparer.OrdinalIgnoreCase)
                {
                    ["createdBy"] = email
                };
                fields = updated;
            }

            items[i] = item with { CreatedBy = email, Fields = fields };
        }
    }

    private static object? NormalizeFieldValue(object value) => value switch
    {
        DateTime dt => dt,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        decimal or double or float or int or long or short or byte or bool or string => value,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return name;
        var trimmed = name.Trim();
        if (trimmed.Length == 1)
            return trimmed.ToLowerInvariant();
        return char.ToLowerInvariant(trimmed[0]) + trimmed[1..];
    }

    public static string? ResolveDateFilterColumn(HashSet<string> tableColumns, RepositoryDetailDto repo)
    {
        if (RepositoryItemTableColumns.Has(tableColumns, "DocumentDate"))
            return "DocumentDate";

        if (RepositoryItemTableColumns.Has(tableColumns, "InvoiceDate"))
            return "InvoiceDate";

        if (RepositoryItemTableColumns.Has(tableColumns, "PODate"))
            return "PODate";

        if (RepositoryItemTableColumns.Has(tableColumns, "Date"))
            return "Date";

        var dateField = repo.Fields.FirstOrDefault(f =>
            string.Equals(f.DataType, "date", StringComparison.OrdinalIgnoreCase)
            || string.Equals(f.DataType, "datetime", StringComparison.OrdinalIgnoreCase));

        if (dateField != null && RepositoryItemTableColumns.Has(tableColumns, dateField.SqlColumnName))
            return dateField.SqlColumnName;

        return null;
    }

    private static string? CoalesceString(SqlDataReader reader, HashSet<string> columns, params string[] names)
    {
        foreach (var name in names)
        {
            var value = GetString(reader, columns, name);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static DateTime? CoalesceDateTime(SqlDataReader reader, HashSet<string> columns, params string[] names)
    {
        foreach (var name in names)
        {
            var value = GetDateTime(reader, columns, name);
            if (value.HasValue)
                return value;
        }

        return null;
    }

    private static decimal? CoalesceDecimal(SqlDataReader reader, HashSet<string> columns, params string[] names)
    {
        foreach (var name in names)
        {
            var value = GetDecimal(reader, columns, name);
            if (value.HasValue)
                return value;
        }

        return null;
    }

    private static byte? CoalesceByte(SqlDataReader reader, HashSet<string> columns, params string[] names)
    {
        foreach (var name in names)
        {
            var value = GetByte(reader, columns, name);
            if (value.HasValue)
                return value;
        }

        return null;
    }

    private static string? GetString(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
            return null;

        var value = reader.GetValue(ordinal);
        if (value is string text)
            return string.IsNullOrWhiteSpace(text) ? null : text;

        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static string? GetString(SqlDataReader reader, HashSet<string> columns, string column) =>
        RepositoryItemTableColumns.Has(columns, column) && HasColumn(reader, column) ? GetString(reader, column) : null;

    private static DateTime? GetDateTime(SqlDataReader reader, HashSet<string> columns, string column)
    {
        if (!RepositoryItemTableColumns.Has(columns, column))
            return null;
        if (!HasColumn(reader, column))
            return null;

        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
            return null;

        return TryToDateTime(reader.GetValue(ordinal));
    }

    private static decimal? GetDecimal(SqlDataReader reader, HashSet<string> columns, string column)
    {
        if (!RepositoryItemTableColumns.Has(columns, column))
            return null;
        if (!HasColumn(reader, column))
            return null;

        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
            return null;

        return TryToDecimal(reader.GetValue(ordinal));
    }

    private static byte? GetByte(SqlDataReader reader, HashSet<string> columns, string column)
    {
        if (!RepositoryItemTableColumns.Has(columns, column))
            return null;
        if (!HasColumn(reader, column))
            return null;

        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
            return null;

        return TryToByte(reader.GetValue(ordinal));
    }

    private static int? GetInt32(SqlDataReader reader, HashSet<string> columns, string column)
    {
        if (!RepositoryItemTableColumns.Has(columns, column))
            return null;
        if (!HasColumn(reader, column))
            return null;

        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
            return null;

        return TryToInt32(reader.GetValue(ordinal));
    }

    private static decimal? TryToDecimal(object value) => value switch
    {
        decimal d => d,
        double d => (decimal)d,
        float f => (decimal)f,
        int i => i,
        long l => l,
        short s => s,
        byte b => b,
        string text when TryParseDecimal(text, out var parsed) => parsed,
        _ => null
    };

    private static DateTime? TryToDateTime(object value)
    {
        if (value is DateTime dt)
            return dt;

        if (value is DateOnly date)
            return date.ToDateTime(TimeOnly.MinValue);

        if (value is not string text)
            return null;

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed;

        return DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeUniversal, out parsed)
            ? parsed
            : null;
    }

    private static byte? TryToByte(object value) => value switch
    {
        byte b => b,
        short s when s is >= 0 and <= byte.MaxValue => (byte)s,
        int i when i is >= 0 and <= byte.MaxValue => (byte)i,
        string text when byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        string text when decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var dec) => (byte)Math.Clamp(dec <= 1 ? dec * 100 : dec, 0, 255),
        _ => null
    };

    private static int? TryToInt32(object value) => value switch
    {
        int i => i,
        short s => s,
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null
    };

    private static bool TryParseDecimal(string text, out decimal parsed)
    {
        if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed))
            return true;

        return decimal.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out parsed);
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
}
