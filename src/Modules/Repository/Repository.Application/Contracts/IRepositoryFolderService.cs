namespace SaaSApp.Repository.Application.Contracts;

/// <summary>
/// Resolves repository folder hierarchy from <see cref="RepositoryFieldDto.IncludeInFolderStructure"/> levels.
/// Persists rows in <c>repository.Folders</c> with <c>ParentId</c> chain; blob path uses archive/{repoName}/{levels}.
/// </summary>
public interface IRepositoryFolderService
{
    /// <summary>
    /// Creates or reuses folder rows per metadata level. Returns null when the repository has no folder-structure fields.
    /// </summary>
    Task<RepositoryFolderResolveResult?> ResolveOrCreateFolderPathAsync(
        Guid repositoryId,
        Guid tenantId,
        IReadOnlyDictionary<string, string> metadata,
        Guid? userId,
        CancellationToken cancellationToken = default,
        bool allowIncompleteFolderMetadata = false);
}

public sealed record RepositoryFolderResolveResult(
    Guid LeafFolderId,
    IReadOnlyList<Guid> FolderChain,
    IReadOnlyList<string> FolderNames,
    string RepositoryName);

public sealed record RepositoryFolderDto(
    Guid Id,
    Guid RepositoryId,
    string Name,
    Guid? ParentId,
    int LevelId,
    string? PathId);
