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

    /// <summary>Public status URL for the Hangfire job, when ApAgent:ApiBaseUrl is set.</summary>
    string? StatusUrl(string jobId);
}

public interface IFtlAgentPipelineService
{
    Task ExecuteAsync(FtlAgentJobArgs args, string hangfireJobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the current form data and the document-node template to Python three times
    /// (quoteType quote_1, quote_2, quote_3) and archives each PDF Python returns.
    /// A section with no valid quote (no pdf_base64 in the reply) is skipped;
    /// fails only when no section returns a PDF. Does not move the workflow.
    /// </summary>
    Task<FtlDocumentGenerateResult> GenerateDocumentAsync(
        FtlAgentJobArgs args,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Same as <see cref="GenerateDocumentAsync"/>, but the quote PDF(s) it replaces are
    /// soft-deleted once the new ones are confirmed archived -- only the latest PDF per quote
    /// section stays visible. Does not move the workflow.
    /// </summary>
    Task<FtlDocumentGenerateResult> RegenerateDocumentAsync(
        FtlAgentJobArgs args,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-sends the ticket's already-archived RFQ file to the Qualify Agent and refreshes
    /// formData/validation with the new response. Never moves the workflow -- the caller is
    /// responsible for only calling this while the ticket is on the Qualify Agent step.
    /// </summary>
    Task<FtlRegenerateFormResult> RegenerateQualifierAsync(
        FtlAgentJobArgs args,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-sends the ticket's already-archived RFQ file (and the stored qualifier result) to the
    /// Quote Agent and refreshes formData/validation with the new response. Never moves the
    /// workflow -- the caller is responsible for only calling this while on the Quote Agent step.
    /// </summary>
    Task<FtlRegenerateFormResult> RegenerateQuoteAsync(
        FtlAgentJobArgs args,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Preview only: remap jsonId formData to field names, call Python chat once for the
    /// selected quoteType (quote_1 when omitted), return the PDF.
    /// Does not archive or move the workflow.
    /// </summary>
    Task<FtlDocumentPreviewResult> PreviewDocumentAsync(
        string? formId,
        string formDataJson,
        System.Text.Json.JsonElement templateJson,
        CancellationToken cancellationToken = default,
        string? quoteType = null);
}

/// <summary>
/// One generated quote PDF. AttachmentId/ItemId/FileName on
/// <see cref="FtlDocumentGenerateResult"/> mirror the quote_1 document for compatibility.
/// </summary>
public sealed record FtlGeneratedDocument(
    string QuoteType,
    Guid AttachmentId,
    Guid ItemId,
    string FileName);

/// <summary>PDFs archived from the document-generate Python calls, plus the quote_1 request sent.</summary>
public sealed record FtlDocumentGenerateResult(
    Guid AttachmentId,
    Guid ItemId,
    string FileName,
    WorkflowPdfPythonRequestDto PythonRequest,
    IReadOnlyList<FtlGeneratedDocument> Documents);

/// <summary>Python PDF preview. FormDataJson uses field names, not jsonIds.</summary>
public sealed record FtlDocumentPreviewResult(
    string FileName,
    string PdfBase64,
    string FormDataJson,
    string QuoteType = "quote_1");

/// <summary>Refreshed qualifier/quote output. FormDataJson is jsonId-keyed, same shape as mailbox formData.</summary>
public sealed record FtlRegenerateFormResult(
    string AgentResponseJson,
    string FormDataJson);
