using System.Text.Json;

namespace SaaSApp.Workflow.Application.Contracts;

public sealed record WorkflowTicketFilterFieldDto(
    string Name,
    string SqlColumnName,
    string? DataType,
    IReadOnlyList<string> SupportedOperators);

public sealed record WorkflowTicketFilterSchemaDto(
    Guid WorkflowId,
    string? FormId,
    IReadOnlyList<WorkflowTicketFilterFieldDto> Fields);

/// <summary>
/// One filter clause for POST .../filter/search.
/// <list type="bullet">
/// <item><description><c>dataType</c> date: <c>value</c> is a string; use <c>valueTo</c> with condition <c>between</c> for ranges.</description></item>
/// <item><description>Other dataTypes: <c>value</c> may be a string/number or a JSON array (e.g. for <c>in</c>).</description></item>
/// <item><description>Legacy clients may still send <c>value</c> as a plain string without <c>dataType</c>/<c>valueTo</c>.</description></item>
/// </list>
/// </summary>
public sealed record WorkflowTicketSearchFilter(
    string Criteria,
    string Condition,
    JsonElement Value = default,
    string? ValueTo = null,
    string? DataType = null);

public sealed record WorkflowTicketSearchSortBy(
    string Criteria = "raisedAt",
    string Order = "DESC");

public sealed record WorkflowTicketSearchRequest(
    IReadOnlyList<WorkflowTicketSearchFilter>? FilterBy = null,
    WorkflowTicketSearchSortBy? SortBy = null,
    string GroupBy = "",
    int CurrentPage = 1,
    int ItemsPerPage = 20);

public enum WorkflowTicketSearchStatus
{
    Found,
    WorkflowNotFound,
    FormNotConfigured,
    TablesMissing
}

public sealed record WorkflowTicketSearchOutcome(
    WorkflowTicketSearchStatus Status,
    WorkflowFilterSearchResult? Result);

public sealed record WorkflowFilterSearchGroup(
    string Key,
    IReadOnlyList<LegacyMailboxRowDto> Value);

public sealed record WorkflowFilterSearchMeta(
    int CurrentPage,
    int ItemsPerPage,
    int TotalItems);

public sealed record WorkflowFilterSearchResult(
    IReadOnlyList<WorkflowFilterSearchGroup> Data,
    WorkflowFilterSearchMeta Meta,
    bool TableExists);
