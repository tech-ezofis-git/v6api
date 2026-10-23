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

    /// <summary>
    /// When true (default), skip company-logo / signature images embedded in the HTML body
    /// (inline/CID images under <see cref="MinNonSignatureImageBytes"/>, or logo-like file names).
    /// Real RFQ photos and screenshots are kept.
    /// </summary>
    public bool SkipSignatureImages { get; set; } = true;

    /// <summary>
    /// Inline / Content-ID images smaller than this are treated as signature chrome (default 150 KB).
    /// Larger inline images are kept as real attachments.
    /// </summary>
    public long MinNonSignatureImageBytes { get; set; } = 150 * 1024;
}

