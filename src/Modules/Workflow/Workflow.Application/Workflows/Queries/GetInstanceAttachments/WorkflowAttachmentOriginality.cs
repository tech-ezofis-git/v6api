namespace SaaSApp.Workflow.Application.Workflows.Queries.GetInstanceAttachments;

/// <summary>
/// Marks the "primary" document for a stage so the frontend can surface it first:
/// the source .eml for email-started tickets, and generated quote PDFs for the document stage.
/// Extracted / supporting files stay <c>isOriginal: false</c>.
/// </summary>
public static class WorkflowAttachmentOriginality
{
    public static bool IsOriginal(string? fileName, string? contentType) =>
        LooksLikeEml(fileName, contentType) || IsQuoteDocumentFileName(fileName);

    public static bool LooksLikeEml(string? fileName, string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(fileName)
            && fileName.EndsWith(".eml", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.IsNullOrWhiteSpace(contentType))
            return false;

        return contentType.Contains("rfc822", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("message/rfc822", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsQuoteDocumentFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        return fileName.EndsWith("_quote_1.pdf", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith("_quote_2.pdf", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith("_quote_3.pdf", StringComparison.OrdinalIgnoreCase);
    }
}
