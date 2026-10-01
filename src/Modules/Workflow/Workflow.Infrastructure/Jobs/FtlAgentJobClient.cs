using Hangfire;
using Microsoft.Extensions.Options;
using SaaSApp.MultiTenancy;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Infrastructure.Options;

namespace SaaSApp.Workflow.Infrastructure.Jobs;

public sealed class FtlAgentJobClient : IFtlAgentJobClient
{
    private readonly IApAgentJobProgressService _progress;
    private readonly ITenantDisplayResolver _tenantDisplay;
    private readonly IOptions<ApAgentOptions> _apAgentOptions;

    public FtlAgentJobClient(
        IApAgentJobProgressService progress,
        ITenantDisplayResolver tenantDisplay,
        IOptions<ApAgentOptions> apAgentOptions)
    {
        _progress = progress;
        _tenantDisplay = tenantDisplay;
        _apAgentOptions = apAgentOptions;
    }

    public string? StatusUrl(string jobId)
    {
        var baseUrl = _apAgentOptions.Value.ApiBaseUrl?.TrimEnd('/');
        return string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(jobId)
            ? null
            : $"{baseUrl}/ap-agent/jobs/{jobId}";
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
