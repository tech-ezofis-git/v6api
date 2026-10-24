namespace SaaSApp.Reporting.Application.Contracts;

public interface IReportAiDefinitionService
{
    Task<ReportAiDefinitionRecord> SaveAsync(
        ReportAiDefinitionRecord record,
        CancellationToken cancellationToken = default);

    Task<ReportAiDefinitionRecord?> GetAsync(
        Guid tenantId,
        Guid reportId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReportAiDefinitionRecord>> ListAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<ReportAiDefinitionRecord?> UpdateAsync(
        Guid tenantId,
        Guid reportId,
        ReportAiUpdateRequest patch,
        CancellationToken cancellationToken = default);

    Task IncrementRunsAsync(
        Guid tenantId,
        Guid reportId,
        CancellationToken cancellationToken = default);
}
