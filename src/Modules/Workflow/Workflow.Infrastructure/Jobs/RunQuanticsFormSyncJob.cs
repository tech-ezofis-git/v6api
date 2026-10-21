using Hangfire;
using Hangfire.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SaaSApp.MultiTenancy;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Application.Workflows;

namespace SaaSApp.Workflow.Infrastructure.Jobs;

public sealed class RunQuanticsFormSyncJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITenantConnectionStringResolver _connectionStringResolver;
    private readonly ILogger<RunQuanticsFormSyncJob> _logger;

    public RunQuanticsFormSyncJob(
        IServiceScopeFactory scopeFactory,
        ITenantConnectionStringResolver connectionStringResolver,
        ILogger<RunQuanticsFormSyncJob> logger)
    {
        _scopeFactory = scopeFactory;
        _connectionStringResolver = connectionStringResolver;
        _logger = logger;
    }

    // Python upserts master records by match criteria, so a retry is safe.
    [AutomaticRetry(Attempts = 2)]
    [JobDisplayName("Quantics Form Sync · {0}")]
    public async Task Execute(string tenantDisplay, QuanticsFormSyncJobArgs args, PerformContext? context)
    {
        var jobId = context?.BackgroundJob.Id
            ?? throw new InvalidOperationException("Hangfire PerformContext is required for Quantics form sync jobs.");

        _logger.LogInformation(
            "Quantics form sync job {JobId} started for workflow {WorkflowId} instance {InstanceId}",
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
                $"{QuanticsPackaging.FormSyncLabel} started",
                cancellationToken: default);

            await services.GetRequiredService<IQuanticsFormSyncService>()
                .ExecuteAsync(args, jobId);

            await progress.SetHangfireStateAsync(jobId, "Succeeded", cancellationToken: default);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Quantics form sync job {JobId} failed for instance {InstanceId}", jobId, args.InstanceId);
            try
            {
                await progress.SetHangfireStateAsync(jobId, "Failed", $"{QuanticsPackaging.FormSyncLabel} failed", ex.Message);
                await progress.UpdateProgressAsync(
                    jobId,
                    new ApAgentJobProgressUpdate("FAILED", $"{QuanticsPackaging.FormSyncLabel} failed"),
                    default);
            }
            catch (Exception progressEx)
            {
                _logger.LogWarning(progressEx, "Failed to persist Quantics form sync job failure for {JobId}", jobId);
            }

            throw;
        }
        finally
        {
            services.GetRequiredService<JobExecutionContext>().Clear();
        }
    }
}
