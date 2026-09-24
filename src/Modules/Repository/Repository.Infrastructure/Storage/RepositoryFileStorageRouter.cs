using SaaSApp.BlobStorage;
using SaaSApp.Repository.Application.Contracts;

namespace SaaSApp.Repository.Infrastructure.Storage;

/// <summary>Routes EZOFIS to Azure Blob (EzofisBlobStorage); falls back to local disk when blob is not configured.</summary>
internal sealed class RepositoryFileStorageRouter : IRepositoryFileStorage
{
    private readonly IEzofisBlobStorageService _blobStorage;
    private readonly EzofisBlobRepositoryFileStorage _blobFiles;
    private readonly LocalRepositoryFileStorage _localFiles;

    public RepositoryFileStorageRouter(
        IEzofisBlobStorageService blobStorage,
        EzofisBlobRepositoryFileStorage blobFiles,
        LocalRepositoryFileStorage localFiles)
    {
        _blobStorage = blobStorage;
        _blobFiles = blobFiles;
        _localFiles = localFiles;
    }

    public Task<string> SaveAsync(
        Guid tenantId,
        Guid repositoryId,
        Guid itemId,
        string fileName,
        Stream content,
        string storageProviderCode,
        string? storageRelativePath = null,
        CancellationToken cancellationToken = default)
    {
        if (IsEzofis(storageProviderCode) && _blobStorage.IsConfigured)
            return _blobFiles.SaveAsync(tenantId, repositoryId, itemId, fileName, content, storageProviderCode, storageRelativePath, cancellationToken);

        if (IsEzofis(storageProviderCode))
            return _localFiles.SaveAsync(tenantId, repositoryId, itemId, fileName, content, storageProviderCode, storageRelativePath, cancellationToken);

        throw new NotSupportedException($"Storage provider '{storageProviderCode}' is not implemented.");
    }

    public Task<Stream> OpenReadAsync(
        Guid tenantId,
        string relativePath,
        string storageProviderCode,
        CancellationToken cancellationToken = default)
    {
        if (IsEzofis(storageProviderCode) && _blobStorage.IsConfigured)
            return _blobFiles.OpenReadAsync(tenantId, relativePath, storageProviderCode, cancellationToken);

        if (IsEzofis(storageProviderCode))
            return _localFiles.OpenReadAsync(tenantId, relativePath, storageProviderCode, cancellationToken);

        throw new NotSupportedException($"Storage provider '{storageProviderCode}' is not implemented.");
    }

    public Task DeleteAsync(
        Guid tenantId,
        string relativePath,
        string storageProviderCode,
        CancellationToken cancellationToken = default)
    {
        if (IsEzofis(storageProviderCode) && _blobStorage.IsConfigured)
            return _blobFiles.DeleteAsync(tenantId, relativePath, storageProviderCode, cancellationToken);

        if (IsEzofis(storageProviderCode))
            return _localFiles.DeleteAsync(tenantId, relativePath, storageProviderCode, cancellationToken);

        throw new NotSupportedException($"Storage provider '{storageProviderCode}' is not implemented.");
    }

    public bool CanRead(string storageProviderCode) =>
        IsEzofis(storageProviderCode) && (_blobStorage.IsConfigured || _localFiles.CanRead(storageProviderCode));

    private static bool IsEzofis(string code) =>
        string.Equals(code, "EZOFIS", StringComparison.OrdinalIgnoreCase);
}
