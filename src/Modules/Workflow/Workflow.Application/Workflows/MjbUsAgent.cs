using SaaSApp.Workflow.Domain.Entities;

namespace SaaSApp.Workflow.Application.Workflows;

/// <summary>
/// MJB_US mail → classification → OCR → FTP. Scoped to one tenant and workflow
/// so AP Agent and FTL routing stay unchanged.
/// </summary>
public static class MjbUsAgent
{
    public static readonly Guid TenantId = Guid.Parse("0470d932-31f1-46e6-b124-9070b1bc15fb");
    public static readonly Guid WorkflowId = Guid.Parse("f5781e33-202e-407b-9c0d-e83a1a21b101");
    public static readonly Guid FormId = Guid.Parse("f0412211-f2c7-4840-9c52-1faafa74f40d");

    public const string MonitorFolder = "Ramco_mjb";
    public const string Classification = "classification";
    public const string Ocr = "ocr";
    public const string Ftp = "ftp";
    public const string Mail = "mail";
    public const string End = "end";

    public const string ClassificationLabel = "Classification agent";
    public const string OcrLabel = "OCR agent";
    public const string FtpLabel = "FTP agent";

    public static readonly string[] AttachmentExtensions =
    [
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".tif", ".tiff", ".png", ".jpg", ".jpeg", ".txt", ".csv", ".rtf"
    ];

    public static bool IsThisWorkflow(Guid workflowId, Guid? tenantId) =>
        workflowId == WorkflowId && tenantId == TenantId;

    public static string Kind(WorkflowStep step)
    {
        var text = $"{step.StageType} {step.Name}";
        if (Contains(text, "CLASSIF"))
            return Classification;
        if (Contains(text, "FTP"))
            return Ftp;
        if (Contains(text, "OCR") || Contains(text, "EXTRACT"))
            return Ocr;
        if (string.Equals(step.StageType?.Trim(), "END", StringComparison.OrdinalIgnoreCase)
            || Contains(text, "SUCCESS"))
            return End;
        if (Contains(text, "MAIL"))
            return Mail;
        return "other";
    }

    public static WorkflowStep? FindStep(IEnumerable<WorkflowStep> steps, string kind) =>
        steps.FirstOrDefault(step => string.Equals(Kind(step), kind, StringComparison.OrdinalIgnoreCase));

    public static string ActivityIdOf(WorkflowStep step) =>
        string.IsNullOrWhiteSpace(step.ActivityId) ? step.Id.ToString("D") : step.ActivityId!;

    public static bool SameActivity(WorkflowStep step, string? activityId)
    {
        if (string.IsNullOrWhiteSpace(activityId))
            return false;

        var id = activityId.Trim();
        return string.Equals(ActivityIdOf(step), id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(step.Id.ToString("D"), id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(step.Id.ToString("N"), id, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Contains(string text, string value) =>
        text.Contains(value, StringComparison.OrdinalIgnoreCase);
}
