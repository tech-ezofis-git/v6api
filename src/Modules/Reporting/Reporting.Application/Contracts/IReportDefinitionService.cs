namespace SaaSApp.Reporting.Application.Contracts;

public interface IReportDefinitionService
{
    Task<IReadOnlyList<ReportListItemDto>> ListAsync(
        Guid tenantId,
        Guid userId,
        bool isAdmin,
        ReportListQuery query,
        CancellationToken cancellationToken = default);

    Task<ReportBuilderConfig?> GetAsync(
        Guid tenantId,
        Guid userId,
        bool isAdmin,
        Guid reportId,
        CancellationToken cancellationToken = default);

    Task<ReportBuilderConfig> SaveAsync(
        Guid tenantId,
        Guid userId,
        bool isAdmin,
        ReportBuilderConfig config,
        CancellationToken cancellationToken = default);

    Task<ReportBuilderConfig> PublishAsync(
        Guid tenantId,
        Guid userId,
        bool isAdmin,
        Guid reportId,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        Guid tenantId,
        Guid userId,
        bool isAdmin,
        Guid reportId,
        CancellationToken cancellationToken = default);

    Task<ReportBuilderConfig?> GetForJobAsync(
        Guid tenantId,
        Guid reportId,
        CancellationToken cancellationToken = default);

    Task IncrementRunsAsync(Guid tenantId, Guid reportId, CancellationToken cancellationToken = default);
}
