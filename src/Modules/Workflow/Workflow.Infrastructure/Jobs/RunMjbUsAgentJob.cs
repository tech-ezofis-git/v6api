using Hangfire;
using Hangfire.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SaaSApp.MultiTenancy;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Application.Workflows;

namespace SaaSApp.Workflow.Infrastructure.Jobs;

public sealed class RunMjbUsAgentJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITenantConnectionStringResolver _connectionStringResolver;
    private readonly ILogger<RunMjbUsAgentJob> _logger;

    public RunMjbUsAgentJob(
        IServiceScopeFactory scopeFactory,
        ITenantConnectionStringResolver connectionStringResolver,
        ILogger<RunMjbUsAgentJob> logger)
    {
        _scopeFactory = scopeFactory;
        _connectionStringResolver = connectionStringResolver;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 0)]
    [JobDisplayName("MJB Agent · {0}")]
    public async Task Execute(string tenantDisplay, MjbUsAgentJobArgs args, PerformContext? context)
    {
        var jobId = context?.BackgroundJob.Id
            ?? throw new InvalidOperationException("Hangfire PerformContext is required for MJB agent jobs.");

        _logger.LogInformation(
            "MJB agent job {JobId} started for workflow {WorkflowId} instance {InstanceId}",
            jobId,
            args.WorkflowId,
            args.InstanceId);

        var connectionString = await _connectionStringResolver.GetConnectionStringAsync(args.TenantId);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"Tenant connection string not found for {args.TenantId:D}.");

        using var scope = _scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        services.GetRequiredService<ITenantConnectionProvider>().SetConnectionString(connectionString);
        services.GetRequiredService<JobExecutionContext>().Set(args.TenantId, args.UserId);
        var progress = services.GetRequiredService<IApAgentJobProgressService>();

        try
        {
            await progress.SetHangfireStateAsync(
                jobId,
                "Processing",
                $"{MjbUsAgent.ClassificationLabel} started",
                cancellationToken: default);

            await services.GetRequiredService<IMjbUsAgentPipelineService>()
                .ExecuteAsync(args, jobId);

            await progress.SetHangfireStateAsync(jobId, "Succeeded", cancellationToken: default);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MJB agent job {JobId} failed for instance {InstanceId}", jobId, args.InstanceId);
            try
            {
                await progress.SetHangfireStateAsync(jobId, "Failed", "MJB agent failed", ex.Message);
                await progress.UpdateProgressAsync(
                    jobId,
                    new ApAgentJobProgressUpdate("FAILED", "MJB agent failed"),
                    default);
            }
            catch (Exception progressEx)
            {
                _logger.LogWarning(progressEx, "Failed to persist MJB job failure for {JobId}", jobId);
            }

            throw;
        }
        finally
        {
            services.GetRequiredService<JobExecutionContext>().Clear();
        }
    }
}
