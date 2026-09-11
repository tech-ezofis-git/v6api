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

    /// <summary>Promote a staged monitor file into repository archive (items table + blob).</summary>
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
