namespace SaaSApp.Reporting.Application.Contracts;

public interface IReportExportService
{
    ReportFileResult Export(ReportRunResult data, string? format);
}
