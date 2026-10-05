using MediatR;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Application.Workflows.Queries.GetMailboxByActivityIds;

public sealed record GetMailboxActivityCountsQuery(
    Guid WorkflowId,
    IReadOnlyList<string> ActivityIds) : IRequest<LegacyMailboxActivityCountResult>;
