namespace SaaSApp.Repository.Application.Contracts;

/// <summary>v5 fieldWithValues shape.</summary>
public sealed record UploadIndexFieldDto(string Name, string? Value, string? Type = null);

/// <summary>v5 resPostUpload response.</summary>
public sealed record UploadIndexUploadResult(
    string FileId,
    IReadOnlyList<UploadIndexFieldDto>? OcrFieldList = null);

/// <summary>uploadForOcr response — raw OCR JSON + parsed fields + full document text (no staging).</summary>
public sealed record UploadForOcrResult(
    string OcrJson,
    IReadOnlyList<UploadIndexFieldDto>? OcrFieldList,
    string? OcrText = null);

/// <summary>uploadWithOcr — staged file + caller-provided OCR metadata (pre-ticket).</summary>
public sealed record UploadWithOcrResult(
    string FileId,
    Guid RepositoryId,
    string FileName,
    string FilePath,
    string OcrJson,
    IReadOnlyList<UploadIndexFieldDto>? OcrFieldList);

/// <summary>Promote a staged monitor file into repository archive.</summary>
public sealed record UploadIndexPromoteResult(
    Guid ItemId,
    Guid RepositoryId,
    string FileName,
    string FilePath,
    string? MetadataJson,
    long? FileSize,
    string? ContentType);

/// <summary>v5 resArray.</summary>
public sealed record UploadIndexRefDto(string? Id, string? Value);

/// <summary>v5 resindex response (ids are GUID strings in V6).</summary>
public sealed record UploadIndexLoadResult(
    string Id,
    string TenantId,
    string Name,
    string FilePath,
    long Size,
    UploadIndexRefDto? Workspace,
    UploadIndexRefDto? Repository,
    string ItemId,
    IReadOnlyList<UploadIndexFieldDto> Fields,
    string? Error,
    string Status,
    bool IsVerified,
    string? ArchivePath,
    string? CloudFileServer,
    string? UploadedFrom,
    string? UploadedAt,
    string? CreatedBy,
    string? CreatedAt,
    string? ModifiedBy,
    string? ModifiedAt,
    bool IsDeleted,
    int TotalPage,
    string? HangfireJobId = null,
    string? PromotedItemId = null);

/// <summary>v5 saveInStage body for PUT index/{id}.</summary>
public sealed record UploadIndexSaveRequest(
    Guid RepositoryId,
    string? ItemId,
    string? Status,
    IReadOnlyList<UploadIndexFieldDto>? Fields,
    string? OcrResult = null);

/// <summary>Response when stage file is archived (sync — no Hangfire).</summary>
public sealed record UploadIndexArchiveQueuedResult(
    string StageId,
    string HangfireJobId,
    string Message,
    Guid? ItemId = null,
    string? FileName = null,
    string? FilePath = null);

public sealed record UploadIndexListRequest(
    int CurrentPage = 1,
    int ItemsPerPage = 50,
    string Mode = "browse",
    Guid? RepositoryId = null);

public sealed record UploadIndexListItem(
    string Id,
    string Name,
    string Status,
    string? RepositoryId,
    string? RepositoryName,
    long Size,
    string? CreatedAt,
    string? PromotedItemId);

public sealed record UploadIndexListResult(
    IReadOnlyList<UploadIndexListItem> Items,
    int CurrentPage,
    int ItemsPerPage,
    int TotalItems);

/// <summary>One file in a bulk upload batch (staged immediately; OCR may still be running).</summary>
public sealed record BulkUploadFileResult(
    string FileId,
    Guid RepositoryId,
    string FileName,
    string FilePath,
    string OcrJson,
    IReadOnlyList<UploadIndexFieldDto>? OcrFieldList,
    bool Succeeded,
    string? Error = null,
    string? Status = null);

/// <summary>
/// Immediate bulk-upload response — files are on monitor/stage; OCR runs in Hangfire.
/// Poll GET bulkUpload/jobs/{jobId} for OCR progress.
/// </summary>
public sealed record BulkUploadResult(
    Guid RepositoryId,
    string JobId,
    string Message,
    IReadOnlyList<BulkUploadFileResult> Files,
    int Succeeded,
    int Failed);

/// <summary>Hangfire args for background OCR of a bulk-upload batch (StageIdsCsv = comma-separated GUIDs).</summary>
public sealed record BulkOcrJobArgs(
    Guid TenantId,
    Guid RepositoryId,
    string StageIdsCsv,
    string? SharedFieldsJson,
    string? PageNo,
    string? OcrType,
    string? ValidateType,
    Guid? UserId);

/// <summary>Per-file OCR status for a bulk job.</summary>
public sealed record BulkUploadFileStatusItem(
    string FileId,
    string FileName,
    string Status,
    string? Error = null);

/// <summary>Poll bulk OCR job — Hangfire state + each stage row status.</summary>
public sealed record BulkUploadJobStatusResult(
    string JobId,
    string HangfireState,
    bool IsTerminal,
    string? ErrorMessage,
    Guid RepositoryId,
    IReadOnlyList<BulkUploadFileStatusItem> Files,
    int OcrCompleted,
    int OcrPending,
    int OcrFailed);

public sealed record ArchiveStageJobArgs(
    Guid TenantId,
    Guid RepositoryId,
    Guid StageId,
    Guid? UserId);
