using Npgsql;
using SaaSApp.MultiTenancy;
using SaaSApp.Repository.Application.Contracts;
using SaaSApp.Repository.Infrastructure.Services;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Api.Services;

public sealed class WorkflowAttachmentArchiveService : IWorkflowAttachmentArchiveService
{
    private readonly IRepositoryArchiveFileUploadService _archiveUpload;
    private readonly IRepositoryUploadIndexService _uploadIndex;
    private readonly IWorkflowProcessAddonService _processAddon;
    private readonly IRepositoryItemQueryService _itemQuery;
    private readonly RepositoryWorkflowAttachService _workflowAttach;
    private readonly ITenantConnectionProvider _connectionProvider;

    public WorkflowAttachmentArchiveService(
        IRepositoryArchiveFileUploadService archiveUpload,
        IRepositoryUploadIndexService uploadIndex,
        IWorkflowProcessAddonService processAddon,
        IRepositoryItemQueryService itemQuery,
        RepositoryWorkflowAttachService workflowAttach,
        ITenantConnectionProvider connectionProvider)
    {
        _archiveUpload = archiveUpload;
        _uploadIndex = uploadIndex;
        _processAddon = processAddon;
        _itemQuery = itemQuery;
        _workflowAttach = workflowAttach;
        _connectionProvider = connectionProvider;
    }

    public async Task<WorkflowAttachmentArchiveResult> UploadAsync(
        Guid tenantId,
        Guid workflowId,
        Guid instanceId,
        Guid repositoryId,
        Stream fileStream,
        string fileName,
        string? contentType,
        long? fileSize,
        string? metadataJson,
        int? transactionId,
        Guid userId,
        CancellationToken cancellationToken = default,
        bool allowIncompleteFolderMetadata = false)
    {
        var request = new RepositoryUploadItemRequest(
            fileStream,
            fileName,
            contentType,
            workflowId,
            ProcessId: null,
            instanceId,
            transactionId,
            StorageProviderCode: null,
            fileSize,
            metadataJson,
            AllowIncompleteFolderMetadata: allowIncompleteFolderMetadata);

        var upload = await _archiveUpload.UploadItemAsync(
            repositoryId,
            tenantId,
            request,
            userId,
            cancellationToken);

        var attachmentId = upload.WorkflowAttached
            ? await FindAttachmentIdAsync(workflowId, instanceId, upload.ItemId, cancellationToken)
            : await _workflowAttach.AttachAsync(
                workflowId,
                instanceId,
                transactionId,
                repositoryId,
                upload.ItemId,
                upload.FileName,
                upload.FilePath,
                fileSize,
                contentType,
                userId,
                cancellationToken: cancellationToken);

        var processAddonId = await _processAddon.InsertAsync(
            workflowId,
            instanceId,
            repositoryId,
            upload.ItemId,
            upload.FileName,
            transactionId,
            userId,
            cancellationToken);

        return new WorkflowAttachmentArchiveResult(
            attachmentId,
            upload.ItemId,
            repositoryId,
            workflowId,
            instanceId,
            processAddonId,
            upload.FileName,
            upload.FilePath,
            upload.StorageProviderCode,
            upload.FileVersion,
            upload.FolderId,
            upload.FolderPathSegments);
    }

    public async Task<WorkflowAttachmentArchiveResult?> PromoteFromStageAsync(
        Guid tenantId,
        Guid workflowId,
        Guid instanceId,
        Guid repositoryId,
        Guid stageId,
        int? transactionId,
        Guid userId,
        CancellationToken cancellationToken = default,
        bool allowIncompleteFolderMetadata = false,
        string? formJsonId = null)
    {
        var promoted = await _uploadIndex.PromoteStageAsync(
            stageId,
            repositoryId,
            tenantId,
            userId,
            cancellationToken,
            allowIncompleteFolderMetadata);
        if (promoted == null)
            return null;

        var fileName = string.IsNullOrWhiteSpace(promoted.FileName)
            ? $"{promoted.ItemId:D}.bin"
            : promoted.FileName;
        var filePath = string.IsNullOrWhiteSpace(promoted.FilePath)
            ? $"repository/{promoted.RepositoryId:N}/{promoted.ItemId:N}"
            : promoted.FilePath;

        var attachmentId = await _workflowAttach.AttachAsync(
            workflowId,
            instanceId,
            transactionId,
            promoted.RepositoryId,
            promoted.ItemId,
            fileName,
            filePath,
            promoted.FileSize,
            promoted.ContentType,
            userId,
            formJsonId: formJsonId,
            cancellationToken: cancellationToken);

        var processAddonId = await _processAddon.InsertAsync(
            workflowId,
            instanceId,
            promoted.RepositoryId,
            promoted.ItemId,
            fileName,
            transactionId,
            userId,
            cancellationToken);

        return new WorkflowAttachmentArchiveResult(
            attachmentId,
            promoted.ItemId,
            promoted.RepositoryId,
            workflowId,
            instanceId,
            processAddonId,
            fileName,
            filePath,
            string.Empty,
            1,
            null,
            Array.Empty<string>());
    }

    public async Task<WorkflowAttachmentArchiveResult?> AttachExistingArchiveItemAsync(
        Guid tenantId,
        Guid workflowId,
        Guid instanceId,
        Guid repositoryId,
        Guid itemId,
        string? fileName,
        int? transactionId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (repositoryId == Guid.Empty || itemId == Guid.Empty)
            return null;

        RepositoryItemDetailDto? item;
        try
        {
            item = await _itemQuery.GetItemAsync(repositoryId, tenantId, itemId, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        if (item == null)
            return null;

        var resolvedName = !string.IsNullOrWhiteSpace(fileName)
            ? fileName.Trim()
            : item.FileName;
        if (string.IsNullOrWhiteSpace(resolvedName))
            resolvedName = $"{itemId:D}.bin";

        var filePath = item.FilePath;
        if (string.IsNullOrWhiteSpace(filePath))
            filePath = $"repository/{repositoryId:N}/{item.Id:N}";

        var fileSize = item.FileSize is int size ? (long?)size : null;
        var attachmentId = await _workflowAttach.AttachAsync(
            workflowId,
            instanceId,
            transactionId,
            repositoryId,
            item.Id,
            resolvedName,
            filePath,
            fileSize,
            item.FileType,
            userId,
            cancellationToken: cancellationToken);

        var processAddonId = await _processAddon.InsertAsync(
            workflowId,
            instanceId,
            repositoryId,
            item.Id,
            resolvedName,
            transactionId,
            userId,
            cancellationToken);

        return new WorkflowAttachmentArchiveResult(
            attachmentId,
            item.Id,
            repositoryId,
            workflowId,
            instanceId,
            processAddonId,
            resolvedName,
            filePath,
            item.StorageProviderCode ?? string.Empty,
            1,
            null,
            Array.Empty<string>());
    }

    private async Task<Guid> FindAttachmentIdAsync(
        Guid workflowId,
        Guid instanceId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");
        var suffix = workflowId.ToString("N")[..8];
        var table = $"workflow.workflow_attachments_{suffix}";

        var sql = $"""
            SELECT id
            FROM {table}
            WHERE workflow_instance_id = @InstanceId AND item_id = @ItemId AND is_deleted = false
            ORDER BY created_at_utc DESC
            LIMIT 1;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@InstanceId", instanceId);
        cmd.Parameters.AddWithValue("@ItemId", itemId);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is Guid guid ? guid : Guid.Empty;
    }
}
