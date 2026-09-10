namespace SaaSApp.Reporting.Application.Contracts;

public interface IReportScheduleService
{
    void Sync(Guid tenantId, ReportBuilderConfig config);
    void Remove(Guid tenantId, Guid reportId);
    string JobId(Guid tenantId, Guid reportId);
}
