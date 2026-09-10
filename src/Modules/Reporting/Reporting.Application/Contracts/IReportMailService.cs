namespace SaaSApp.Reporting.Application.Contracts;

public interface IReportMailService
{
    Task SendAsync(
        Guid tenantId,
        ReportBuilderConfig config,
        ReportRunResult data,
        CancellationToken cancellationToken = default);
}
