using WorkflowEntity = SaaSApp.Workflow.Domain.Entities.Workflow;
using SaaSApp.Workflow.Application.Workflows.Commands.StartWorkflow;

namespace SaaSApp.Workflow.Application.Contracts;

public sealed record MjbUsAgentJobArgs(
    Guid TenantId,
    Guid UserId,
    Guid WorkflowId,
    Guid InstanceId,
    string ActivityId,
    string BlobPath,
    string FileName,
    string? FromEmail,
    string? Subject,
    string? ReceivedAt,
    string? MessageId,
    string? RepositoryId = null,
    string? FormId = null);

public interface IMjbUsAgentJobClient
{
    Task<string> EnqueueAsync(MjbUsAgentJobArgs args, CancellationToken cancellationToken = default);
}

public interface IMjbUsAgentPipelineService
{
    Task ExecuteAsync(MjbUsAgentJobArgs args, string hangfireJobId, CancellationToken cancellationToken = default);
}

/// <summary>
/// MJB_US email start: store the attachment under monitor/Ramco_mjb, fill the mail
/// fields, start the ticket, and enqueue classification.
/// </summary>
public interface IMjbUsMailWorkflowStarter
{
    Task<StartWorkflowCommandResult> StartAsync(
        WorkflowEntity workflow,
        byte[] attachmentBytes,
        string fileName,
        string? contentType,
        string? fromEmail,
        string? subject,
        DateTime? receivedAtUtc,
        string? messageId,
        CancellationToken cancellationToken = default);
}
