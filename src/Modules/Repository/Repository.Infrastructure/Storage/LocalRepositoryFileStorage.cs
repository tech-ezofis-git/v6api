using Microsoft.Extensions.Options;
using SaaSApp.Repository.Infrastructure.Options;

using SaaSApp.Repository.Application.Contracts;

namespace SaaSApp.Repository.Infrastructure.Storage;

internal sealed class LocalRepositoryFileStorage : IRepositoryFileStorage
{
    private readonly RepositoryFileStorageOptions _options;

    public LocalRepositoryFileStorage(IOptions<RepositoryFileStorageOptions> options) =>
        _options = options.Value;

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
        if (!IsLocalProvider(storageProviderCode))
            throw new NotSupportedException($"Storage provider '{storageProviderCode}' file save is not implemented. Use EZOFIS for local dev.");

        var relativePath = storageRelativePath
            ?? RepositoryFilePathHelper.BuildFlatRelativePath(repositoryId, itemId, fileName);
        var fullPath = GetFullPath(tenantId, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using var file = File.Create(fullPath);
        await content.CopyToAsync(file, cancellationToken);

        return NormalizeRelativePath(relativePath);
    }

    public Task<Stream> OpenReadAsync(
        Guid tenantId,
        string relativePath,
        string storageProviderCode,
        CancellationToken cancellationToken = default)
    {
        if (!IsLocalProvider(storageProviderCode))
            throw new NotSupportedException($"Storage provider '{storageProviderCode}' file read is not implemented.");

        var fullPath = GetFullPath(tenantId, relativePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Repository file not found on disk.", fullPath);

        Stream stream = File.OpenRead(fullPath);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(
        Guid tenantId,
        string relativePath,
        string storageProviderCode,
        CancellationToken cancellationToken = default)
    {
        if (!IsLocalProvider(storageProviderCode))
            throw new NotSupportedException($"Storage provider '{storageProviderCode}' file delete is not implemented.");

        if (string.IsNullOrWhiteSpace(relativePath))
            return Task.CompletedTask;

        var fullPath = GetFullPath(tenantId, relativePath);
        if (File.Exists(fullPath))
            File.Delete(fullPath);

        // Best-effort: remove empty monitor timestamp folder.
        try
        {
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch (IOException)
        {
            // ignore
        }

        return Task.CompletedTask;
    }

    public bool CanRead(string storageProviderCode) => IsLocalProvider(storageProviderCode);

    private string GetFullPath(Guid tenantId, string relativePath)
    {
        var root = Path.GetFullPath(_options.LocalRootPath);
        var combined = Path.GetFullPath(Path.Combine(
            root,
            tenantId.ToString("N"),
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!combined.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid file path.");
        return combined;
    }

    private static bool IsLocalProvider(string code) =>
        string.Equals(code, "EZOFIS", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/');
}
