using System.Text.Json;
using System.Text.Json.Serialization;

namespace SaaSApp.Workflow.Application.Forms;

/// <summary>v5-compatible form entry upsert result (maps to resultforHTTpscode).</summary>
public sealed record FormEntryResult(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("output")] string? Output,
    [property: JsonPropertyName("EncryptOutput")] string? EncryptOutput,
    [property: JsonPropertyName("creditconsumed")] bool CreditConsumed = false);

/// <summary>Duplicate unique-column violation details (v5 output when id=3).</summary>
public sealed record FormEntryDuplicateField(string Id, string? Name, string? Value);

/// <summary>v5 insformentry — field map keyed by wFormControl jsonId.</summary>
public sealed class FormEntryUpsertRequest
{
    public Dictionary<string, JsonElement>? Fields { get; set; }
}

public enum FormEntryGetStatus
{
    Found = 1,
    NotFound = 0
}

public sealed record FormEntryGetResult(
    FormEntryGetStatus Status,
    IReadOnlyList<Dictionary<string, object?>>? Entries);

/// <summary>POST /api/form/{id}/entry/all — list/filter form entries (v5 parity).</summary>
public sealed record FormEntryAllRequest(
    FormAllSortBy? SortBy = null,
    List<FormAllFilterGroup>? FilterBy = null,
    int CurrentPage = 1,
    int ItemsPerPage = 20,
    string Mode = "browse",
    bool IncludeFormJson = true);

public sealed record FormEntryAllResult(
    FormEntryGetStatus Status,
    string FormId,
    IReadOnlyList<Dictionary<string, object?>>? Entries,
    FormAllMeta? Meta);

/// <summary>Row from dbo.wFormControl for a form (field definitions / form entry schema).</summary>
public sealed record FormControlItem(
    int Id,
    string WFormId,
    string? JsonId,
    string? Name,
    string? Type,
    bool IsMandatory,
    int ParentId,
    string? CreatedAt,
    string? ModifiedAt,
    string? CreatedBy,
    string? ModifiedBy,
    bool IsDeleted,
    string? ActivityBy,
    string? ActivityOn,
    int? ActivityId,
    string? ValidationJson,
    /// <summary>Exact dbo.ezfb_*_items physical column (e.g. Address, Address_2). Null on older rows.</summary>
    string? ColumnName = null);

public sealed record FormControlsResult(
    string FormId,
    int ControlCount,
    IReadOnlyList<FormControlItem> Controls);

public enum FormControlDistinctValuesStatus
{
    Found,
    FormNotFound,
    ControlNotFound,
    TableNotFound,
    ColumnNotFound
}

public sealed record FormControlDistinctValuesResult(
    FormControlDistinctValuesStatus Status,
    string? WFormId,
    string? WFormControlName,
    string? JsonId,
    string? ColumnName,
    IReadOnlyList<string>? Values);

public enum FormEntryByColumnStatus
{
    /// <summary>One or more entries matched (get), or exactly one entry was updated (update).</summary>
    Found = 1,
    /// <summary>No entry matched the given column value.</summary>
    NotFound = 0,
    /// <summary>formId/columnName/columnValue/fields failed validation, the column does not exist
    /// on this form, or (update only) more than one entry matched -- updating all of them
    /// unambiguously is refused rather than silently changing unrelated records.</summary>
    InvalidRequest = 2
}

/// <summary>
/// Optional AND filter for POST by-column. <see cref="ColumnName"/> / <see cref="ColumnId"/>
/// may be the control label, jsonId, or physical ezfb column (same resolution as the target column).
/// </summary>
public sealed record FormEntryColumnFilter(
    string? ColumnName = null,
    string? ColumnId = null,
    string? ColumnValue = null);

/// <summary>
/// Insert or update a master-form entry by column names/values (label, jsonId, or physical column).
/// When <see cref="MatchColumn"/> / <see cref="MatchFilters"/> is set, finds an existing row and
/// updates it; when no row matches, inserts. With no match key, always inserts.
/// </summary>
public sealed record FormEntryUpsertByColumnsRequest(
    Dictionary<string, JsonElement>? Fields = null,
    string? MatchColumn = null,
    string? MatchColumnId = null,
    string? MatchValue = null,
    IReadOnlyList<FormEntryColumnFilter>? MatchFilters = null);

/// <summary>
/// Load or update ezfb_{form}_items entries by an arbitrary column value instead of requiring
/// the caller to already know the item_id GUID (v5 master-form-by-column parity).
/// When get is called with columnName/columnId only (no columnValue), <see cref="Values"/> holds
/// every distinct non-empty value for that column (optionally narrowed by <c>filters</c>);
/// <see cref="Entries"/> is used when filtering by the target column's value.
/// </summary>
public sealed record FormEntryByColumnResult(
    FormEntryByColumnStatus Status,
    string? Message,
    string? FormId,
    string? ColumnName,
    string? ResolvedColumn,
    IReadOnlyList<Dictionary<string, object?>>? Entries,
    int UpdatedCount = 0,
    IReadOnlyList<string>? Values = null);
