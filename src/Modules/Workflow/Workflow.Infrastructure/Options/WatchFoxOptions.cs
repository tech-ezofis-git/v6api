namespace SaaSApp.Workflow.Infrastructure.Options;

/// <summary>
/// WatchFox calls <c>POST /api/email-ingest/watchfox</c> when a watched mailbox matches.
/// ApiKey and WebhookSecret stay in the host settings, not in git.
/// </summary>
public sealed class WatchFoxOptions
{
    public const string SectionName = "WatchFox";

    public string ApiKey { get; set; } = string.Empty;

    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Used when the watch event metadata does not include tenantId.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Used when the watch event metadata does not include workflowId. Invoice watches use this.</summary>
    public string WorkflowId { get; set; } = string.Empty;

    /// <summary>
    /// FTL Enquiries watch. A new enquiry email starts this workflow with the .eml attached.
    /// </summary>
    public string EnquiryWorkflowId { get; set; } = "1325cf43-3902-426d-800f-ffde5bbfe0da";
}
