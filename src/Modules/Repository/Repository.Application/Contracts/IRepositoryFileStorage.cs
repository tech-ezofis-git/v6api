namespace SaaSApp.Repository.Application.Contracts;

public interface IRepositoryFileStorage
{
    Task<string> SaveAsync(
        Guid tenantId,
        Guid repositoryId,
        Guid itemId,
        string fileName,
        Stream content,
        string storageProviderCode,
        string? storageRelativePath = null,
        CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(
        Guid tenantId,
        string relativePath,
        string storageProviderCode,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a blob/file at <paramref name="relativePath"/> if it exists (best-effort for monitor cleanup).</summary>
    Task DeleteAsync(
        Guid tenantId,
        string relativePath,
        string storageProviderCode,
        CancellationToken cancellationToken = default);

    bool CanRead(string storageProviderCode);
}
