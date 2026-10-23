using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Npgsql;
using SaaSApp.MultiTenancy;
using SaaSApp.Repository.Application.Contracts;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Infrastructure.Options;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>
/// Extracts attachments embedded in a .eml already archived on a workflow instance and archives
/// each one as its own repository item / workflow attachment, skipping anything already
/// extracted (by content hash) and anything outside the configured type/size allow-list.
/// Additive: never touches how the ticket was started or how the .eml itself got archived.
/// </summary>
public sealed class EmlAttachmentExtractionService : IEmlAttachmentExtractionService
{
    private readonly IWorkflowRepository _workflowRepository;
    private readonly IRepositoryItemQueryService _itemQuery;
    private readonly IWorkflowAttachmentArchiveService _attachmentArchive;
    private readonly ITenantConnectionProvider _connectionProvider;
    private readonly EmlAttachmentExtractionOptions _options;
    private readonly ILogger<EmlAttachmentExtractionService> _logger;

    public EmlAttachmentExtractionService(
        IWorkflowRepository workflowRepository,
        IRepositoryItemQueryService itemQuery,
        IWorkflowAttachmentArchiveService attachmentArchive,
        ITenantConnectionProvider connectionProvider,
        IOptions<EmlAttachmentExtractionOptions> options,
        ILogger<EmlAttachmentExtractionService> logger)
    {
        _workflowRepository = workflowRepository;
        _itemQuery = itemQuery;
        _attachmentArchive = attachmentArchive;
        _connectionProvider = connectionProvider;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<EmlAttachmentExtractionResult> ExtractAsync(
        Guid tenantId,
        Guid workflowId,
        Guid instanceId,
        Guid userId,
        Guid? sourceRepositoryId,
        Guid? sourceItemId,
        CancellationToken cancellationToken = default)
    {
        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureTrackingTableAsync(connection, cancellationToken);

        var workflow = await _workflowRepository.GetByIdWithStepsAsync(workflowId, cancellationToken)
            ?? throw new InvalidOperationException("Workflow not found.");
        var suffix = workflowId.ToString("N")[..8];

        var source = sourceItemId is { } explicitItemId && explicitItemId != Guid.Empty
            ? await ResolveExplicitSourceAsync(connection, suffix, instanceId, sourceRepositoryId, explicitItemId, cancellationToken)
            : await FindLatestEmlAttachmentAsync(connection, suffix, instanceId, cancellationToken);

        if (source is null)
            throw new InvalidOperationException("No .eml attachment was found on this workflow instance.");

        var (repositoryId, itemId, fileName) = source.Value;

        var fileContent = await _itemQuery.OpenItemFileAsync(repositoryId, tenantId, itemId, cancellationToken)
            ?? throw new InvalidOperationException("The .eml attachment's file content could not be opened.");

        MimeMessage message;
        await using (fileContent.Stream)
            message = await MimeMessage.LoadAsync(fileContent.Stream, cancellationToken);

        var extracted = new List<ExtractedEmlAttachment>();
        var skipped = new List<SkippedEmlAttachment>();
        var allowedExtensions = new HashSet<string>(_options.AllowedExtensions, StringComparer.OrdinalIgnoreCase);

        foreach (var entity in message.Attachments)
        {
            if (entity is not MimePart part)
                continue;

            var attachmentName = string.IsNullOrWhiteSpace(part.FileName)
                ? $"attachment-{extracted.Count + skipped.Count + 1}{GuessExtension(part.ContentType?.MimeType)}"
                : part.FileName.Trim();

            var extension = Path.GetExtension(attachmentName);
            if (string.IsNullOrWhiteSpace(extension) || !allowedExtensions.Contains(extension))
            {
                skipped.Add(new SkippedEmlAttachment(attachmentName, $"File type '{extension}' is not allowed."));
                continue;
            }

            byte[] bytes;
            using (var buffer = new MemoryStream())
            {
                await part.Content.DecodeToAsync(buffer, cancellationToken);
                bytes = buffer.ToArray();
            }

            if (bytes.Length == 0)
            {
                skipped.Add(new SkippedEmlAttachment(attachmentName, "Attachment was empty."));
                continue;
            }

            if (bytes.Length > _options.MaxAttachmentSizeBytes)
            {
                skipped.Add(new SkippedEmlAttachment(
                    attachmentName,
                    $"Attachment exceeds the {_options.MaxAttachmentSizeBytes / (1024 * 1024)} MB limit."));
                continue;
            }

            var contentHash = Convert.ToHexString(SHA256.HashData(bytes));
            if (await AlreadyExtractedAsync(connection, instanceId, contentHash, cancellationToken))
            {
                skipped.Add(new SkippedEmlAttachment(attachmentName, "Already extracted from this instance (duplicate content)."));
                continue;
            }

            var contentType = part.ContentType?.MimeType;
            await using var attachmentStream = new MemoryStream(bytes);
            var archived = await _attachmentArchive.UploadAsync(
                tenantId,
                workflowId,
                instanceId,
                repositoryId,
                attachmentStream,
                attachmentName,
                contentType,
                bytes.Length,
                metadataJson: null,
                transactionId: null,
                userId,
                cancellationToken,
                allowIncompleteFolderMetadata: true);

            await RecordExtractionAsync(
                connection,
                tenantId,
                workflowId,
                instanceId,
                itemId,
                archived.ItemId,
                archived.AttachmentId,
                attachmentName,
                contentType,
                bytes.Length,
                contentHash,
                userId,
                cancellationToken);

            extracted.Add(new ExtractedEmlAttachment(
                archived.AttachmentId,
                archived.ItemId,
                attachmentName,
                contentType,
                bytes.Length,
                contentHash));
        }

        _logger.LogInformation(
            "EML attachment extraction for instance {InstanceId}: {ExtractedCount} archived, {SkippedCount} skipped (source {SourceFileName}).",
            instanceId,
            extracted.Count,
            skipped.Count,
            fileName);

        return new EmlAttachmentExtractionResult(itemId, fileName, extracted, skipped);
    }

    private static string GuessExtension(string? mimeType) => mimeType?.ToLowerInvariant() switch
    {
        "application/pdf" => ".pdf",
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        _ => ".bin"
    };

    private static async Task<(Guid RepositoryId, Guid ItemId, string FileName)?> ResolveExplicitSourceAsync(
        NpgsqlConnection connection,
        string workflowSuffix,
        Guid instanceId,
        Guid? repositoryIdHint,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        if (repositoryIdHint is { } repoId && repoId != Guid.Empty)
        {
            var fileName = await TryGetFileNameAsync(connection, workflowSuffix, instanceId, itemId, cancellationToken)
                ?? "message.eml";
            return (repoId, itemId, fileName);
        }

        const string sqlTemplate = """
            SELECT repository_id, file_name
            FROM workflow.workflow_attachments_{0}
            WHERE workflow_instance_id = @InstanceId AND item_id = @ItemId AND is_deleted = false
            ORDER BY created_at_utc DESC
            LIMIT 1;
            """;
        await using var cmd = new NpgsqlCommand(
            string.Format(sqlTemplate, workflowSuffix), connection);
        cmd.Parameters.AddWithValue("@InstanceId", instanceId);
        cmd.Parameters.AddWithValue("@ItemId", itemId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return (reader.GetGuid(0), itemId, reader.IsDBNull(1) ? "message.eml" : reader.GetString(1));
    }

    private static async Task<string?> TryGetFileNameAsync(
        NpgsqlConnection connection,
        string workflowSuffix,
        Guid instanceId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT file_name
            FROM workflow.workflow_attachments_{workflowSuffix}
            WHERE workflow_instance_id = @InstanceId AND item_id = @ItemId AND is_deleted = false
            ORDER BY created_at_utc DESC
            LIMIT 1;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@InstanceId", instanceId);
        cmd.Parameters.AddWithValue("@ItemId", itemId);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result as string;
    }

    /// <summary>Most recently archived .eml-shaped attachment on this instance.</summary>
    private static async Task<(Guid RepositoryId, Guid ItemId, string FileName)?> FindLatestEmlAttachmentAsync(
        NpgsqlConnection connection,
        string workflowSuffix,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT repository_id, item_id, file_name
            FROM workflow.workflow_attachments_{workflowSuffix}
            WHERE workflow_instance_id = @InstanceId
              AND is_deleted = false
              AND (file_name ILIKE '%.eml' OR content_type ILIKE '%rfc822%')
            ORDER BY created_at_utc DESC
            LIMIT 1;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@InstanceId", instanceId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return (reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? "message.eml" : reader.GetString(2));
    }

    private static async Task EnsureTrackingTableAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS workflow.eml_attachment_extractions (
                id uuid NOT NULL PRIMARY KEY,
                tenant_id uuid NOT NULL,
                workflow_id uuid NOT NULL,
                instance_id uuid NOT NULL,
                source_item_id uuid NOT NULL,
                extracted_item_id uuid NOT NULL,
                extracted_attachment_id uuid NOT NULL,
                file_name varchar(500) NOT NULL,
                content_type varchar(200) NULL,
                file_size bigint NOT NULL,
                content_hash varchar(64) NOT NULL,
                created_at_utc timestamptz NOT NULL,
                created_by uuid NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_eml_attachment_extractions_instance_hash
                ON workflow.eml_attachment_extractions (instance_id, content_hash);
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> AlreadyExtractedAsync(
        NpgsqlConnection connection,
        Guid instanceId,
        string contentHash,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT 1 FROM workflow.eml_attachment_extractions
            WHERE instance_id = @InstanceId AND content_hash = @ContentHash
            LIMIT 1;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@InstanceId", instanceId);
        cmd.Parameters.AddWithValue("@ContentHash", contentHash);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result != null;
    }

    private static async Task RecordExtractionAsync(
        NpgsqlConnection connection,
        Guid tenantId,
        Guid workflowId,
        Guid instanceId,
        Guid sourceItemId,
        Guid extractedItemId,
        Guid extractedAttachmentId,
        string fileName,
        string? contentType,
        long fileSize,
        string contentHash,
        Guid createdBy,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO workflow.eml_attachment_extractions
                (id, tenant_id, workflow_id, instance_id, source_item_id, extracted_item_id,
                 extracted_attachment_id, file_name, content_type, file_size, content_hash,
                 created_at_utc, created_by)
            VALUES
                (@Id, @TenantId, @WorkflowId, @InstanceId, @SourceItemId, @ExtractedItemId,
                 @ExtractedAttachmentId, @FileName, @ContentType, @FileSize, @ContentHash,
                 now(), @CreatedBy);
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        cmd.Parameters.AddWithValue("@WorkflowId", workflowId);
        cmd.Parameters.AddWithValue("@InstanceId", instanceId);
        cmd.Parameters.AddWithValue("@SourceItemId", sourceItemId);
        cmd.Parameters.AddWithValue("@ExtractedItemId", extractedItemId);
        cmd.Parameters.AddWithValue("@ExtractedAttachmentId", extractedAttachmentId);
        cmd.Parameters.AddWithValue("@FileName", fileName);
        cmd.Parameters.AddWithValue("@ContentType", (object?)contentType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@FileSize", fileSize);
        cmd.Parameters.AddWithValue("@ContentHash", contentHash);
        cmd.Parameters.AddWithValue("@CreatedBy", createdBy);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
