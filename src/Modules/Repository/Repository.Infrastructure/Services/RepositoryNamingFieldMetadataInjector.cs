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

        var archiveFileName = TryGetArchiveFileName(originalFileName, contentType);
        if (string.IsNullOrWhiteSpace(archiveFileName))
            return;

        // Filename column must keep the uploaded file name, not a placeholder such as "string".
        if (HasNamingValue(metadata, namingField)
            && !RepositoryArchiveFileNameResolver.IsOriginalFileNameField(namingField))
            return;

        // Prefer a single key so stage UPDATE SET does not list the same column twice
        // (display Name + SqlColumnName both resolve to one SQL column).
        if (!string.IsNullOrWhiteSpace(namingField.SqlColumnName))
            metadata[namingField.SqlColumnName.Trim()] = archiveFileName;
        else
            metadata[namingField.Name.Trim()] = archiveFileName;
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
