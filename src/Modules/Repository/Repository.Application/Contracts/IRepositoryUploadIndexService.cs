namespace SaaSApp.Repository.Application.Contracts;

/// <summary>v5-compatible upload → index → archive (uses V6 stage table + archive upload).</summary>
public interface IRepositoryUploadIndexService
{
    Task<UploadIndexUploadResult> UploadAsync(
        Guid repositoryId,
        Guid tenantId,
        Stream fileStream,
        string fileName,
        string? contentType,
        long fileSize,
        string? fieldsJson,
        Guid? userId,
        CancellationToken cancellationToken = default);

    /// <summary>Call Python OCR (base64 file + parameters from fields); no monitor/stage storage.</summary>
    Task<UploadForOcrResult> UploadForOcrAsync(
        Guid repositoryId,
        Guid tenantId,
        Stream fileStream,
        string? fieldsJson,
        string? pageNo,
        string? ocrType,
        string? validateType,
        string? filename = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stage file to monitor blob + stage table using caller-provided OCR metadata (no OCR call).
    /// Typical flow: uploadForOcr → then uploadWithOcr(file, repositoryId, metadata).
    /// </summary>
    Task<UploadWithOcrResult> UploadWithOcrAsync(
        Guid repositoryId,
        Guid tenantId,
        Stream fileStream,
        string fileName,
        string? contentType,
        long fileSize,
        string? metadataJson,
        string? ocrJson,
        string? ocrText,
        Guid? userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Promote a staged monitor file into repository archive (items table + blob) using stage field data.
    /// When the repository's resolved naming field (see RepositoryArchiveFileNameResolver) has no value
    /// from the stage row or OCR, <paramref name="namingFieldFallback"/> fills it -- e.g. the workflow
    /// ticket's reference number for a repository whose naming field is a per-ticket request number.
    /// Never overrides an explicit value and never applies to a field that represents the literal
    /// uploaded file name (that one is always backfilled from the file name itself).
    /// </summary>
    Task<UploadIndexPromoteResult?> PromoteStageAsync(
        Guid stageId,
        Guid repositoryId,
        Guid tenantId,
        Guid? userId,
        CancellationToken cancellationToken = default,
        bool allowIncompleteFolderMetadata = false,
        string? namingFieldFallback = null);

    Task<UploadIndexLoadResult?> LoadAsync(
        Guid stageId,
        Guid tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>Open staged monitor file stream for view/download (by stage fileId).</summary>
    Task<RepositoryItemFileContent?> OpenStageFileAsync(
        Guid stageId,
        Guid tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>Open a staged monitor file by its blob path (monitor/...), with no fileId.</summary>
    Task<RepositoryItemFileContent?> OpenMonitorFileByPathAsync(
        string monitorPath,
        Guid tenantId,
        CancellationToken cancellationToken = default);

    Task<UploadIndexArchiveQueuedResult?> QueueArchiveAsync(
        Guid stageId,
        Guid tenantId,
        UploadIndexSaveRequest request,
        Guid? userId,
        CancellationToken cancellationToken = default);

    /// <summary>Update stage field columns only. Does not archive or change stage status.</summary>
    Task<UploadIndexStageFieldsResult?> SaveStageFieldsAsync(
        Guid stageId,
        Guid tenantId,
        UploadIndexStageFieldsRequest request,
        Guid? userId,
        CancellationToken cancellationToken = default);

    Task<UploadIndexListResult> ListIndexAsync(
        Guid tenantId,
        UploadIndexListRequest request,
        Guid indexedByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-delete staged files and remove their monitor temp blobs.
    /// </summary>
    Task<UploadIndexDeleteFilesResult> DeleteStageFilesAsync(
        Guid tenantId,
        IReadOnlyList<Guid> stageIds,
        Guid? repositoryId,
        Guid? userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk upload: stage all files to monitor + stage table with shared fields, then queue Hangfire OCR.
    /// Returns immediately with jobId — poll <see cref="GetBulkUploadJobStatusAsync"/>.
    /// Files stay in stage until export (PUT index/{id}).
    /// </summary>
    Task<BulkUploadResult> BulkUploadAsync(
        Guid repositoryId,
        Guid tenantId,
        IReadOnlyList<(Stream Stream, string FileName, string? ContentType, long FileSize)> files,
        string? sharedFieldsJson,
        string? pageNo,
        string? ocrType,
        string? validateType,
        Guid? userId,
        CancellationToken cancellationToken = default);

    /// <summary>Run OCR for one staged bulk-upload file (called by Hangfire job).</summary>
    Task ProcessBulkOcrForStageAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid stageId,
        string? sharedFieldsJson,
        string? pageNo,
        string? ocrType,
        string? validateType,
        Guid? userId,
        CancellationToken cancellationToken = default);

    /// <summary>Hangfire + stage-row status for a bulk OCR job.</summary>
    Task<BulkUploadJobStatusResult?> GetBulkUploadJobStatusAsync(
        string jobId,
        Guid tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// First active bulk OCR Hangfire job for this tenant (Processing, then Enqueued).
    /// Optional <paramref name="repositoryId"/> filters to one repository.
    /// </summary>
    Task<BulkUploadJobStatusResult?> GetActiveBulkUploadJobStatusAsync(
        Guid tenantId,
        Guid? repositoryId = null,
        CancellationToken cancellationToken = default);
}
