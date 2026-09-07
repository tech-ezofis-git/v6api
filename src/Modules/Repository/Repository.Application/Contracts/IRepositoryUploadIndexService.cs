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

    /// <summary>Stage to monitor blob, run OCR, persist OcrJson + extracted fields; return fileId.</summary>
    Task<UploadWithOcrResult> UploadWithOcrAsync(
        Guid repositoryId,
        Guid tenantId,
        Stream fileStream,
        string fileName,
        string? contentType,
        long fileSize,
        string? fieldsJson,
        string? pageNo,
        string? ocrType,
        string? validateType,
        Guid? userId,
        CancellationToken cancellationToken = default);

    /// <summary>Promote staged file to archive layout (synchronous; for workflow start).</summary>
    Task<UploadIndexPromoteResult?> PromoteStageAsync(
        Guid stageId,
        Guid repositoryId,
        Guid tenantId,
        Guid? userId,
        CancellationToken cancellationToken = default);

    Task<UploadIndexLoadResult?> LoadAsync(
        Guid stageId,
        Guid tenantId,
        CancellationToken cancellationToken = default);

    Task<UploadIndexArchiveQueuedResult?> QueueArchiveAsync(
        Guid stageId,
        Guid tenantId,
        UploadIndexSaveRequest request,
        Guid? userId,
        CancellationToken cancellationToken = default);

    Task<UploadIndexListResult> ListIndexAsync(
        Guid tenantId,
        UploadIndexListRequest request,
        CancellationToken cancellationToken = default);
}
