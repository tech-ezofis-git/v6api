namespace SaaSApp.Workflow.Infrastructure.Options;

public sealed class ApDashboardInsightsOptions
{
    public const string SectionName = "ApDashboard:Insights";

    /// <summary>Python insights endpoint. Dashboard JSON is POSTed; response includes <c>insights</c>.</summary>
    public string ApiUrl { get; set; } = "http://localhost:8091/api/v1/insights";

    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>When false, dashboard returns without calling Python.</summary>
    public bool Enabled { get; set; } = true;
}
