namespace SaaSApp.Workflow.Application.Contracts;

public interface IApDashboardInsightsClient
{
    /// <summary>
    /// POSTs the dashboard payload to the Python insights API and returns insight strings.
    /// Returns an empty list when disabled or on failure (dashboard still returns).
    /// </summary>
    Task<IReadOnlyList<string>> GetInsightsAsync(
        ApDashboardResult dashboard,
        CancellationToken cancellationToken = default);
}
