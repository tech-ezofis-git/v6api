namespace SaaSApp.Repository.Application;

/// <summary>
/// Shared file-name extension rules for repository items, workflow attachments, and processAddon.
/// When filename extension and content type disagree, content type wins (e.g. PNG saved as *.pdf).
/// </summary>
public static class RepositoryFileNameHelper
{
    public static string EnsureExtension(
        string? fileName,
        string? contentType = null,
        string? filePath = null)
    {
        var name = string.IsNullOrWhiteSpace(fileName)
            ? string.Empty
            : Path.GetFileName(fileName.Trim());

        if (string.IsNullOrWhiteSpace(name))
            name = "document";

        var existingExt = Path.GetExtension(name);
        var mimeExt = ExtensionFromContentType(contentType);
        if (IsRealFileExtension(existingExt))
        {
            if (!string.IsNullOrEmpty(mimeExt) && !ExtensionsMatch(existingExt, mimeExt))
            {
                var stem = Path.GetFileNameWithoutExtension(name);
                return stem + mimeExt;
            }

            return name;
        }

        if (!string.IsNullOrEmpty(existingExt) && existingExt != ".")
            name = Path.GetFileNameWithoutExtension(name);

        var fromPath = !string.IsNullOrWhiteSpace(filePath)
            ? Path.GetExtension(filePath.Trim().Replace('\\', '/'))
            : null;
        if (IsRealFileExtension(fromPath))
            return name + fromPath!.ToLowerInvariant();

        if (!string.IsNullOrEmpty(mimeExt))
            return name + mimeExt;

        return name + ".pdf";
    }

    public static string? ExtensionFromContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return null;

        var mime = contentType.Trim().Split(';')[0].Trim().ToLowerInvariant();
        return mime switch
        {
            "application/pdf" => ".pdf",
            "image/tiff" => ".tiff",
            "image/tif" => ".tif",
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/png" => ".png",
            "application/msword" => ".doc",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
            "application/vnd.ms-excel" => ".xls",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
            _ when mime.StartsWith("image/", StringComparison.Ordinal) => ".img",
            _ => null
        };
    }

    private static bool ExtensionsMatch(string? ext1, string? ext2)
    {
        if (string.IsNullOrEmpty(ext1) || string.IsNullOrEmpty(ext2))
            return false;

        return string.Equals(NormalizeExtension(ext1), NormalizeExtension(ext2), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeExtension(string ext) =>
        ext.Trim().ToLowerInvariant() switch
        {
            ".jpeg" => ".jpg",
            ".tif" => ".tiff",
            _ => ext.Trim().ToLowerInvariant()
        };

    private static bool IsRealFileExtension(string? ext)
    {
        if (string.IsNullOrEmpty(ext) || ext == ".")
            return false;

        var body = ext.TrimStart('.');
        return body.Length is >= 2 and <= 8 && body.All(char.IsLetter);
    }
}
