namespace SaaSApp.Api.Services.Jira;

/// <summary>Jira Cloud settings for creating support tickets.</summary>
public sealed class JiraOptions
{
    public const string SectionName = "Jira";

    public bool Enabled { get; set; }

    /// <summary>
    /// When true, create issues via the local Python gateway (OpenSSL/certifi)
    /// instead of calling Atlassian directly with Windows Schannel.
    /// </summary>
    public bool UseProxy { get; set; }

    /// <summary>Local gateway base URL, e.g. http://127.0.0.1:5055</summary>
    public string ProxyBaseUrl { get; set; } = "http://127.0.0.1:5055";

    /// <summary>Site root, e.g. https://ezofis.atlassian.net (not a UI list URL).</summary>
    public string BaseUrl { get; set; } = "https://ezofis.atlassian.net";

    /// <summary>Jira account email (also used as support-team notification recipient).</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Jira API token. Required for direct mode; optional when UseProxy is true (token lives on the gateway).</summary>
    public string ApiToken { get; set; } = string.Empty;

    public string ProjectKey { get; set; } = "SUP";

    public string IssueType { get; set; } = "Task";
}
