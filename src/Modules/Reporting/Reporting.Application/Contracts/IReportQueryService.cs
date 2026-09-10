namespace SaaSApp.Reporting.Application.Contracts;

public interface IReportQueryService
{
    Task<IReadOnlyList<ReportDomainDto>> ListDomainsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReportSourceFormDto>> ListSourceFormsAsync(
        string? sourceType,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReportAvailableFieldDto>> ListFieldsAsync(
        Guid tenantId,
        string? domain,
        string? sourceFormId,
        string? sourceForm = null,
        string? sourceType = null,
        CancellationToken cancellationToken = default);

    Task<ReportRunResult> ExecuteAsync(
        Guid tenantId,
        ReportBuilderConfig config,
        CancellationToken cancellationToken = default);
}
