using SaaSApp.Workflow.Application.Forms;

namespace SaaSApp.Workflow.Application.Contracts;

/// <summary>
/// Form-filtered workflow ticket search: ezfb operator DSL → processForm → legacy mailbox-shaped rows.
/// </summary>
public interface IWorkflowTicketSearchService
{
    Task<WorkflowTicketFilterSchemaDto?> GetFilterFieldsAsync(
        Guid workflowId,
        CancellationToken cancellationToken = default);

    Task<WorkflowTicketSearchOutcome> SearchAsync(
        Guid workflowId,
        WorkflowTicketSearchRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Distinct non-empty values for a control on the workflow's FormId (ezfb_{suffix}_items).
    /// Returns null when the workflow does not exist.
    /// </summary>
    Task<FormControlDistinctValuesResult?> GetDistinctControlValuesAsync(
        Guid workflowId,
        string controlName,
        CancellationToken cancellationToken = default);
}
