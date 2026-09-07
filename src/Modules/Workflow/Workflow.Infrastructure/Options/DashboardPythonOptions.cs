namespace SaaSApp.Workflow.Infrastructure.Options;

public sealed class DashboardPythonOptions
{
    public const string SectionName = "Dashboard";

    public bool Enabled { get; set; } = true;

    /// <summary>Python dashboard API base, e.g. http://52.172.32.88:8041/api</summary>
    public string ApiBaseUrl { get; set; } = "http://52.172.32.88:8041/api";

    public int TimeoutSeconds { get; set; } = 120;
}
