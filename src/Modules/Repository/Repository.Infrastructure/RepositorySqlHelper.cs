using Npgsql;
using System.Text;
using System.Text.RegularExpressions;

namespace SaaSApp.Repository.Infrastructure;

internal static class RepositorySqlHelper
{
    /// <summary>
    /// Fixed/reserved column names on every dynamic Items_/Stage table. These are entirely
    /// system-controlled (never user input), so -- per Decision 1's "dynamic DDL emits
    /// lowercase directly" and matching WorkflowTableCreator.cs's convention -- the actual
    /// physical columns are snake_case, unquoted. This set itself is compared case-
    /// insensitively (see the HashSet's comparer below) purely to keep collision checks
    /// against user-submitted field names working regardless of the casing a user types;
    /// it is NOT the literal DDL casing. See <see cref="ColumnRef"/>/ReservedColumnDdlNames
    /// below for the canonical snake_case DDL names used when emitting CREATE TABLE/trigger SQL.
    /// </summary>
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

    // Postgres port: table names are lowercase (system-controlled, dynamic-DDL-owned --
    // see the class doc comment above), replacing SQL Server's PascalCase Items_xxxxxxxx.
    public static string ItemsTableName(Guid repositoryId) => $"items_{ToSuffix(repositoryId)}";

    public static string StageTableName(Guid repositoryId) => $"items_{ToSuffix(repositoryId)}_stage";

    public static string HistoryTableName(Guid repositoryId) => $"items_{ToSuffix(repositoryId)}_history";

    public static string QualifiedItemsTable(string itemsTableName) => $"repository.{itemsTableName}";

    public static bool IsValidItemsTableName(string name) =>
        Regex.IsMatch(name, @"^items_[a-f0-9]{8}$", RegexOptions.IgnoreCase);

    public static bool IsValidStageTableName(string name) =>
        Regex.IsMatch(name, @"^items_[a-f0-9]{8}_stage$", RegexOptions.IgnoreCase);

    public static string SanitizeColumnName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Field name is required.");

        var cleaned = Regex.Replace(name.Trim(), @"[^a-zA-Z0-9_]", "");
        if (cleaned.Length == 0)
            throw new ArgumentException($"Invalid field name: {name}");

        if (char.IsDigit(cleaned[0]))
            cleaned = "F_" + cleaned;

