namespace SaaSApp.Workflow.Application.Contracts;

/// <summary>Inserts dbo.notification rows for move-next (Ticket Submitted / Ticket Received).</summary>
public interface IWorkflowMoveNotificationService
{
    Task TryInsertMoveNotificationsAsync(
        WorkflowMoveNotificationContext context,
        CancellationToken cancellationToken = default);
}

public sealed record WorkflowMoveNotificationContext(
    Guid WorkflowId,
    Guid InstanceId,
    string WorkflowName,
    string? Review,
    string CurrentStageName,
    string? CurrentStageType,
    string? NextStageName,
    string? NextStageType,
    /// <summary>Transaction ModifiedBy for Ticket Submitted (who submitted the review).</summary>
    Guid SubmittedModifiedByUserId,
    /// <summary>Next transaction CreatedBy for Ticket Received; falls back to ModifiedBy when null.</summary>
    Guid? ReceivedCreatedByUserId);
