namespace SaaSApp.Reporting.Application.Contracts;

public interface IReportAgentPythonClient
{
    Task<ReportAgentProxyResult> GeneratePromptAsync(
        ReportAgentChatRequest request,
        CancellationToken cancellationToken = default);

    Task<ReportAgentProxyResult> RunReportAsync(
        ReportAgentChatRequest request,
        CancellationToken cancellationToken = default);

    Task<ReportAgentProxyResult> GetTypesAsync(CancellationToken cancellationToken = default);

    Task<ReportAgentProxyResult> GetScopeOptionsAsync(
        string scope,
        Guid tenantId,
        CancellationToken cancellationToken = default);
}
