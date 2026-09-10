using System.Text.RegularExpressions;

namespace SaaSApp.Repository.Infrastructure.Storage;

internal static class RepositoryFilePathHelper
{
    public const string ArchiveRoot = "archive";
    public const string MonitorRoot = "monitor";

    /// <summary>Staging path before index/archive: monitor/{repositoryId}/{timestamp}/{fileName}</summary>
    public static string BuildMonitorRelativePath(Guid repositoryId, string fileName)
    {
        var ts = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        var safe = SanitizePathSegment(Path.GetFileName(fileName));
        if (string.IsNullOrWhiteSpace(safe))
            safe = "document.pdf";
        safe = EnsureFileNameHasExtension(safe, filePath: fileName);
        return $"{MonitorRoot}/{repositoryId:N}/{ts}/{safe}";
    }

    /// <summary>Legacy flat path when archive layout is not used.</summary>
    public static string BuildFlatRelativePath(Guid repositoryId, Guid itemId, string fileName)
    {
        var ext = GetExtension(fileName);
        return $"repository/{repositoryId:N}/{itemId:N}{ext}";
    }

    /// <summary>
    /// Tenant container (ezts{tenantId}) + blob path:
    /// archive/{repositoryName}/{folder fields}/{archiveFileName}.ext — file stem from naming field (level above folders, or max folder field).
    /// </summary>
    public static string BuildArchiveRelativePath(
        string repositoryName,
        IReadOnlyList<string> folderLevelNames,
        string originalFileName,
        int fileVersion = 1)
    {
        var ext = GetExtension(originalFileName);
        var repoSegment = SanitizePathSegment(repositoryName);
        if (string.IsNullOrWhiteSpace(repoSegment))
            repoSegment = "repository";

        var segments = new List<string> { ArchiveRoot, repoSegment };

        for (var i = 0; i < folderLevelNames.Count; i++)
        {
            var folder = SanitizePathSegment(folderLevelNames[i]);
            if (string.IsNullOrWhiteSpace(folder))
                folder = $"Level{i + 1}";
            segments.Add(folder);
        }

        var fileStem = SanitizePathSegment(Path.GetFileNameWithoutExtension(originalFileName));
        if (string.IsNullOrWhiteSpace(fileStem))
            fileStem = "document";
        segments.Add(AppendVersionToFileSegment($"{fileStem}{ext}", fileVersion));

        return string.Join('/', segments);
    }

    /// <summary>Original upload name without an existing <c>_vN</c> suffix.</summary>
    public static string GetBaseFileName(string fileName)
    {
        var name = Path.GetFileName(fileName.Trim());
        if (string.IsNullOrWhiteSpace(name))
            return name;

        var ext = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        if (string.IsNullOrWhiteSpace(stem))
            return name;

        stem = StripVersionSuffixFromStem(stem);
        return string.IsNullOrEmpty(ext) ? stem : stem + ext;
    }

    /// <summary>
    /// Ensures <paramref name="fileName"/> has an extension (e.g. <c>.pdf</c>).
    /// Prefers existing extension, then <paramref name="filePath"/>, then content-type, then <c>.pdf</c>.
    /// </summary>
    public static string EnsureFileNameHasExtension(
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
        // Real file extensions are short alphabetic (pdf, tiff, …). Reject numeric "extensions"
        // like ".6001" from invoice-style names using dots (INV.2026.6001).
        if (IsRealFileExtension(existingExt))
            return name;

        // Strip a fake numeric extension before appending the real one.
        if (!string.IsNullOrEmpty(existingExt) && existingExt != ".")
            name = Path.GetFileNameWithoutExtension(name);

        var fromPath = !string.IsNullOrWhiteSpace(filePath)
            ? Path.GetExtension(filePath.Trim().Replace('\\', '/'))
            : null;
        if (IsRealFileExtension(fromPath))
            return name + fromPath!.ToLowerInvariant();

        var fromMime = ExtensionFromContentType(contentType);
        if (!string.IsNullOrEmpty(fromMime))
            return name + fromMime;

        return name + ".pdf";
    }

    private static bool IsRealFileExtension(string? ext)
    {
        if (string.IsNullOrEmpty(ext) || ext == ".")
            return false;
        // ".pdf", ".tiff", ".docx" — not ".6001" or ".2026"
        var body = ext.TrimStart('.');
        return body.Length is >= 2 and <= 8 && body.All(char.IsLetter);
    }

    /// <summary>Display/storage name: <c>invoice.pdf</c> (v1), <c>invoice_v2.pdf</c> (v2+).</summary>
    public static string ApplyVersionToFileName(string fileName, int fileVersion)
    {
        if (fileVersion < 1)
            fileVersion = 1;

        var baseName = EnsureFileNameHasExtension(GetBaseFileName(fileName));
        var ext = Path.GetExtension(baseName);
        var stem = Path.GetFileNameWithoutExtension(baseName);
        if (string.IsNullOrWhiteSpace(stem))
            stem = "document";

        if (fileVersion == 1)
            return $"{stem}{ext}";

        return $"{stem}_v{fileVersion}{ext}";
    }

    /// <summary>SQL LIKE pattern for all versions of a base file (e.g. <c>invoice_v%.pdf</c>).</summary>
    public static string BuildVersionedFileNameLikePattern(string baseFileName)
    {
        var baseName = GetBaseFileName(baseFileName);
        var ext = Path.GetExtension(baseName);
        var stem = Path.GetFileNameWithoutExtension(baseName);
        if (string.IsNullOrWhiteSpace(stem))
            stem = "document";

        return string.IsNullOrEmpty(ext)
            ? $"{stem}_v%"
            : $"{stem}_v%{ext}";
    }

    internal static string AppendVersionToFileSegment(string fileSegmentWithExt, int fileVersion)
    {
        if (fileVersion < 1)
            fileVersion = 1;

        var ext = Path.GetExtension(fileSegmentWithExt);
        var stem = Path.GetFileNameWithoutExtension(fileSegmentWithExt);
        if (string.IsNullOrWhiteSpace(stem))
            stem = "document";

        stem = StripVersionSuffixFromStem(stem);
        if (fileVersion == 1)
            return $"{stem}{ext}";

        return $"{stem}_v{fileVersion}{ext}";
    }

    private static string StripVersionSuffixFromStem(string stem)
    {
        var match = VersionSuffixRegex.Match(stem);
        return match.Success ? stem[..match.Index] : stem;
    }

    private static readonly Regex VersionSuffixRegex = new(@"_v\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string SanitizePathSegment(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return string.Empty;

        var invalid = Path.GetInvalidFileNameChars();
        var chars = trimmed
            .Select(c => invalid.Contains(c) || c is '/' or '\\' or ':' ? '_' : c)
            .ToArray();

        // Do NOT Trim('.') — that strips a trailing extension when callers sanitize a full file name.
        var cleaned = new string(chars).Trim();
        cleaned = Regex.Replace(cleaned, @"\s+", " ");
        // Strip only trailing dots that are not the extension separator (e.g. "invoice.pdf..." → "invoice.pdf").
        cleaned = Regex.Replace(cleaned, @"\.+$", string.Empty);
        return cleaned.Length > 200 ? cleaned[..200] : cleaned;
    }

    private static string GetExtension(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext) || ext == ".")
            return ".pdf";
        return ext.ToLowerInvariant();
    }

    private static string? ExtensionFromContentType(string? contentType)
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
}
