using Hangfire;
using Hangfire.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SaaSApp.MultiTenancy;
using SaaSApp.Reporting.Application.Contracts;

namespace SaaSApp.Reporting.Infrastructure.Jobs;

public sealed class RunScheduledReportJob
{
    private static readonly Guid SystemUserId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITenantConnectionStringResolver _connectionStringResolver;
    private readonly ILogger<RunScheduledReportJob> _logger;

    public RunScheduledReportJob(
        IServiceScopeFactory scopeFactory,
        ITenantConnectionStringResolver connectionStringResolver,
        ILogger<RunScheduledReportJob> logger)
    {
        _scopeFactory = scopeFactory;
        _connectionStringResolver = connectionStringResolver;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 2)]
    [JobDisplayName("Report delivery ┬╖ {0} ┬╖ {1}")]
    public async Task Execute(Guid tenantId, Guid reportId, PerformContext? context)
    {
        var connectionString = await _connectionStringResolver.GetConnectionStringAsync(tenantId);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogWarning("Scheduled report {ReportId}: no connection string for tenant {TenantId}.", reportId, tenantId);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        services.GetRequiredService<ITenantConnectionProvider>().SetConnectionString(connectionString);
        services.GetRequiredService<JobExecutionContext>().Set(tenantId, SystemUserId);

        var definitions = services.GetRequiredService<IReportDefinitionService>();
        var query = services.GetRequiredService<IReportQueryService>();
        var mail = services.GetRequiredService<IReportMailService>();

        var config = await definitions.GetForJobAsync(tenantId, reportId);
        if (config is null)
        {
            _logger.LogWarning("Scheduled report {ReportId} was not found for tenant {TenantId}.", reportId, tenantId);
            RecurringJob.RemoveIfExists($"report-delivery-{tenantId:N}-{reportId:N}");
            return;
        }

        if (!config.Scheduled)
        {
            _logger.LogInformation("Scheduled report {ReportId} has scheduling turned off ΓÇö skip.", reportId);
            return;
        }

        var data = await query.ExecuteAsync(tenantId, config);
        await mail.SendAsync(tenantId, config, data);
        await definitions.IncrementRunsAsync(tenantId, reportId);
        _logger.LogInformation(
            "Delivered scheduled report {ReportId} ({Name}) with {RowCount} row(s).",
            reportId,
            config.Name,
            data.RowCount);
    }
}
