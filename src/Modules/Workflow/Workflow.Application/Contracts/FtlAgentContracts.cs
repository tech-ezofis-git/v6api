namespace SaaSApp.Workflow.Application.Contracts;

/// <summary>Hangfire args for FTL qualifier, quote estimator, or document PDF. Not used by AP Agent.</summary>
public sealed record FtlAgentJobArgs(
    Guid TenantId,
    Guid UserId,
    Guid WorkflowId,
    Guid InstanceId,
    string ActivityId,
    string Mode,
    string? RepositoryId = null,
    string? FormId = null);

public interface IFtlAgentJobClient
{
    Task<string> EnqueueAsync(FtlAgentJobArgs args, CancellationToken cancellationToken = default);
}

public interface IFtlAgentPipelineService
{
    Task ExecuteAsync(FtlAgentJobArgs args, string hangfireJobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the current form data and the document-node template to Python, archives the PDF.
    /// Does not move the workflow.
    /// </summary>
    Task<FtlDocumentGenerateResult> GenerateDocumentAsync(
        FtlAgentJobArgs args,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Preview only: remap jsonId formData to field names, call Python chat, return the PDF.
    /// Does not archive or move the workflow.
    /// </summary>
    Task<FtlDocumentPreviewResult> PreviewDocumentAsync(
        string? formId,
        string formDataJson,
        System.Text.Json.JsonElement templateJson,
        CancellationToken cancellationToken = default);
}

/// <summary>PDF archived from the document-generate Python call, plus the exact request sent.</summary>
public sealed record FtlDocumentGenerateResult(
    Guid AttachmentId,
    Guid ItemId,
    string FileName,
    WorkflowPdfPythonRequestDto PythonRequest);

/// <summary>Python PDF preview. FormDataJson uses field names, not jsonIds.</summary>
public sealed record FtlDocumentPreviewResult(
    string FileName,
    string PdfBase64,
    string FormDataJson);
