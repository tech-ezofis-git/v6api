using MediatR;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Application.Workflows.Queries.GetMailboxByActivityIds;

public sealed class GetMailboxActivityCountsQueryHandler : IRequestHandler<GetMailboxActivityCountsQuery, LegacyMailboxActivityCountResult>
{
    private readonly IWorkflowLegacyMailboxQueryService _mailboxQuery;
    private readonly ICurrentUserProvider _currentUserProvider;

    public GetMailboxActivityCountsQueryHandler(
        IWorkflowLegacyMailboxQueryService mailboxQuery,
        ICurrentUserProvider currentUserProvider)
    {
        _mailboxQuery = mailboxQuery;
        _currentUserProvider = currentUserProvider;
    }

    public Task<LegacyMailboxActivityCountResult> Handle(
        GetMailboxActivityCountsQuery request,
        CancellationToken cancellationToken)
    {
        var userId = _currentUserProvider.GetUserId()
            ?? throw new InvalidOperationException("User context is required.");

        return _mailboxQuery.CountByActivityIdsAsync(
            new LegacyMailboxByActivityRequest(request.WorkflowId, request.ActivityIds, userId),
            cancellationToken);
    }
}
