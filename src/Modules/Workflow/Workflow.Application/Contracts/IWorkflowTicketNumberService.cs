namespace SaaSApp.Workflow.Application.Contracts;

/// <summary>
/// Allocates sequential ticket numbers per workflow from
/// <c>settings.general.processNumberPrefix</c> (e.g. REQ-1, REQ-2).
/// </summary>
public interface IWorkflowTicketNumberService
{
    /// <summary>
    /// Returns the next ticket number for the workflow using processNumberPrefix
    /// from workflow JSON, and persists the counter so concurrent starts do not collide.
    /// </summary>
    Task<string> AllocateNextAsync(Guid workflowId, CancellationToken cancellationToken = default);
}
