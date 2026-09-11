using SaaSApp.Repository.Application.Contracts;
using SaaSApp.Repository.Infrastructure.Storage;

namespace SaaSApp.Repository.Infrastructure.Services;

/// <summary>
/// Archive blob/file name: folder path from <see cref="RepositoryFieldDto.IncludeInFolderStructure"/>
/// except the naming field. Prefer a non-folder field with Level above folder max; otherwise the
/// highest folder-structure field is the file stem (not a folder segment).
/// </summary>
internal static class RepositoryArchiveFileNameResolver
{
    public static RepositoryFieldDto? ResolveNamingField(
        IReadOnlyList<RepositoryFieldDto> allFields,
        IReadOnlyList<RepositoryFieldDto> orderedFolderFields)
    {
        var folderMaxLevel = orderedFolderFields.Count > 0
            ? orderedFolderFields.Max(f => f.Level)
            : -1;

        var dedicated = allFields
            .Where(f => !f.IncludeInFolderStructure && f.Level > folderMaxLevel)
            .OrderByDescending(f => f.Level)
            .ThenBy(f => f.OrderId ?? int.MaxValue)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (dedicated != null)
            return dedicated;

        // No level above folders → highest IncludeInFolderStructure field is the file name.
        if (orderedFolderFields.Count == 0)
            return null;

        return orderedFolderFields[^1];
    }

    /// <summary>Folder path levels only (excludes naming field when it is a folder-structure field).</summary>
    public static IReadOnlyList<RepositoryFieldDto> PathFolderFields(
        IReadOnlyList<RepositoryFieldDto> allFields,
        IReadOnlyList<RepositoryFieldDto> orderedFolderFields)
    {
        var naming = ResolveNamingField(allFields, orderedFolderFields);
        if (naming == null || !naming.IncludeInFolderStructure)
            return orderedFolderFields;

        return orderedFolderFields
            .Where(f => !string.Equals(f.SqlColumnName, naming.SqlColumnName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static string? ResolveArchiveFileStem(
        IReadOnlyList<RepositoryFieldDto> allFields,
        IReadOnlyDictionary<string, string> metadata)
    {
        var folderFields = RepositoryFolderStructureHelper.OrderFolderFields(
            allFields.Where(f => f.IncludeInFolderStructure));
        var namingField = ResolveNamingField(allFields, folderFields);
        if (namingField == null)
            return null;

        return RepositoryFolderMetadataResolver.ResolveSegmentName(metadata, namingField);
    }

    /// <summary>Last naming column is Filename — archive file must be the uploaded file name, not metadata like "string".</summary>
    public static bool IsOriginalFileNameField(RepositoryFieldDto field)
    {
        static string Compact(string? value) =>
            string.IsNullOrWhiteSpace(value) ? string.Empty : value.Replace(" ", "").Replace("_", "");

        return Compact(field.Name).Equals("Filename", StringComparison.OrdinalIgnoreCase)
            || Compact(field.SqlColumnName).Equals("Filename", StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolveArchiveBaseFileName(
        IReadOnlyList<RepositoryFieldDto> allFields,
        IReadOnlyDictionary<string, string> metadata,
        string originalFileName)
    {
        var folderFields = RepositoryFolderStructureHelper.OrderFolderFields(
            allFields.Where(f => f.IncludeInFolderStructure));
        var namingField = ResolveNamingField(allFields, folderFields);
        if (namingField != null && IsOriginalFileNameField(namingField))
        {
            return RepositoryFilePathHelper.EnsureFileNameHasExtension(
                RepositoryFilePathHelper.GetBaseFileName(originalFileName),
                filePath: originalFileName);
        }

        var stem = ResolveArchiveFileStem(allFields, metadata);
        var ext = Path.GetExtension(originalFileName);
        if (string.IsNullOrEmpty(ext) || ext == ".")
            ext = ".pdf";
        else
            ext = ext.ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(stem))
            return RepositoryFilePathHelper.EnsureFileNameHasExtension(
                RepositoryFilePathHelper.GetBaseFileName(originalFileName),
                filePath: originalFileName);

        // Naming metadata is a stem (invoice/PO no.). Strip any accidental extension before appending.
        var rawStem = Path.GetFileNameWithoutExtension(stem);
        if (string.IsNullOrWhiteSpace(rawStem))
            rawStem = stem;
        stem = RepositoryFilePathHelper.SanitizePathSegment(rawStem);
        if (string.IsNullOrWhiteSpace(stem))
            return RepositoryFilePathHelper.EnsureFileNameHasExtension(
                RepositoryFilePathHelper.GetBaseFileName(originalFileName),
                filePath: originalFileName);

        return RepositoryFilePathHelper.EnsureFileNameHasExtension($"{stem}{ext}", filePath: originalFileName);
    }

    public static void EnsureMandatoryNamingMetadata(
        IReadOnlyList<RepositoryFieldDto> allFields,
        IReadOnlyDictionary<string, string> metadata,
        bool allowIncompleteFolderMetadata = false)
    {
        if (allowIncompleteFolderMetadata)
            return;

        var folderFields = RepositoryFolderStructureHelper.OrderFolderFields(
            allFields.Where(f => f.IncludeInFolderStructure));
        var namingField = ResolveNamingField(allFields, folderFields);
        if (namingField == null || !namingField.IsMandatory)
            return;

        var stem = RepositoryFolderMetadataResolver.ResolveSegmentName(metadata, namingField);
        if (!string.IsNullOrWhiteSpace(stem))
            return;

        throw new InvalidOperationException(
            $"Archive file name requires metadata field '{namingField.Name}' (sql: {namingField.SqlColumnName}, level: {namingField.Level}).");
    }
}
