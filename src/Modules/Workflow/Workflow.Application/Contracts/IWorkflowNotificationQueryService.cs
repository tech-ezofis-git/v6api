namespace SaaSApp.Workflow.Application.Contracts;

public interface IWorkflowNotificationQueryService
{
    Task<IReadOnlyList<WorkflowNotificationItemDto>> ListForCurrentUserAsync(
        string? category = null,
        CancellationToken cancellationToken = default);

    Task<WorkflowNotificationReadDto?> MarkReadAsync(
        int id,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        int id,
        CancellationToken cancellationToken = default);
}

public sealed record WorkflowNotificationActorDto(string Name, string Email);

public sealed record WorkflowNotificationDataDto(
    string? ProcessId,
    string? TransactionId,
    string? WorkflowId,
    string? WorkflowName);

public sealed record WorkflowNotificationSearchDto(
    string? ProcessId,
    string? TransactionId,
    string? WorkflowId);

public sealed record WorkflowNotificationTargetDto(
    string Route,
    WorkflowNotificationSearchDto Search);

public sealed record WorkflowNotificationItemDto(
    string Id,
    WorkflowNotificationActorDto Actor,
    string Category,
    string? CreatedAtUtc,
    WorkflowNotificationDataDto Data,
    bool IsRead,
    string? Message,
    string Severity,
    WorkflowNotificationTargetDto Target,
    string? Title);

public sealed record WorkflowNotificationReadDto(string Id, bool IsRead);
