using MediatR;

namespace SaaSApp.Workflow.Application.Workflows.Queries.GetInstanceComments;

/// <summary>Get comments for a workflow instance from workflow-specific table.</summary>
public record GetInstanceCommentsQuery(Guid WorkflowId, Guid InstanceId) : IRequest<GetInstanceCommentsQueryResult>;

/// <summary>Comments from workflow-specific table.</summary>
public record GetInstanceCommentsQueryResult(
    Guid WorkflowId,
    Guid InstanceId,
    List<CommentItem> Comments,
    string TableName);

/// <summary>Comment item.</summary>
public record CommentItem(
    Guid Id,
    Guid WorkflowId,
    Guid WorkflowInstanceId,
    Guid? StepInstanceId,
    string Comments,
    string? ExternalCommentsBy,
    int ShowTo,
    string? EmbedJson,
    bool EmbedStatus,
    DateTime CreatedAtUtc,
    Guid CreatedBy,
    /// <summary>Display author for the UI. Agent comments use "AI Agent"; do not resolve CreatedBy to a person for those.</summary>
    string? AuthorName = null,
    string AuthorType = "USER",
    bool IsAgent = false
);
