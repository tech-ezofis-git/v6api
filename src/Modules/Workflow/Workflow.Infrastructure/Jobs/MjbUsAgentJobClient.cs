using Hangfire;
using SaaSApp.MultiTenancy;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Application.Workflows;

namespace SaaSApp.Workflow.Infrastructure.Jobs;

public sealed class MjbUsAgentJobClient : IMjbUsAgentJobClient
{
    private readonly IApAgentJobProgressService _progress;
    private readonly ITenantDisplayResolver _tenantDisplay;

    public MjbUsAgentJobClient(IApAgentJobProgressService progress, ITenantDisplayResolver tenantDisplay)
    {
        _progress = progress;
        _tenantDisplay = tenantDisplay;
    }

    public async Task<string> EnqueueAsync(MjbUsAgentJobArgs args, CancellationToken cancellationToken = default)
    {
        var tenantDisplay = await _tenantDisplay.ResolveAsync(args.TenantId, cancellationToken);
        var jobId = BackgroundJob.Enqueue<RunMjbUsAgentJob>(job => job.Execute(tenantDisplay, args, null));
        await _progress.RegisterQueuedAsync(
            jobId,
            args.TenantId,
            args.WorkflowId,
            args.InstanceId,
            cancellationToken,
            $"{MjbUsAgent.ClassificationLabel} queued");
        return jobId;
    }
}
