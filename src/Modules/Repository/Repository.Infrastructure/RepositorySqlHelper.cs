using Microsoft.Data.SqlClient;
using System.Text;
using System.Text.RegularExpressions;

namespace SaaSApp.Repository.Infrastructure;

internal static class RepositorySqlHelper
{
    public static readonly HashSet<string> ReservedItemColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "Id", "TenantId", "RepositoryId", "FolderId", "StorageProviderId",
        "FilePath", "FileName", "FileType", "FileSize", "TotalPages",
        "IsVerified", "ArchivedFrom", "ArchivedAt", "FileVersion", "Revision",
        "StageStatus", "Status", "MailId", "SummaryJson", "OcrText", "OcrJson", "OcrScore", "AiStatus",
        "ActiveItem", "WorkflowInstanceId", "EncryptPassword", "EncryptStatus", "EncryptedBy",
        "ActivityBy", "ActivityOn", "ActivityId",
        "CreatedAtUtc", "ModifiedAtUtc", "CreatedBy", "ModifiedBy", "IsDeleted",
        "ValidFrom", "ValidTo"
    };

    public static string ToSuffix(Guid repositoryId) => repositoryId.ToString("N")[..8];

    public static string ItemsTableName(Guid repositoryId) => $"Items_{ToSuffix(repositoryId)}";

    public static string StageTableName(Guid repositoryId) => $"Items_{ToSuffix(repositoryId)}Stage";

    public static string HistoryTableName(Guid repositoryId) => $"Items_{ToSuffix(repositoryId)}History";

    public static string QualifiedItemsTable(string itemsTableName) => $"repository.[{itemsTableName}]";

    public static bool IsValidItemsTableName(string name) =>
        Regex.IsMatch(name, @"^Items_[a-f0-9]{8}$", RegexOptions.IgnoreCase);

    public static bool IsValidStageTableName(string name) =>
        Regex.IsMatch(name, @"^Items_[a-f0-9]{8}Stage$", RegexOptions.IgnoreCase);

    public static string SanitizeColumnName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Field name is required.");

        var trimmed = name.Trim();
        // Allow Unicode letters/digits (Arabic, etc.) + underscore. Strip spaces/punctuation only.
        var cleaned = Regex.Replace(trimmed, @"[^\p{L}\p{N}_]", "");
        if (cleaned.Length == 0)
        {
            // Emoji-only / symbol labels: stable ASCII fallback from the display name.
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(trimmed)))[..8];
            cleaned = "F_" + hash;
        }

        // SQL identifiers cannot start with a digit.
        if (char.IsDigit(cleaned[0]) || !char.IsLetter(cleaned[0]))
            cleaned = "F_" + cleaned;

        if (cleaned.Length > 128)
            cleaned = cleaned[..128];

        return cleaned;
    }

    public static string MapDataTypeToSql(string? dataType) => (dataType ?? "text").Trim().ToLowerInvariant() switch
    {
        "number" or "decimal" or "amount" => "DECIMAL(18,2) NULL",
        "int" or "integer" => "INT NULL",
        "date" or "datetime" => "DATE NULL",
        "bit" or "bool" or "boolean" => "BIT NULL",
        _ => "NVARCHAR(MAX) NULL"
    };

    public static string MapSortColumn(string sortBy) => sortBy.Trim().ToLowerInvariant() switch
    {
        "filename" or "name" => "FileName",
        "status" or "stagestatus" => "Status",
        "documentdate" or "invoicedate" or "podate" => "DocumentDate",
        "supplier" or "vendorname" or "vendor" => "Supplier",
        "invoicenumber" or "invoiceno" => "InvoiceNumber",
        "ponumber" => "PoNumber",
        "amount" or "invoiceamount" or "poamount" => "Amount",
        "documenttype" => "DocumentType",
        "currency" => "Currency",
        "ocrpercent" or "ocrscore" => "OcrScore",
        "aistatus" or "matchedstatus" => "AiStatus",
        "risklevel" => "RiskLevel",
        "source" => "Source",
        "department" => "Department",
        "fileversion" => "FileVersion",
        "createdatutc" => "CreatedAtUtc",
        "modifiedatutc" => "ModifiedAtUtc",
        _ => "CreatedAtUtc"
    };

    public static string MapFacetColumn(string fieldName)
    {
        var col = SanitizeColumnName(fieldName);
        if (!ReservedItemColumns.Contains(col))
            throw new ArgumentException($"Unknown facet field: {fieldName}");
        return col;
    }

    /// <summary>Each SqlCommand needs its own parameter instances (cannot reuse AddRange across commands).</summary>
    public static void AddParameters(SqlCommand cmd, IEnumerable<SqlParameter> parameters)
    {
        foreach (var p in parameters)
            cmd.Parameters.Add(CloneParameter(p));
    }

    public static SqlParameter CloneParameter(SqlParameter p) =>
        new(p.ParameterName, p.Value ?? DBNull.Value) { SqlDbType = p.SqlDbType };
}
