namespace SaaSApp.Repository.Application;

/// <summary>
/// File types sent to Agents OCR (/chat intent=ocr): PDF, images, Word, and Excel.
/// </summary>
public static class RepositoryOcrFileSupport
{
    private static readonly HashSet<string> AgentOcrExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf",
        ".tif",
        ".tiff",
        ".png",
        ".jpg",
        ".jpeg",
        ".bmp",
        ".webp",
        ".gif",
        ".doc",
        ".docx",
        ".xls",
        ".xlsx"
    };

    public static bool SupportsAgentOcr(string? fileName, string? contentType = null)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty);
        if (!string.IsNullOrEmpty(ext) && AgentOcrExtensions.Contains(ext))
            return true;

        var mimeExt = RepositoryFileNameHelper.ExtensionFromContentType(contentType);
        return !string.IsNullOrEmpty(mimeExt) && AgentOcrExtensions.Contains(mimeExt);
    }

    public static string ResolveContentType(string? fileName, string? contentType = null)
    {
        if (!string.IsNullOrWhiteSpace(contentType)
            && !contentType.Contains("octet-stream", StringComparison.OrdinalIgnoreCase))
            return contentType.Trim().Split(';')[0].Trim();

        var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "application/pdf",
            ".tif" or ".tiff" => "image/tiff",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            _ => "application/octet-stream"
        };
    }
}
