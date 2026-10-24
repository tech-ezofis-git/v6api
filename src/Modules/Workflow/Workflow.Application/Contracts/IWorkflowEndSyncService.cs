namespace SaaSApp.Workflow.Application.Contracts;

/// <summary>
/// When a completed step's designer settings have <c>endSync.enabled</c> and
/// <c>target: MASTER_FORM</c>, copies mapped workflow form values onto the master form
/// (insert or update using the mapping marked <c>isMainField</c>).
/// </summary>
public interface IWorkflowEndSyncService
{
    Task TrySyncOnStepCompleteAsync(
        Guid workflowId,
        Guid instanceId,
        string activityId,
        string? workflowFormId,
        Guid? workflowFormEntryId,
        string? submittedFormDataJson,
        IReadOnlyDictionary<string, string>? formDataFields,
        CancellationToken cancellationToken = default);
}
