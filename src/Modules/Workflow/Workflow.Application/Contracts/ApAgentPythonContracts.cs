namespace SaaSApp.Workflow.Application.Contracts;

/// <summary>Arguments for the AP Agent Python background job (multipart start with file only).</summary>
public sealed record ApAgentPythonJobArgs(
    Guid TenantId,
    Guid UserId,
    Guid WorkflowId,
    Guid InstanceId,
    string StartPayloadJson,
    /// <summary>
    /// Optional skill subset for <c>/chat</c>. Null/empty = full tenant default plan on agents
    /// (unless <c>ApAgent:DefaultSkills</c> is configured).
    /// </summary>
    IReadOnlyList<string>? Skills = null);

public interface IApAgentPythonJobClient
{
    Task<string> EnqueueAsync(ApAgentPythonJobArgs args, CancellationToken cancellationToken = default);
}

public interface IApAgentPythonPipelineService
{
    Task ExecuteAsync(
        ApAgentPythonJobArgs args,
        string? hangfireJobId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Exact JSON body POSTed to agents <c>/chat</c> for the given job args.</summary>
    string BuildChatRequestJson(ApAgentPythonJobArgs args, string? hangfireJobId = null);
}
