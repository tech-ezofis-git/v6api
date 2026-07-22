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

public sealed record WorkflowTicketSearchFilter(
    string Criteria,
    string Condition,
    string? Value);

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
