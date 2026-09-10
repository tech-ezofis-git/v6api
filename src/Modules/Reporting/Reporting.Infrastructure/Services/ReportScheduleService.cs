using Hangfire;
using Microsoft.Extensions.Logging;
using SaaSApp.Reporting.Application.Contracts;
using SaaSApp.Reporting.Infrastructure.Jobs;

namespace SaaSApp.Reporting.Infrastructure.Services;

public sealed class ReportScheduleService : IReportScheduleService
{
    private readonly ILogger<ReportScheduleService> _logger;

    public ReportScheduleService(ILogger<ReportScheduleService> logger)
    {
        _logger = logger;
    }

    public string JobId(Guid tenantId, Guid reportId) =>
        $"report-delivery-{tenantId:N}-{reportId:N}";

    public void Sync(Guid tenantId, ReportBuilderConfig config)
    {
        var jobId = JobId(tenantId, config.Id);
        var shouldRun = config.Scheduled
            && config.Schedule is not null
            && HasRecipients(config);

        if (!ReportCron.HangfireConfigured())
        {
            _logger.LogWarning("Hangfire is not configured; skipped schedule sync for report {ReportId}.", config.Id);
            return;
        }

        if (!shouldRun)
        {
            RecurringJob.RemoveIfExists(jobId);
            return;
        }

        if (!ReportCron.TryBuild(
                config.Schedule!.Recurrence,
                config.Schedule.Day,
                config.Schedule.Time,
                config.Schedule.Timezone,
                out var cron,
                out var zone))
        {
            _logger.LogWarning("Could not build cron for report {ReportId}.", config.Id);
            return;
        }

        RecurringJob.AddOrUpdate<RunScheduledReportJob>(
            jobId,
            job => job.Execute(tenantId, config.Id, null),
            cron,
            new RecurringJobOptions { TimeZone = zone });

        _logger.LogInformation(
            "Synced Hangfire report job {JobId} cron={Cron} tz={TimeZone}.",
            jobId,
            cron,
            zone.Id);
    }

    public void Remove(Guid tenantId, Guid reportId)
    {
        if (!ReportCron.HangfireConfigured())
            return;
        RecurringJob.RemoveIfExists(JobId(tenantId, reportId));
    }

    private static bool HasRecipients(ReportBuilderConfig config)
    {
        var schedule = config.Schedule;
        if (schedule is null)
            return false;
        return schedule.Recipients.Count > 0
            || schedule.Cc.Count > 0
            || config.SharedUsers.Count > 0;
    }
}
