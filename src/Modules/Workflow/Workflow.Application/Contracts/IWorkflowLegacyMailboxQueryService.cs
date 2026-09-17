using System.Text.Json.Serialization;
using SaaSApp.Workflow.Application.Workflows;

namespace SaaSApp.Workflow.Application.Contracts;

public enum LegacyMailboxTableKind
{
    Inbox,
    Sent,
    Completed
}

public sealed record LegacyMailboxListRequest(
    LegacyMailboxTableKind Kind,
    Guid WorkflowId,
    Guid? InstanceId,
    string? TransactionId,
    Guid CurrentUserId,
    int PageNumber = 1,
    int PageSize = 20,
    /// <summary>When true, skips COUNT(*) for faster paging (TotalCount is -1).</summary>
    bool SkipTotal = false);

public sealed record LegacyMailboxListResult(
    IReadOnlyList<LegacyMailboxRowDto> Items,
    int TotalCount,
    int PageNumber,
    int PageSize,
    bool TableExists);

public sealed record LegacyMailboxRowDto(
    int Id,
    string? UserId,
    int? GroupId,
    string? WorkflowId,
    string? Name,
    string? WorkflowInstanceId,
    string? ReferenceNumber,
    DateTime? CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    string? Context,
    string? TransactionId,
    string? ActivityId,
    string? RuleId,
    string? StageType,
    string? Stage,
    string? Review,
    DateTime? TransactionCreatedAt,
    string? TransactionCreatedBy,
    string? TransactionCreatedByEmail,
    DateTime? TransactionModifiedAt,
    string? TransactionModifiedBy,
    string? RepositoryId,
    string? ItemId,
    string? FormId,
    string? FormEntryId,
    [property: JsonConverter(typeof(RawJsonStringConverter))]
    string? FormData,
    string? MlPrediction,
    string? MlCondition,
    string? UserType,
    string? CreatedByName,
    string? LastActionStageType,
    string? LastActionStageName,
    string? LastAction,
    int? CommentsCount,
    int? AttachmentCount,
    string? ActivityUserEmail,
    string? ActivityGroupName,
    string? AgentValidationWorkflowId = null,
    string? AgentResponse = null,
    string? AgentHtml = null,
    /// <summary>1 = show verify/approve buttons; 0 = hide. Default 1.</summary>
    int Action = 1);

public sealed record LegacyMailboxInstanceCountRequest(
    Guid WorkflowId,
    Guid CurrentUserId);

public sealed record LegacyMailboxInstanceCountResult(
    Guid WorkflowId,
    int InboxCount,
    int SentCount,
    int CompletedCount,
    bool InboxTableExists,
    bool SentTableExists,
    bool CompletedTableExists);

public interface IWorkflowLegacyMailboxQueryService
{
    Task<LegacyMailboxListResult> ListAsync(LegacyMailboxListRequest request, CancellationToken cancellationToken = default);

    Task<LegacyMailboxInstanceCountResult> GetInstanceCountsAsync(
        LegacyMailboxInstanceCountRequest request,
        CancellationToken cancellationToken = default);
}
