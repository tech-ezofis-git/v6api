namespace SaaSApp.Workflow.Application.Contracts;

public interface IApDashboardInsightsClient
{
    /// <summary>
    /// POSTs the dashboard as <c>intent=insight</c> / <c>payload.insight_json</c>
    /// to the agents <c>/chat</c> endpoint and returns insight strings.
    /// Returns an empty list when disabled or on failure (dashboard still returns).
    /// </summary>
    Task<IReadOnlyList<string>> GetInsightsAsync(
        ApDashboardResult dashboard,
        CancellationToken cancellationToken = default);
}
