using Hangfire;
using SaaSApp.MultiTenancy;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Application.Workflows;

namespace SaaSApp.Workflow.Infrastructure.Jobs;

public sealed class QuanticsFormSyncJobClient : IQuanticsFormSyncJobClient
{
    private readonly IApAgentJobProgressService _progress;
    private readonly ITenantDisplayResolver _tenantDisplay;

    public QuanticsFormSyncJobClient(IApAgentJobProgressService progress, ITenantDisplayResolver tenantDisplay)
    {
        _progress = progress;
        _tenantDisplay = tenantDisplay;
    }

    public async Task<string> EnqueueAsync(QuanticsFormSyncJobArgs args, CancellationToken cancellationToken = default)
    {
        var tenantDisplay = await _tenantDisplay.ResolveAsync(args.TenantId, cancellationToken);
        var jobId = BackgroundJob.Enqueue<RunQuanticsFormSyncJob>(job => job.Execute(tenantDisplay, args, null));
        await _progress.RegisterQueuedAsync(
            jobId,
            args.TenantId,
            args.WorkflowId,
            args.InstanceId,
            cancellationToken,
            $"{QuanticsPackaging.FormSyncLabel} queued");
        return jobId;
    }
}