        return cleaned;
    }

    // Custom (user-defined) columns keep their sanitized-but-original casing and are always
    // double-quoted in emitted SQL -- unlike the reserved columns, this text is user input
    // (RepositoryFields.Name -> Canonicalize), so it must NOT be silently lowercased by
    // Postgres's unquoted-identifier folding: every downstream query builder across
    // Repository.Infrastructure references columns by this exact string.
    public static string QuoteCustomColumn(string sanitizedName) => $"\"{sanitizedName}\"";

    /// <summary>
    /// Maps every historically-PascalCase reserved/fixed column literal used throughout
    /// Repository.Infrastructure's SQL builders to its actual Postgres physical name (see
    /// StaticRepositoryProvisioner's BuildItemsTableScript/BuildStageTableScript -- snake_case,
    /// unquoted, system-controlled). Not every entry here is necessarily a live column on
    /// every table (e.g. PromotedItemId only exists on Stage; several -- ArchivedFrom,
    /// Revision, EncryptPassword, etc. -- were reserved-but-unused already in the original
    /// SQL Server ReservedItemColumns set); harmless to map them all uniformly.
    /// </summary>
    private static readonly Dictionary<string, string> ReservedColumnDdlNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Id"] = "id",
        ["TenantId"] = "tenant_id",
        ["RepositoryId"] = "repository_id",
        ["FolderId"] = "folder_id",
        ["StorageProviderId"] = "storage_provider_id",
        ["FilePath"] = "file_path",
        ["FileName"] = "file_name",
        ["FileType"] = "file_type",
        ["FileSize"] = "file_size",
        ["TotalPages"] = "total_pages",
        ["IsVerified"] = "is_verified",
        ["ArchivedFrom"] = "archived_from",
        ["ArchivedAt"] = "archived_at",
        ["FileVersion"] = "file_version",
        ["Revision"] = "revision",
        ["StageStatus"] = "stage_status",
        ["Status"] = "status",
        ["MailId"] = "mail_id",
        ["SummaryJson"] = "summary_json",
        ["OcrText"] = "ocr_text",
        ["OcrJson"] = "ocr_json",
        ["OcrScore"] = "ocr_score",
        ["AiStatus"] = "ai_status",
        ["ActiveItem"] = "active_item",
        ["WorkflowInstanceId"] = "workflow_instance_id",
        ["EncryptPassword"] = "encrypt_password",
        ["EncryptStatus"] = "encrypt_status",
        ["EncryptedBy"] = "encrypted_by",
        ["ActivityBy"] = "activity_by",
        ["ActivityOn"] = "activity_on",
        ["ActivityId"] = "activity_id",
        ["CreatedAtUtc"] = "created_at_utc",
        ["ModifiedAtUtc"] = "modified_at_utc",
        ["CreatedBy"] = "created_by",
        ["ModifiedBy"] = "modified_by",
        ["IsDeleted"] = "is_deleted",
        ["ValidFrom"] = "valid_from",
        ["ValidTo"] = "valid_to",
        ["PromotedItemId"] = "promoted_item_id",
    };

    /// <summary>
    /// The single point every Repository.Infrastructure SQL builder should go through to
    /// reference a column by its logical (historically-PascalCase) name: reserved/fixed
    /// columns resolve to their unquoted snake_case physical name, everything else is
    /// assumed to be a user-defined custom column and is double-quoted with its casing
    /// preserved as given (callers should pass the canonical stored name, e.g. from
    /// <see cref="RepositoryItemTableColumns.TryGetCanonicalName"/>, not raw user input).
    /// </summary>
    public static string ColumnRef(string logicalColumnName) =>
        ReservedColumnDdlNames.TryGetValue(logicalColumnName, out var physical)
            ? physical
            : QuoteCustomColumn(logicalColumnName);

    /// <summary>
    /// Unquoted physical name only (no SQL-embedding quotes) -- for membership/equality checks
    /// against <see cref="RepositoryItemTableColumns"/>'s physical column set, which is what
    /// <see cref="RepositoryItemTableColumns.Has"/>/TryGetCanonicalName route every lookup
    /// through. Reserved logical names (however cased) resolve to their physical snake_case
    /// form; anything else (a custom column, or an already-physical name) passes through
    /// unchanged.
    /// </summary>
    public static string ToPhysicalName(string logicalOrPhysicalName) =>
        ReservedColumnDdlNames.TryGetValue(logicalOrPhysicalName, out var physical)
            ? physical
            : logicalOrPhysicalName;

    // Reverse of ReservedColumnDdlNames: physical snake_case -> historical PascalCase logical
    // name -- used by SelectColumn to alias query results back to the names a large amount of
    // pre-migration C# (dictionaries, DataReader-by-name) still reads by literal PascalCase key.
    private static readonly Dictionary<string, string> ReservedColumnLogicalNames =
        ReservedColumnDdlNames
            .GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Builds a `physical_col AS "LogicalName"` SELECT-list entry for a physical column name
    /// (as returned by <see cref="RepositoryItemTableColumns.LoadAsync"/>). Reserved columns
    /// alias back to their historical PascalCase name; custom columns alias to their own
    /// (already original-case) name for symmetry. Use this instead of `SELECT alias.*`
    /// anywhere a query's results are read by column name (dictionary, GetOrdinal) rather
    /// than purely by position -- `SELECT *` would otherwise return reserved columns under
    /// their new lowercase snake_case names and silently break every PascalCase-keyed lookup
    /// downstream.
    /// </summary>
    public static string SelectColumn(string physicalColumnName, string? tableAlias = null)
    {
        var prefix = string.IsNullOrEmpty(tableAlias) ? string.Empty : $"{tableAlias}.";
        if (ReservedColumnLogicalNames.TryGetValue(physicalColumnName, out var pascalName))
            return $"{prefix}{physicalColumnName} AS \"{pascalName}\"";

        return $"{prefix}{QuoteCustomColumn(physicalColumnName)} AS {QuoteCustomColumn(physicalColumnName)}";
    }

    public static string BuildAliasedSelectList(IEnumerable<string> physicalColumns, string? tableAlias = null) =>
        string.Join(", ", physicalColumns.Select(c => SelectColumn(c, tableAlias)));

    /// <summary>
    /// SQL-embeddable reference for a column name already known to be PHYSICAL (e.g. returned
    /// by <see cref="RepositoryItemTableColumns.TryGetCanonicalName"/>) -- NOT a logical
    /// PascalCase literal. Unlike <see cref="ColumnRef"/> (which expects a logical name and
    /// would incorrectly quote an already-lowercase reserved physical name like "tenant_id",
    /// since it isn't a key in the PascalCase-keyed reserved-name map), this checks the
    /// physical-name reverse map instead: reserved physical names stay unquoted, everything
    /// else is treated as a custom column and quoted with its casing preserved.
    /// </summary>
    public static string PhysicalColumnRef(string physicalColumnName) =>
        ReservedColumnLogicalNames.ContainsKey(physicalColumnName)
            ? physicalColumnName
            : QuoteCustomColumn(physicalColumnName);

    public static string MapDataTypeToSql(string? dataType) => (dataType ?? "text").Trim().ToLowerInvariant() switch
    {
        "number" or "decimal" or "amount" => "decimal(18,2) NULL",
        "int" or "integer" => "integer NULL",
        "date" or "datetime" => "date NULL",
        "bit" or "bool" or "boolean" => "boolean NULL",
        _ => "text NULL"
    };

    /// <summary>
    /// Returns a LOGICAL column name (historical PascalCase reserved name, or a custom
    /// column's own name) -- not SQL-ready text. Callers resolve it to a physical reference
    /// via <see cref="ColumnRef"/> (for SQL embedding) or <see cref="ToPhysicalName"/> (for
    /// tableColumns membership checks), same as every other logical-name producer in this file.
    /// </summary>
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

    /// <summary>Each NpgsqlCommand needs its own parameter instances (cannot reuse AddRange across commands).</summary>
    public static void AddParameters(NpgsqlCommand cmd, IEnumerable<NpgsqlParameter> parameters)
    {
        foreach (var p in parameters)
            cmd.Parameters.Add(CloneParameter(p));
    }

    public static NpgsqlParameter CloneParameter(NpgsqlParameter p) =>
        new(p.ParameterName, p.Value ?? DBNull.Value) { NpgsqlDbType = p.NpgsqlDbType };
}
