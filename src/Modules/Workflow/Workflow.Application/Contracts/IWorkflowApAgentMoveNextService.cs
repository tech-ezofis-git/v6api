using SaaSApp.Workflow.Application.Workflows.Commands.MoveToNextStep;
using SaaSApp.Workflow.Domain.Entities;

namespace SaaSApp.Workflow.Application.Contracts;

/// <summary>AP agent stage: persist validation, bind ezfb from repository, after Approve move-next.</summary>
public interface IWorkflowApAgentMoveNextService
{
    Task SaveAgentValidationAsync(
        Guid workflowId,
        Guid workflowInstanceId,
        WorkflowStep apAgentStep,
        Guid userId,
        MoveToNextStepApAgentPayload payload,
        int? legacyTransactionId,
        CancellationToken cancellationToken = default);

    Task BindEzfbFromRepositoryAsync(
        Guid tenantId,
        MoveToNextStepApAgentPayload payload,
        CancellationToken cancellationToken = default);

    Task AfterApAgentApproveAsync(
        Guid workflowId,
        Guid workflowInstanceId,
        WorkflowStep apAgentStep,
        Guid tenantId,
        Guid userId,
        MoveToNextStepApAgentPayload payload,
        int? legacyTransactionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// AP agent metadata: update repository item columns and matching ezfb_{form}_items columns
    /// (wFormControl name/jsonId). Optionally stores invoice line items JSON on ezfb.
    /// </summary>
    Task<ApAgentMetadataApplyResult> ApplyMetadataAsync(
        Guid tenantId,
        ApAgentMetadataApplyRequest request,
        Guid? userId,
        CancellationToken cancellationToken = default);

    /// <summary>Apply move-next formData jsonId map to dbo.ezfb_{form}_items row (incl. DYNAMIC_TABLE line items).</summary>
    Task<int> ApplyFormDataToEzfbAsync(
        string formId,
        Guid formEntryId,
        IReadOnlyDictionary<string, string> fields,
        string? lineItemsJson = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Apply <c>po_row</c> fields to ezfb form data (formId + formEntryId).
    /// Prefers <paramref name="agentResponseJson"/> from the move-next body when provided;
    /// otherwise falls back to the latest stored agentDataValidation row.
    /// </summary>
    Task<int> ApplyPoRowFromStoredAgentValidationAsync(
        Guid workflowId,
        Guid workflowInstanceId,
        string formId,
        Guid formEntryId,
        string? agentResponseJson = null,
        CancellationToken cancellationToken = default);
}
