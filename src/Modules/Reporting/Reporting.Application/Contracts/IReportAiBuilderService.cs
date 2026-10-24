namespace SaaSApp.Reporting.Application.Contracts;

public interface IReportAiBuilderService
{
    Task<ReportAiHttpResult> GenerateAndSaveAsync(
        Guid tenantId,
        Guid userId,
        ReportAiPromptRequest request,
        CancellationToken cancellationToken = default);

    Task<ReportAiHttpResult> RunSavedAsync(
        Guid tenantId,
        Guid reportId,
        ReportAiRunRequest request,
        CancellationToken cancellationToken = default);

    Task<ReportAiHttpResult> GetAsync(
        Guid tenantId,
        Guid reportId,
        CancellationToken cancellationToken = default);

    Task<ReportAiHttpResult> ListAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<ReportAiHttpResult> UpdateAsync(
        Guid tenantId,
        Guid reportId,
        ReportAiUpdateRequest request,
        CancellationToken cancellationToken = default);

    Task<ReportAiHttpResult> ListTypesAsync(CancellationToken cancellationToken = default);

    Task<ReportAiHttpResult> ListScopeOptionsAsync(
        string scope,
        Guid tenantId,
        CancellationToken cancellationToken = default);
}
