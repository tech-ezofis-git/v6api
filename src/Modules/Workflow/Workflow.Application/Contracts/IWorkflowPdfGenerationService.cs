using SaaSApp.Workflow.Domain.Entities;

namespace SaaSApp.Workflow.Application.Contracts;

/// <summary>
/// Generates a PDF when leaving a workflow step with generatePDF:true in designer JSON.
/// </summary>
public interface IWorkflowPdfGenerationService
{
    /// <summary>
    /// When the completed step has generatePDF enabled, maps ezfb form data to template dataKeys,
    /// calls Python, archives the PDF, and optionally binds FILE controls.
    /// Returns null when generation is not required or Python URL is not configured.
    /// </summary>
    Task<WorkflowPdfGenerationResult?> TryGenerateOnStepCompleteAsync(
        Workflow.Domain.Entities.Workflow workflow,
        WorkflowInstance instance,
        WorkflowStep completedStep,
        string? formId,
        Guid? formEntryId,
        Guid userId,
        int? transactionId,
        CancellationToken cancellationToken = default,
        string? submittedFormDataJson = null);
}

public sealed record WorkflowPdfGenerationResult(
    Guid AttachmentId,
    Guid ItemId,
    string FileName,
    /// <summary>Exact payload fields sent to Python (for move-next cross-check).</summary>
    WorkflowPdfPythonRequestDto PythonRequest);

/// <summary>Input body fields posted to the Python PDF generate API.</summary>
public sealed record WorkflowPdfPythonRequestDto(
    IReadOnlyDictionary<string, string> FormData,
    string FileName,
    object Metadata,
    /// <summary>pdfme template JSON sent as templateJson.</summary>
    System.Text.Json.JsonElement TemplateJson);
