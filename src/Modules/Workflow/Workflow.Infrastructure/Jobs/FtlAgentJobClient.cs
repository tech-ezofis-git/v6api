using Hangfire;
using SaaSApp.MultiTenancy;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Infrastructure.Jobs;

public sealed class FtlAgentJobClient : IFtlAgentJobClient
{
    private readonly IApAgentJobProgressService _progress;
    private readonly ITenantDisplayResolver _tenantDisplay;

    public FtlAgentJobClient(
        IApAgentJobProgressService progress,
        ITenantDisplayResolver tenantDisplay)
    {
        _progress = progress;
        _tenantDisplay = tenantDisplay;
    }

    public async Task<string> EnqueueAsync(FtlAgentJobArgs args, CancellationToken cancellationToken = default)
    {
        var tenantDisplay = await _tenantDisplay.ResolveAsync(args.TenantId, cancellationToken);
        var jobId = BackgroundJob.Enqueue<RunFtlAgentJob>(j => j.Execute(tenantDisplay, args, null));
        await _progress.RegisterQueuedAsync(
            jobId,
            args.TenantId,
            args.WorkflowId,
            args.InstanceId,
            cancellationToken);
        return jobId;
    }
}
