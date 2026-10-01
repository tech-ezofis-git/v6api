using MediatR;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Application.Workflows.Queries.GetMailboxByActivityIds;

public sealed class GetMailboxByActivityIdsQueryHandler : IRequestHandler<GetMailboxByActivityIdsQuery, LegacyMailboxListResult>
{
    private readonly IWorkflowLegacyMailboxQueryService _mailboxQuery;
    private readonly ICurrentUserProvider _currentUserProvider;

    public GetMailboxByActivityIdsQueryHandler(
        IWorkflowLegacyMailboxQueryService mailboxQuery,
        ICurrentUserProvider currentUserProvider)
    {
        _mailboxQuery = mailboxQuery;
        _currentUserProvider = currentUserProvider;
    }

    public Task<LegacyMailboxListResult> Handle(GetMailboxByActivityIdsQuery request, CancellationToken cancellationToken)
    {
        var userId = _currentUserProvider.GetUserId()
            ?? throw new InvalidOperationException("User context is required.");

        return _mailboxQuery.ListByActivityIdsAsync(
            new LegacyMailboxByActivityRequest(request.WorkflowId, request.ActivityIds, userId),
            cancellationToken);
    }
}
