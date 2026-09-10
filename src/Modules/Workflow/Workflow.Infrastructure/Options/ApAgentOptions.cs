namespace SaaSApp.Workflow.Infrastructure.Options;

public sealed class ApAgentOptions
{
    public const string SectionName = "ApAgent";

    public bool Enabled { get; set; } = true;

    public int TimeoutMinutes { get; set; } = 10;

    /// <summary>Public API base for progress callbacks, e.g. https://host/api/workflows</summary>
    public string? ApiBaseUrl { get; set; }

    /// <summary>
    /// When true, workflow start enriches the agents payload with a pilot-user JWT per tenant
    /// (<c>pilotAccessToken</c>, <c>pilotUserId</c>) so Python can call V6 as the agent.
    /// </summary>
    public bool IncludePilotAuthInPayload { get; set; } = true;

    /// <summary>
    /// Optional documentation-only default skill list. Not auto-applied on workflow start.
    /// Pass skills explicitly on <c>POST .../ap-agent/run</c> when a subset is required.
    /// </summary>
    public string[]? DefaultSkills { get; set; }
}
