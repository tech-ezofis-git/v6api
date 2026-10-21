namespace SaaSApp.Workflow.Application.Contracts;

/// <summary>Hangfire args for the Quantics packaging master-form sync on ticket completion.</summary>
public sealed record QuanticsFormSyncJobArgs(
    Guid TenantId,
    Guid UserId,
    Guid WorkflowId,
    Guid InstanceId,
    string? FormId = null,
    Guid? FormEntryId = null);

public interface IQuanticsFormSyncJobClient
{
    Task<string> EnqueueAsync(QuanticsFormSyncJobArgs args, CancellationToken cancellationToken = default);
}

public interface IQuanticsFormSyncService
{
    Task ExecuteAsync(QuanticsFormSyncJobArgs args, string hangfireJobId, CancellationToken cancellationToken = default);
}
