using SaaSApp.Repository.Application.Contracts;
using SaaSApp.Repository.Infrastructure.Storage;

namespace SaaSApp.Repository.Infrastructure.Services;

/// <summary>
/// OCR does not extract the repository archive naming field (last-level filename). Manual upload
/// supplies it from the original PDF name; automated paths must inject it before stage/archive.
/// </summary>
internal static class RepositoryNamingFieldMetadataInjector
{
    public static void InjectFromOriginalFileName(
        IReadOnlyList<RepositoryFieldDto> allFields,
        IDictionary<string, string> metadata,
        string? originalFileName,
        string? contentType = null)
    {
        var namingField = ResolveNamingField(allFields);
        if (namingField == null)
            return;

        // Only a naming field that genuinely represents "the uploaded file's name" (field Name/
        // SqlColumnName is literally "Filename") should ever be backfilled from the upload --
        // for that field it always wins, even over an existing value, since OCR can only have
        // put a placeholder there. A naming field that happens to be the deepest
        // IncludeInFolderStructure level for an unrelated reason (e.g. a "Request Number" field
        // used to name the archive file/folder) must never be overwritten with the file name:
        // doing so silently destroyed whatever real value that field was supposed to hold
        // whenever the caller didn't also supply one.
        if (!RepositoryArchiveFileNameResolver.IsOriginalFileNameField(namingField))
            return;

        var archiveFileName = TryGetArchiveFileName(originalFileName, contentType);
        if (string.IsNullOrWhiteSpace(archiveFileName))
            return;

        // Prefer a single key so stage UPDATE SET does not list the same column twice
        // (display Name + SqlColumnName both resolve to one SQL column).
        if (!string.IsNullOrWhiteSpace(namingField.SqlColumnName))
            metadata[namingField.SqlColumnName.Trim()] = archiveFileName;
        else
            metadata[namingField.Name.Trim()] = archiveFileName;
    }

    /// <summary>
    /// Fills the resolved naming field with <paramref name="fallbackValue"/> when it still has no
    /// value after stage/OCR/original-filename injection -- e.g. a workflow ticket's reference
    /// number for a repository whose naming field is a per-ticket request number. Never overrides
    /// a value the caller/OCR already supplied, and never applies to a field that represents the
    /// literal uploaded file name (that one only ever comes from the upload itself).
    /// </summary>
    public static void ApplyFallbackIfMissing(
        IReadOnlyList<RepositoryFieldDto> allFields,
        IDictionary<string, string> metadata,
        string? fallbackValue)
    {
        if (string.IsNullOrWhiteSpace(fallbackValue))
            return;

        var namingField = ResolveNamingField(allFields);
        if (namingField == null || RepositoryArchiveFileNameResolver.IsOriginalFileNameField(namingField))
            return;

        if (HasNamingValue(metadata, namingField))
            return;

        if (!string.IsNullOrWhiteSpace(namingField.SqlColumnName))
            metadata[namingField.SqlColumnName.Trim()] = fallbackValue.Trim();
        else
            metadata[namingField.Name.Trim()] = fallbackValue.Trim();
    }

    public static void EnsureInFieldList(
        IReadOnlyList<RepositoryFieldDto> allFields,
        IList<UploadIndexFieldDto> fieldList,
        string? originalFileName,
        IReadOnlyDictionary<string, string>? metadata = null,
        string? contentType = null)
    {
        var namingField = ResolveNamingField(allFields);
        if (namingField == null)
            return;

        if (fieldList.Any(f => FieldMatches(f.Name, namingField)))
            return;

        if (metadata != null && HasNamingValue(metadata, namingField))
        {
            var value = ResolveNamingValue(metadata, namingField);
            if (!string.IsNullOrWhiteSpace(value))
            {
                fieldList.Add(new UploadIndexFieldDto(namingField.Name, value, namingField.DataType));
                return;
            }
        }

        // Same rule as InjectFromOriginalFileName: only suggest the upload's own file name for a
        // naming field that genuinely represents "Filename". A naming field with no value that
        // happens to be an unrelated business field (e.g. "Request Number") should stay unset
        // here, not be suggested as if the file name were its answer.
        if (!RepositoryArchiveFileNameResolver.IsOriginalFileNameField(namingField))
            return;

        var archiveFileName = TryGetArchiveFileName(originalFileName, contentType);
        if (string.IsNullOrWhiteSpace(archiveFileName))
            return;

        fieldList.Add(new UploadIndexFieldDto(namingField.Name, archiveFileName, namingField.DataType));
    }

    private static RepositoryFieldDto? ResolveNamingField(IReadOnlyList<RepositoryFieldDto> allFields)
    {
        var folderFields = RepositoryFolderStructureHelper.OrderFolderFields(
            allFields.Where(f => f.IncludeInFolderStructure));
        return RepositoryArchiveFileNameResolver.ResolveNamingField(allFields, folderFields);
    }

    private static string? TryGetArchiveFileName(string? originalFileName, string? contentType = null)
    {
        if (string.IsNullOrWhiteSpace(originalFileName))
            return null;

        var baseName = Path.GetFileName(originalFileName.Trim());
        if (string.IsNullOrWhiteSpace(baseName))
            return null;

        return RepositoryFilePathHelper.EnsureFileNameHasExtension(
            RepositoryFilePathHelper.GetBaseFileName(baseName),
            contentType,
            originalFileName);
    }

    private static bool HasNamingValue(
        IEnumerable<KeyValuePair<string, string>> metadata,
        RepositoryFieldDto namingField)
    {
        return !string.IsNullOrWhiteSpace(ResolveNamingValue(metadata, namingField));
    }

    private static string? ResolveNamingValue(
        IEnumerable<KeyValuePair<string, string>> metadata,
        RepositoryFieldDto namingField)
    {
        foreach (var (key, value) in metadata)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            if (string.Equals(key, namingField.Name, StringComparison.OrdinalIgnoreCase))
                return value.Trim();

            if (!string.IsNullOrWhiteSpace(namingField.SqlColumnName)
                && string.Equals(key, namingField.SqlColumnName, StringComparison.OrdinalIgnoreCase))
            {
                return value.Trim();
            }

            if (FieldMatches(key, namingField))
                return value.Trim();
        }

        return null;
    }

    private static bool FieldMatches(string? key, RepositoryFieldDto field)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        return string.Equals(key.Trim(), field.Name, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(field.SqlColumnName)
                && string.Equals(key.Trim(), field.SqlColumnName, StringComparison.OrdinalIgnoreCase));
    }
}
