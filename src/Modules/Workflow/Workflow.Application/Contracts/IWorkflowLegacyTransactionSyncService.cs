using SaaSApp.Workflow.Domain.Entities;

namespace SaaSApp.Workflow.Application.Contracts;

/// <summary>
/// Legacy transaction sync for move-next (activityId + optional review).
/// </summary>
public interface IWorkflowLegacyTransactionSyncService
{
    Task<WorkflowLegacyTransactionSyncResult> SyncTransactionByActivityIdAsync(
        Guid workflowId,
        Guid workflowInstanceId,
        string? referenceNumber,
        WorkflowStep targetStep,
        IReadOnlyList<WorkflowStep> orderedSteps,
        string activityId,
        Guid userId,
        Guid? activityUserId,
        string? review,
        MailboxFormSnapshot? mailboxForm = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Legacy process FlowStatus for the instance (0 = running, 1 = completed), or null if no process row exists.
    /// </summary>
    Task<int?> GetLegacyProcessFlowStatusAsync(
        Guid workflowId,
        Guid workflowInstanceId,
        string? referenceNumber,
        CancellationToken cancellationToken = default);
}

public enum LegacyTransactionSyncStatus
{
    StepInserted,
    StepAlreadyThere,
    ReviewUpdated,
    ReviewAlreadyUpdated
}

public sealed record WorkflowLegacyTransactionSyncResult(
    LegacyTransactionSyncStatus Status,
    Guid WorkflowInstanceId,
    int? CurrentTransactionId,
    int? NextTransactionId,
    Guid? NextTransactionGuid,
    bool WorkflowCompleted = false,
    /// <summary>Assignee of the newly opened next step (when returned to share owner after guest move-next).</summary>
    Guid? NextActivityUserId = null,
    /// <summary>CreatedBy of the newly opened next transaction row.</summary>
    Guid? NextCreatedByUserId = null);
