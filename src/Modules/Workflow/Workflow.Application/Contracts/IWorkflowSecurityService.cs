using SaaSApp.Workflow.Application.Workflows.Commands.CreateWorkflow;

namespace SaaSApp.Workflow.Application.Contracts;

/// <summary>Manages workflow security and user assignments.</summary>
public interface IWorkflowSecurityService
{
    Task EnsureDefaultWorkflowSecurityAsync(
        Guid workflowId,
        CancellationToken cancellationToken = default);

    Task SetWorkflowSecurityAsync(
        Guid workflowId,
        string[]? coordinators,
        string[]? superusers,
        List<WorkflowBlockDto> blocks,
        CancellationToken cancellationToken = default);

    Task SetWorkflowUsersByDomainAsync(
        Guid workflowId,
        string[] domains,
        CancellationToken cancellationToken = default);

    /// <summary>Admin and tenant pilot see every workflow. TenantUsers see none until granted.</summary>
    Task<bool> UserSeesAllWorkflowsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>True when the user is unrestricted or is granted this workflow (security, users, or creator).</summary>
    Task<bool> CanAccessWorkflowAsync(
        Guid workflowId,
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>Workflow ids the user may list. Empty when nothing is granted.</summary>
    Task<IReadOnlySet<Guid>> GetAccessibleWorkflowIdsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}

