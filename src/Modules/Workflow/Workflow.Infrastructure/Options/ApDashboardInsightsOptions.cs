namespace SaaSApp.Workflow.Infrastructure.Options;

/// <summary>Hardcoded AP dashboard insight defaults. Agents URL comes from <c>Agents:ChatUrl</c>.</summary>
public static class ApDashboardInsightsDefaults
{
    public const bool Enabled = true;
    public const int TimeoutSeconds = 60;
    public const int InsightsCount = 4;
    public const string InsightArea = "AP Dashboard";
}
