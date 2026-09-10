using SaaSApp.Workflow.Application.Workflows.Commands.StartWorkflow;
using WorkflowEntity = SaaSApp.Workflow.Domain.Entities.Workflow;

namespace SaaSApp.Workflow.Application.Contracts;

/// <summary>
/// Normal workflow email ingest: OCR → stage → map form → StartWorkflow with formData + stagedFiles.
/// </summary>
public interface IEmailIngestNormalWorkflowStarter
{
    Task<StartWorkflowCommandResult> StartAsync(
        WorkflowEntity workflow,
        byte[] attachmentBytes,
        string fileName,
        string? contentType,
        string contextJson,
        CancellationToken cancellationToken = default);
}