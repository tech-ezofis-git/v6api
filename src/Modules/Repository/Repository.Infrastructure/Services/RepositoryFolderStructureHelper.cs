using SaaSApp.Repository.Application.Contracts;

namespace SaaSApp.Repository.Infrastructure.Services;

/// <summary>
/// Ordered repository fields marked <see cref="RepositoryFieldDto.IncludeInFolderStructure"/>.
/// Path segments exclude the archive naming field (see <see cref="RepositoryArchiveFileNameResolver"/>).
/// </summary>
internal static class RepositoryFolderStructureHelper
{
    public static IReadOnlyList<RepositoryFieldDto> OrderFolderFields(IEnumerable<RepositoryFieldDto> fields) =>
        fields
            .OrderBy(f => f.Level)
            .ThenBy(f => f.OrderId ?? int.MaxValue)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
