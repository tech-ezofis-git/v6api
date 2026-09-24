namespace SaaSApp.Workflow.Application.Contracts;

public interface IWorkflowInstanceHistoryService
{
    Task<WorkflowInstanceHistoryResult?> GetHistoryAsync(
        Guid workflowId,
        Guid instanceId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Instance timeline from <c>workflow.transaction_{suffix}</c> only (one row per transaction = one flow step).
/// </summary>
public sealed record WorkflowInstanceHistoryResult(
    Guid WorkflowId,
    Guid InstanceId,
    string WorkflowName,
    string? ReferenceNumber,
    int Status,
    string StatusName,
    int FlowCount,
    /// <summary>Assignee user id on the open (pending) transaction.</summary>
    Guid? NextActionUserId,
    /// <summary>Next action user email (open stage assignee).</summary>
    [property: System.Text.Json.Serialization.JsonPropertyName("nextActionUser")]
    string? NextActionUser,
    IReadOnlyList<WorkflowInstanceHistoryFlowDto> Flows);

/// <summary>
/// One flow step from a transaction row. <see cref="Action"/>: move (opened stage), submit (review), forward, complete (END).
/// <see cref="Milestone"/>: start | ap_agent | verified | approved | forwarded | completed | moved | submitted.
/// </summary>
public sealed record WorkflowInstanceHistoryFlowDto(
    int Sequence,
    int TransactionRowId,
    string Milestone,
    string Action,
    string Title,
    string? Description,
    /// <summary>Stage opened — transaction <c>CreatedAt</c>.</summary>
    DateTime OccurredAtUtc,
    /// <summary>Action performed — transaction <c>ModifiedAt</c> when <c>modifiedBy</c> acted; else CreatedAt.</summary>
    DateTime PerformedAtUtc,
    Guid? PerformedByUserId,
    /// <summary>Performer email from users.Users (matched by <see cref="PerformedByUserId"/>).</summary>
    string? PerformedByUserName,
    string? StageName,
    string? StageType,
    string? ActivityId,
    /// <summary>Transaction <c>ActivityUserId</c>.</summary>
    Guid? ActionUserId,
    /// <summary>Assigned action user email for this stage.</summary>
    [property: System.Text.Json.Serialization.JsonPropertyName("actionUser")]
    string? ActionUser,
    Guid? CreatedBy,
    /// <summary>Creator email from users.Users.</summary>
    string? CreatedByName,
    Guid? ModifiedBy,
    /// <summary>Modifier email from users.Users.</summary>
    string? ModifiedByName,
    string? Review,
    int ActionStatus);
