namespace SaaSApp.Workflow.Application.Contracts;

/// <summary>Hangfire args for FTL qualifier, quote estimator, or document PDF. Not used by AP Agent.</summary>
public sealed record FtlAgentJobArgs(
    Guid TenantId,
    Guid UserId,
    Guid WorkflowId,
    Guid InstanceId,
    string ActivityId,
    string Mode,
    string? RepositoryId = null,
    string? FormId = null);

public interface IFtlAgentJobClient
{
    Task<string> EnqueueAsync(FtlAgentJobArgs args, CancellationToken cancellationToken = default);
}

public interface IFtlAgentPipelineService
{
    Task ExecuteAsync(FtlAgentJobArgs args, string hangfireJobId, CancellationToken cancellationToken = default);
}
