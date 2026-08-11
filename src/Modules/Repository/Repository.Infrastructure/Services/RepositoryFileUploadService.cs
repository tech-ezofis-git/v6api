using Npgsql;
using SaaSApp.MultiTenancy;
using SaaSApp.Repository.Application.Contracts;
using SaaSApp.Repository.Infrastructure.Storage;

namespace SaaSApp.Repository.Infrastructure.Services;

/// <summary>Legacy upload: flat blob path repository/{repositoryId}/{itemId}.ext</summary>
public sealed class RepositoryFileUploadService : IRepositoryFileUploadService
{
    private readonly ITenantConnectionProvider _connectionProvider;
    private readonly IStaticRepositoryProvisioner _provisioner;
    private readonly IRepositoryStorageSeedService _storageSeed;
    private readonly IRepositoryFileStorage _fileStorage;
    private readonly RepositoryWorkflowAttachService _workflowAttach;

    public RepositoryFileUploadService(
        ITenantConnectionProvider connectionProvider,
        IStaticRepositoryProvisioner provisioner,
        IRepositoryStorageSeedService storageSeed,
        IRepositoryFileStorage fileStorage,
        RepositoryWorkflowAttachService workflowAttach)
    {
        _connectionProvider = connectionProvider;
        _provisioner = provisioner;
        _storageSeed = storageSeed;
        _fileStorage = fileStorage;
        _workflowAttach = workflowAttach;
    }

    public async Task<RepositoryUploadItemResult> UploadItemAsync(
        Guid repositoryId,
        Guid tenantId,
        RepositoryUploadItemRequest request,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        if (request.FileStream == null || !request.FileStream.CanRead)
            throw new ArgumentException("File stream is required.");

        if (string.IsNullOrWhiteSpace(request.FileName))
            throw new ArgumentException("File name is required.");

        var repo = await _provisioner.GetRepositoryAsync(repositoryId, tenantId, cancellationToken)
            ?? throw new InvalidOperationException("Repository not found.");

        ValidateWorkflowArgs(request);

        if (request.WorkflowId is Guid wfId && request.InstanceId is Guid instId)
            await _workflowAttach.ValidateInstanceAsync(wfId, instId, cancellationToken);

        var itemId = Guid.NewGuid();
        var storageProviderId = await _storageSeed.ResolveStorageProviderIdAsync(
            tenantId,
            repo.StorageProviderId,
            string.IsNullOrWhiteSpace(request.StorageProviderCode) ? null : request.StorageProviderCode,
            cancellationToken);

        var providers = await _storageSeed.ListProvidersAsync(tenantId, cancellationToken);
        var providerCode = providers.First(p => p.Id == storageProviderId).Code;

        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var baseFileName = RepositoryFilePathHelper.GetBaseFileName(request.FileName);
        var fileVersion = await RepositoryItemVersionResolver.ResolveNextFileVersionAsync(
            connection,
            repo.ItemsTableName,
            tenantId,
            repositoryId,
            folderId: null,
            baseFileName,
            cancellationToken);

        var versionedFileName = RepositoryFilePathHelper.ApplyVersionToFileName(baseFileName, fileVersion);

        var relativePath = await _fileStorage.SaveAsync(
            tenantId,
            repositoryId,
            itemId,
            request.FileName,
            request.FileStream,
            providerCode,
            storageRelativePath: null,
            cancellationToken);

        var fieldValues = RepositoryItemFilterHelper.ParseMetadataJson(request.Metadata);
        var fileSize = request.FileSize is > 0 and <= int.MaxValue
            ? (int?)request.FileSize
            : request.FileStream.CanSeek && request.FileStream.Length <= int.MaxValue
                ? (int)request.FileStream.Length
                : null;

        var createRequest = new CreateRepositoryItemRequest(
            storageProviderId,
            relativePath,
            RepositoryFilePathHelper.EnsureFileNameHasExtension(versionedFileName, request.ContentType, relativePath),
            request.ContentType,
            fileSize,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            FolderId: null,
            request.InstanceId,
            fieldValues,
            fileVersion);

        await RepositoryItemInsertHelper.InsertItemAsync(
            connection, repo, tenantId, repositoryId, itemId, storageProviderId, createRequest, userId, cancellationToken);

        var workflowAttached = false;
        if (request.WorkflowId is Guid workflowId && request.InstanceId is Guid workflowInstanceId)
        {
            await _workflowAttach.AttachAsync(
                workflowId,
                workflowInstanceId,
                request.TransactionId,
                repositoryId,
                itemId,
                createRequest.FileName!,
                relativePath,
                fileSize,
                request.ContentType,
                userId,
                stepInstanceId: null,
                cancellationToken);
            workflowAttached = true;
        }

        return new RepositoryUploadItemResult(
            itemId,
            createRequest.FileName!,
            relativePath,
            providerCode,
            fileVersion,
            workflowAttached,
            request.WorkflowId,
            request.ProcessId,
            request.InstanceId);
    }

    private static void ValidateWorkflowArgs(RepositoryUploadItemRequest request)
    {
        var hasWorkflow = request.WorkflowId.HasValue;
        var hasInstance = request.InstanceId.HasValue;

        if (!hasWorkflow && !hasInstance)
            return;

        if (!hasWorkflow)
            throw new ArgumentException("workflowId is required when linking to a workflow.");

        if (!hasInstance)
            throw new ArgumentException("instanceId is required when workflowId is provided (use the WorkflowInstances.Id GUID, not legacy process id).");
    }
}
