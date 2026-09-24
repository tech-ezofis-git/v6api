using SaaSApp.BlobStorage;
using SaaSApp.Repository.Application.Contracts;

namespace SaaSApp.Repository.Infrastructure.Storage;

/// <summary>Repository files on Azure Blob (EZOFIS provider) — one container per tenant (ezts + tenantId).</summary>
internal sealed class EzofisBlobRepositoryFileStorage : IRepositoryFileStorage
{
    private readonly IEzofisBlobStorageService _blobStorage;

    public EzofisBlobRepositoryFileStorage(IEzofisBlobStorageService blobStorage) =>
        _blobStorage = blobStorage;

    public async Task<string> SaveAsync(
        Guid tenantId,
        Guid repositoryId,
        Guid itemId,
        string fileName,
        Stream content,
        string storageProviderCode,
        string? storageRelativePath = null,
        CancellationToken cancellationToken = default)
    {
        EnsureEzofis(storageProviderCode);
        if (!_blobStorage.IsConfigured)
            throw new InvalidOperationException("EzofisBlobStorage is not configured.");

        var blobPath = storageRelativePath
            ?? RepositoryFilePathHelper.BuildFlatRelativePath(repositoryId, itemId, fileName);
        var client = _blobStorage.GetBlobClient(tenantId, blobPath);
        await client.UploadAsync(content, overwrite: true, cancellationToken);
        return blobPath;
    }

    public async Task<Stream> OpenReadAsync(
        Guid tenantId,
        string relativePath,
        string storageProviderCode,
        CancellationToken cancellationToken = default)
    {
        EnsureEzofis(storageProviderCode);
        if (!_blobStorage.IsConfigured)
            throw new InvalidOperationException("EzofisBlobStorage is not configured.");

        var client = _blobStorage.GetBlobClient(tenantId, relativePath, createContainerIfNotExists: false);
        if (!await client.ExistsAsync(cancellationToken))
            throw new FileNotFoundException("Repository file not found in blob storage.", relativePath);

        var response = await client.DownloadStreamingAsync(cancellationToken: cancellationToken);
        return response.Value.Content;
    }

    public async Task DeleteAsync(
        Guid tenantId,
        string relativePath,
        string storageProviderCode,
        CancellationToken cancellationToken = default)
    {
        EnsureEzofis(storageProviderCode);
        if (!_blobStorage.IsConfigured)
            throw new InvalidOperationException("EzofisBlobStorage is not configured.");

        if (string.IsNullOrWhiteSpace(relativePath))
            return;

        var client = _blobStorage.GetBlobClient(tenantId, relativePath.Trim().Replace('\\', '/'), createContainerIfNotExists: false);
        await client.DeleteIfExistsAsync(cancellationToken: cancellationToken);
    }

    public bool CanRead(string storageProviderCode) =>
        IsEzofis(storageProviderCode) && _blobStorage.IsConfigured;

    private static void EnsureEzofis(string code)
    {
        if (!IsEzofis(code))
            throw new NotSupportedException($"Blob storage only supports EZOFIS provider, not '{code}'.");
    }

    private static bool IsEzofis(string code) =>
        string.Equals(code, "EZOFIS", StringComparison.OrdinalIgnoreCase);

    // Intentionally not using original file name in blob path:
    // path is stable and UI/file name is stored in DB.
}
