namespace SaaSApp.Workflow.Infrastructure.Options;

/// <summary>
/// Dashboard agent settings. Downstream calls go to <c>Agents:ChatUrl</c> with
/// <c>intent=dashboard</c> and <c>payload.phase</c> (prompts | schema | data).
/// </summary>
public sealed class DashboardPythonOptions
{
    public const string SectionName = "Dashboard";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Legacy Python base URL (unused when <c>Agents:ChatUrl</c> is set).
    /// Kept for config compatibility.
    /// </summary>
    public string ApiBaseUrl { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 120;
}
