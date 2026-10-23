using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
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

        // Copy folder/document fields from the source .eml onto each extracted file so they
        // share Company Name / Project / etc. and appear under the same repo browse path.
        var metadataJson = await BuildMetadataJsonFromSourceItemAsync(
            repositoryId, tenantId, itemId, cancellationToken);

        var fileContent = await _itemQuery.OpenItemFileAsync(repositoryId, tenantId, itemId, cancellationToken)
            ?? throw new InvalidOperationException("The .eml attachment's file content could not be opened.");

        MimeMessage message;
        await using (fileContent.Stream)
            message = await MimeMessage.LoadAsync(fileContent.Stream, cancellationToken);

        var extracted = new List<ExtractedEmlAttachment>();
        var skipped = new List<SkippedEmlAttachment>();
        var allowedExtensions = new HashSet<string>(_options.AllowedExtensions, StringComparer.OrdinalIgnoreCase);

        foreach (var part in EnumerateCandidateAttachmentParts(message))
        {
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

            if (_options.SkipSignatureImages
                && IsLikelySignatureOrLogoImage(part, attachmentName, bytes))
            {
                skipped.Add(new SkippedEmlAttachment(
                    attachmentName,
                    "Company logo / email signature image; not archived as an attachment."));
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
                metadataJson,
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

    /// <summary>
    /// MimeKit's <c>MimeMessage.Attachments</c> only returns parts with
    /// Content-Disposition: attachment, which misses photos/receipts/screenshots that many mail
    /// clients forward as Content-Disposition: inline with a Content-ID (used to render inline in
    /// the HTML body) -- the exact shape a forwarded "Fw:" email's embedded JPGs/PNGs take.
    /// Also includes PDF/PNG/JPEG parts that have no filename (only a mime type / Content-ID);
    /// the extension allow-list and size cap still filter what gets archived.
    /// </summary>
    private static IEnumerable<MimePart> EnumerateCandidateAttachmentParts(MimeMessage message)
    {
        foreach (var entity in message.BodyParts)
        {
            if (entity is not MimePart part)
                continue;

            if (part.IsAttachment || !string.IsNullOrWhiteSpace(part.FileName))
            {
                yield return part;
                continue;
            }

            // Inline PDF/PNG/JPEG with no FileName (common in RFQ / quote-request .eml forwards).
            if (IsArchivableEmbeddedMime(part.ContentType?.MimeType))
                yield return part;
        }
    }

    private static bool IsArchivableEmbeddedMime(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
            return false;

        return mimeType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
            || mimeType.Equals("image/png", StringComparison.OrdinalIgnoreCase)
            || mimeType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase)
            || mimeType.Equals("image/jpg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Signature / company-logo chrome: logo-like file names, or small inline/CID images.
    /// Larger inline photos (RFQ screenshots) are kept.
    /// </summary>
    private bool IsLikelySignatureOrLogoImage(MimePart part, string fileName, byte[] bytes)
    {
        if (!IsImagePart(part, fileName))
            return false;

        if (HasSignatureImageFileName(fileName))
            return true;

        // HTML-embedded signature chrome: Content-Disposition: inline and/or a Content-ID.
        var disposition = part.ContentDisposition?.Disposition;
        var isInline = string.Equals(disposition, ContentDisposition.Inline, StringComparison.OrdinalIgnoreCase)
            || (string.IsNullOrWhiteSpace(disposition) && !string.IsNullOrWhiteSpace(part.ContentId));

        return isInline && bytes.Length < _options.MinNonSignatureImageBytes;
    }

    private static bool IsImagePart(MimePart part, string fileName)
    {
        var mime = part.ContentType?.MimeType ?? string.Empty;
        if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return true;

        var ext = Path.GetExtension(fileName);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSignatureImageFileName(string? fileName)
    {
        var name = Path.GetExtension(fileName ?? string.Empty).Length > 0
            ? Path.GetFileNameWithoutExtension(fileName)
            : (fileName ?? string.Empty);
        name = name.Trim();
        if (string.IsNullOrEmpty(name))
            return false;

        return Regex.IsMatch(
            name,
            @"^(image|img|logo|signature|sig|banner|badge|icon|cid_|untitled|company[_-]?logo|email[_-]?logo)\d*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string GuessExtension(string? mimeType) => mimeType?.ToLowerInvariant() switch
    {
        "application/pdf" => ".pdf",
        "image/png" => ".png",
        "image/jpeg" or "image/jpg" => ".jpg",
        _ => ".bin"
    };

    private static readonly HashSet<string> MetadataCopyExcludedColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "Id", "Code", "TenantId", "RepositoryId", "FolderId", "StorageProviderId",
        "FilePath", "FileName", "FileType", "FileSize", "TotalPages", "IsDeleted",
        "CreatedAtUtc", "CreatedBy", "ModifiedAtUtc", "ModifiedBy",
        "WorkflowInstanceId", "OcrText", "OcrJson", "SummaryJson", "ActiveItem", "IsVerified",
        "FileVersion", "Revision", "StageStatus", "MailId", "ArchivedFrom", "ArchivedAt"
    };

    /// <summary>
    /// Builds archive metadata JSON from the source .eml item's custom/folder fields so extracted
    /// PDF/PNG siblings land in the same Document Details / browse folders.
    /// </summary>
    private async Task<string?> BuildMetadataJsonFromSourceItemAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        try
        {
            var item = await _itemQuery.GetItemAsync(repositoryId, tenantId, itemId, cancellationToken);
            if (item?.Fields is not { Count: > 0 })
                return null;

            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, raw) in item.Fields)
            {
                if (string.IsNullOrWhiteSpace(key) || MetadataCopyExcludedColumns.Contains(key))
                    continue;
                if (raw is null || raw is DBNull)
                    continue;

                var text = raw switch
                {
                    string s => s.Trim(),
                    DateTime dt => dt.ToString("O"),
                    DateTimeOffset dto => dto.ToString("O"),
                    _ => Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture)?.Trim()
                };
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                metadata[key.Trim()] = text;
            }

            return metadata.Count == 0 ? null : JsonSerializer.Serialize(metadata);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not copy metadata from source EML item {ItemId}; extracted attachments will archive without inherited fields.",
                itemId);
            return null;
        }
    }

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
