namespace SaaSApp.Workflow.Infrastructure.Options;

public sealed class EmlAttachmentExtractionOptions
{
    public const string SectionName = "EmlAttachmentExtraction";

    /// <summary>Extensions (with leading dot, lowercase) allowed to be extracted and archived.</summary>
    public string[] AllowedExtensions { get; set; } =
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".csv", ".ppt", ".pptx",
        ".txt", ".png", ".jpg", ".jpeg"
    };

    /// <summary>Hard cap per extracted attachment. Default 25 MB.</summary>
    public long MaxAttachmentSizeBytes { get; set; } = 25 * 1024 * 1024;
}
