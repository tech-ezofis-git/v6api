namespace SaaSApp.Workflow.Application.Contracts;

public sealed record ApAgentJobProgressUpdate(
    string? Stage = null,
    string? Message = null,
    int? Percent = null,
    string? FormData = null);

public sealed record ApAgentJobStatusResult(
    string JobId,
    Guid WorkflowId,
    Guid InstanceId,
    string HangfireStatus,
    string? Stage,
    string? Message,
    int? Percent,
    string? FormData,
    string? ErrorMessage,
    DateTime? UpdatedAtUtc,
    bool IsTerminal);

public sealed record ApAgentJobStatusListResult(
    IReadOnlyList<ApAgentJobStatusResult> Items,
    IReadOnlyList<string> NotFoundJobIds);

public interface IApAgentJobProgressService
{
    Task RegisterQueuedAsync(
        string jobId,
        Guid tenantId,
        Guid workflowId,
        Guid instanceId,
        CancellationToken cancellationToken = default,
        string? message = null);

    Task UpdateProgressAsync(
        string jobId,
        ApAgentJobProgressUpdate update,
        CancellationToken cancellationToken = default);

    Task UpdateProgressByInstanceAsync(
        Guid workflowId,
        Guid instanceId,
        ApAgentJobProgressUpdate update,
        CancellationToken cancellationToken = default);

    /// <summary>Loads latest job for workflow+instance and stores formData JSON (e.g. after metadata apply).</summary>
    Task UpdateFormDataByInstanceAsync(
        Guid workflowId,
        Guid instanceId,
        string formDataJson,
        CancellationToken cancellationToken = default);

    Task SetHangfireStateAsync(
        string jobId,
        string hangfireState,
        string? message = null,
        string? errorMessage = null,
        CancellationToken cancellationToken = default);

    Task<ApAgentJobProgressRow?> GetByJobIdAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Creates workflow.ApAgentJobProgress if missing (tenant DB).</summary>
    Task EnsureProgressTableAsync(CancellationToken cancellationToken = default);

    Task<string?> GetLatestActiveJobIdForInstanceAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default);
}

public sealed record ApAgentJobProgressRow(
    string JobId,
    Guid TenantId,
    Guid WorkflowId,
    Guid InstanceId,
    string? HangfireState,
    string? Stage,
    string? Message,
    int? Percent,
    string? ErrorMessage,
    string? FormData,
    DateTime UpdatedAtUtc);

public interface IApAgentJobStatusService
{
    Task<ApAgentJobStatusResult?> GetStatusAsync(string jobId, CancellationToken cancellationToken = default);

    Task<ApAgentJobStatusListResult> GetStatusesAsync(
        IEnumerable<string> jobIds,
        CancellationToken cancellationToken = default);
}
