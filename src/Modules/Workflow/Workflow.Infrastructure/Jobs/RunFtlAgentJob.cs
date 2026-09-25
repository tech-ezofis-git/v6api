using Hangfire;
using Hangfire.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SaaSApp.MultiTenancy;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Infrastructure.Jobs;

public sealed class RunFtlAgentJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITenantConnectionStringResolver _connectionStringResolver;
    private readonly ILogger<RunFtlAgentJob> _logger;

    public RunFtlAgentJob(
        IServiceScopeFactory scopeFactory,
        ITenantConnectionStringResolver connectionStringResolver,
        ILogger<RunFtlAgentJob> logger)
    {
        _scopeFactory = scopeFactory;
        _connectionStringResolver = connectionStringResolver;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 0)]
    [JobDisplayName("FTL Agent · {0}")]
    public async Task Execute(string tenantDisplay, FtlAgentJobArgs args, PerformContext? context)
    {
        var jobId = context?.BackgroundJob.Id
            ?? throw new InvalidOperationException("Hangfire PerformContext is required for FTL Agent jobs.");

        _logger.LogInformation(
            "FTL {Mode} job {JobId} started for workflow {WorkflowId} instance {InstanceId}",
            args.Mode,
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
            await progress.SetHangfireStateAsync(jobId, "Processing", "Calling FTL agent", cancellationToken: default);
            await progress.UpdateProgressAsync(
                jobId,
                new ApAgentJobProgressUpdate("PROCESSING", $"FTL {args.Mode} started"),
                default);

            await services.GetRequiredService<IFtlAgentPipelineService>()
                .ExecuteAsync(args, jobId);

            await progress.SetHangfireStateAsync(jobId, "Succeeded", $"FTL {args.Mode} completed", cancellationToken: default);
            await progress.UpdateProgressAsync(
                jobId,
                new ApAgentJobProgressUpdate("COMPLETED", $"FTL {args.Mode} completed", 100),
                default);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FTL {Mode} job {JobId} failed for instance {InstanceId}", args.Mode, jobId, args.InstanceId);
            try
            {
                await progress.SetHangfireStateAsync(jobId, "Failed", "FTL agent failed", ex.Message);
                await progress.UpdateProgressAsync(
                    jobId,
                    new ApAgentJobProgressUpdate("FAILED", ex.Message),
                    default);
            }
            catch (Exception progressEx)
            {
                _logger.LogWarning(progressEx, "Failed to persist FTL job failure for {JobId}", jobId);
            }

            throw;
        }
        finally
        {
            services.GetRequiredService<JobExecutionContext>().Clear();
        }
    }
}
