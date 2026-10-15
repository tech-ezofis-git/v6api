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

    /// <summary>Where WatchFox posts enquiry and invoice matches.</summary>
    public string WebhookUrl { get; set; } = "https://cloud.ezofis.com/api/email-ingest/watchfox";

    /// <summary>Product name on the Office 365 connect page (max 60 chars).</summary>
    public string AppName { get; set; } = "ezofis";

    /// <summary>HTTPS logo URL on the connect page (max 400 chars). Replaces the WatchFox logo.</summary>
    public string LogoUrl { get; set; } = string.Empty;

    /// <summary>Button/link colour as #RRGGBB.</summary>
    public string AccentColor { get; set; } = string.Empty;

    /// <summary>Shown as "Need help?" on the connect page.</summary>
    public string SupportEmail { get; set; } = string.Empty;

    /// <summary>
    /// Removes "Powered by WatchFox". WatchFox allows this on Tracker and Alpha plans only.
    /// </summary>
    public bool HidePoweredBy { get; set; }

    /// <summary>
    /// Same Message-ID in several watched mailboxes: <c>off</c>, <c>flag</c>, or <c>skip</c> (first copy only).
    /// </summary>
    public string Duplicates { get; set; } = "skip";

    /// <summary>
    /// Later mail in a conversation already caught for this type: <c>off</c>, <c>flag</c>, or <c>skip</c>.
    /// </summary>
    public string FollowUps { get; set; } = "skip";

    /// <summary>Conversation window in days for <see cref="FollowUps"/> (1–60).</summary>
    public int FollowUpDays { get; set; } = 60;
}
