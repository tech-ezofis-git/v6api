using MediatR;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Application.Workflows;

namespace SaaSApp.Workflow.Application.Workflows.Queries.GetInstanceComments;

public sealed class GetInstanceCommentsQueryHandler : IRequestHandler<GetInstanceCommentsQuery, GetInstanceCommentsQueryResult>
{
    private readonly IWorkflowRepository _workflowRepository;
    private readonly IDynamicTableRepository _dynamicTableRepository;

    public GetInstanceCommentsQueryHandler(IWorkflowRepository workflowRepository, IDynamicTableRepository dynamicTableRepository)
    {
        _workflowRepository = workflowRepository;
        _dynamicTableRepository = dynamicTableRepository;
    }

    public async Task<GetInstanceCommentsQueryResult> Handle(GetInstanceCommentsQuery request, CancellationToken cancellationToken)
    {
        var instance = await _workflowRepository.GetInstanceByIdAsync(request.InstanceId, cancellationToken);
        WorkflowInstanceScopeValidator.EnsureInstanceBelongsToWorkflow(instance, request.WorkflowId, request.InstanceId);

        var comments = await _dynamicTableRepository.GetCommentsAsync(request.WorkflowId, request.InstanceId, cancellationToken);

        var commentItems = comments
            .Where(c => !WorkflowCommentHelper.IsAutomaticRuleProceedComment(c.Comments))
            .Select(c =>
            {
                var isAgent = IsAgentComment(c.Comments, c.ExternalCommentsBy);
                var external = isAgent ? AgentAuthorName : c.ExternalCommentsBy;
                return new CommentItem(
                    c.Id,
                    request.WorkflowId,
                    c.WorkflowInstanceId,
                    c.StepInstanceId,
                    AgentDisplayText(c.Comments),
                    external,
                    c.ShowTo,
                    c.EmbedJson,
                    c.EmbedStatus,
                    c.CreatedAtUtc,
                    c.CreatedBy,
                    AuthorName: isAgent ? AgentAuthorName : c.ExternalCommentsBy,
                    AuthorType: isAgent ? "AGENT" : (c.ExternalCommentsBy is null ? "USER" : "EXTERNAL"),
                    IsAgent: isAgent);
            }).ToList();

        var tableName = _dynamicTableRepository.GetTableName(request.WorkflowId, "WorkflowComments");

        return new GetInstanceCommentsQueryResult(request.WorkflowId, request.InstanceId, commentItems, tableName);
    }

    private const string AgentAuthorName = "AI Agent";

    /// <summary>
    /// Agent-generated moves (FTL qualifier/quote/document) are shown as AI Agent, not as the
    /// human who started the job. New rows carry ExternalCommentsBy="AI Agent"; older rows are
    /// recognised by their legacy "FTL &lt;mode&gt;" comment text or the former Pilot label.
    /// </summary>
    private static bool IsAgentComment(string? comments, string? externalCommentsBy)
    {
        if (!string.IsNullOrWhiteSpace(externalCommentsBy)
            && (string.Equals(externalCommentsBy.Trim(), AgentAuthorName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(externalCommentsBy.Trim(), "Pilot", StringComparison.OrdinalIgnoreCase)
                || string.Equals(externalCommentsBy.Trim(), "AP Agent Pilot", StringComparison.OrdinalIgnoreCase)))
            return true;

        var text = comments?.Trim();
        return string.Equals(text, "FTL qualifier", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "FTL quote", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "FTL document", StringComparison.OrdinalIgnoreCase)
            || (text?.StartsWith("Qualifier agent", StringComparison.OrdinalIgnoreCase) ?? false)
            || (text?.StartsWith("Quote agent", StringComparison.OrdinalIgnoreCase) ?? false)
            || (text?.StartsWith("Document agent", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>Legacy rows stored the raw mode label; present them in the agent's voice.</summary>
    private static string AgentDisplayText(string comments)
    {
        var text = comments?.Trim();
        if (string.Equals(text, "FTL qualifier", StringComparison.OrdinalIgnoreCase))
            return "Qualifier agent completed.";
        if (string.Equals(text, "FTL quote", StringComparison.OrdinalIgnoreCase))
            return "Quote agent completed.";
        if (string.Equals(text, "FTL document", StringComparison.OrdinalIgnoreCase))
            return "Document agent completed.";
        return comments;
    }
}
