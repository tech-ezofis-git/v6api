namespace SaaSApp.Workflow.Infrastructure.Options;

public sealed class ApAgentOptions
{
    public const string SectionName = "ApAgent";

    public bool Enabled { get; set; } = true;

    public int TimeoutMinutes { get; set; } = 10;

    /// <summary>Public API base for progress callbacks, e.g. https://host/api/workflows</summary>
    public string? ApiBaseUrl { get; set; }

    /// <summary>
    /// Optional documentation-only default skill list. Not auto-applied on workflow start.
    /// Pass skills explicitly on <c>POST .../ap-agent/run</c> when a subset is required.
    /// </summary>
    public string[]? DefaultSkills { get; set; }
}
