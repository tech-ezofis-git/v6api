namespace SaaSApp.Workflow.Application.Contracts;

/// <summary>Inserts a dbo.notification row for move-next (Ticket Received only).</summary>
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
    /// <summary>Current transaction ModifiedBy; used only if the received actor cannot be resolved.</summary>
    Guid SubmittedModifiedByUserId,
    /// <summary>Next transaction CreatedBy for Ticket Received; falls back to ModifiedBy when null.</summary>
    Guid? ReceivedCreatedByUserId,
    string? RequestNo = null,
    int? CurrentTransactionId = null,
    int? NextTransactionId = null);
