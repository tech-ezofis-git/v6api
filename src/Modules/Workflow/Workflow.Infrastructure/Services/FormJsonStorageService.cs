using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Infrastructure.Services;

public sealed class FormJsonStorageService : IFormJsonStorageService
{
    private readonly ITenantContext _tenantContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FormJsonStorageService> _logger;

    public FormJsonStorageService(ITenantContext tenantContext, IConfiguration configuration, ILogger<FormJsonStorageService> logger)
    {
        _tenantContext = tenantContext;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SaveFormJsonAsync(string formId, string json, CancellationToken cancellationToken = default)
    {
        if (await TrySaveToBlobAsync(formId, json, cancellationToken))
            return;

        var folder = Path.Combine(AppContext.BaseDirectory, "Form Json");
        Directory.CreateDirectory(folder);
        var filePath = Path.Combine(folder, $"{SanitizeFileName(formId)}.json");
        await File.WriteAllTextAsync(filePath, json, cancellationToken);
        _logger.LogInformation("Form JSON saved for form {FormId} at {FilePath}", formId, filePath);
    }

    public async Task<string?> GetFormJsonAsync(string formId, CancellationToken cancellationToken = default)
    {
        var fromBlob = await TryGetFromBlobAsync(formId, cancellationToken);
        if (fromBlob != null)
            return fromBlob;

        var filePath = Path.Combine(AppContext.BaseDirectory, "Form Json", $"{SanitizeFileName(formId)}.json");
        if (!File.Exists(filePath))
            return null;

        return await File.ReadAllTextAsync(filePath, cancellationToken);
    }

    private static string SanitizeFileName(string formId) =>
        string.Concat(formId.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));

    private async Task<bool> TrySaveToBlobAsync(string formId, string json, CancellationToken cancellationToken)
    {
        var blobClient = GetBlobClient(formId);
        if (blobClient == null)
            return false;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        await blobClient.UploadAsync(stream, overwrite: true, cancellationToken);
        _logger.LogInformation("Form JSON saved to blob for form {FormId}", formId);
        return true;
    }

    private async Task<string?> TryGetFromBlobAsync(string formId, CancellationToken cancellationToken)
    {
        var blobClient = GetBlobClient(formId);
        if (blobClient == null || !await blobClient.ExistsAsync(cancellationToken))
            return null;

        var download = await blobClient.DownloadContentAsync(cancellationToken);
        return download.Value.Content.ToString();
    }

    private BlobClient? GetBlobClient(string formId)
    {
        var connectionString = _configuration["EzofisBlobStorage:ConnectionString"]
            ?? _configuration["WorkflowJsonStorage:Blob:ConnectionString"]
            ?? _configuration["WorkflowJsonStorage:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;

        var tenantId = _tenantContext.TenantId?.ToString("N").ToLowerInvariant() ?? "default";
        var containerPrefix = (_configuration["EzofisBlobStorage:ContainerPrefix"]
            ?? _configuration["WorkflowJsonStorage:Blob:ContainerPrefix"]
            ?? "ezts").ToLowerInvariant();
        var containerName = $"{containerPrefix}{tenantId}";
        var blobPath = $"Form Json/{SanitizeFileName(formId)}.json";

        var service = new BlobServiceClient(connectionString);
        var container = service.GetBlobContainerClient(containerName);
        container.CreateIfNotExists();
        return container.GetBlobClient(blobPath);
    }
}
