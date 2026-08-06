using SaaSApp.Repository.Application.Contracts;
using SaaSApp.Repository.Infrastructure.Storage;

namespace SaaSApp.Repository.Infrastructure.Services;

/// <summary>
/// Archive blob/file name: folder levels from <see cref="RepositoryFieldDto.IncludeInFolderStructure"/>;
/// the file stem comes from the highest-level metadata field above those folder levels (e.g. PoNumber at level 3).
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

        return allFields
            .Where(f => !f.IncludeInFolderStructure && f.Level > folderMaxLevel)
            .OrderByDescending(f => f.Level)
            .ThenBy(f => f.OrderId ?? int.MaxValue)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
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

    public static string ResolveArchiveBaseFileName(
        IReadOnlyList<RepositoryFieldDto> allFields,
        IReadOnlyDictionary<string, string> metadata,
        string originalFileName)
    {
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
        IReadOnlyDictionary<string, string> metadata)
    {
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
