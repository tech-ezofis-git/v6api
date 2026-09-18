using MediatR;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Application.Workflows.Queries.ListWorkflows;

public sealed class ListWorkflowsQueryHandler : IRequestHandler<ListWorkflowsQuery, ListWorkflowsQueryResult>
{
    private readonly IWorkflowRepository _repository;
    private readonly IUserEmailLookup _userEmails;
    private readonly ICurrentUserProvider _currentUser;
    private readonly IWorkflowSecurityService _security;

    public ListWorkflowsQueryHandler(
        IWorkflowRepository repository,
        IUserEmailLookup userEmails,
        ICurrentUserProvider currentUser,
        IWorkflowSecurityService security)
    {
        _repository = repository;
        _userEmails = userEmails;
        _currentUser = currentUser;
        _security = security;
    }

    public async Task<ListWorkflowsQueryResult> Handle(ListWorkflowsQuery request, CancellationToken cancellationToken)
    {
        var workflows = await _repository.ListAsync(cancellationToken);
        var userId = _currentUser.GetUserId();
        if (userId is Guid uid
            && !await _security.UserSeesAllWorkflowsAsync(uid, cancellationToken))
        {
            var allowed = await _security.GetAccessibleWorkflowIdsAsync(uid, cancellationToken);
            workflows = workflows.Where(w => allowed.Contains(w.Id)).ToList();
        }
        else if (userId is null)
        {
            return new ListWorkflowsQueryResult(Array.Empty<ListWorkflowsItem>());
        }

        var userIds = workflows
            .Select(w => w.CreatedBy)
            .Concat(workflows.Where(w => w.ModifiedBy.HasValue).Select(w => w.ModifiedBy!.Value));
        var emails = await _userEmails.GetEmailsAsync(userIds, cancellationToken);

        var items = workflows.Select(w =>
        {
            emails.TryGetValue(w.CreatedBy, out var createdEmail);
            var modifiedEmail = createdEmail;
            if (w.ModifiedBy is Guid modifiedId && emails.TryGetValue(modifiedId, out var modEmail))
                modifiedEmail = modEmail;

            return new ListWorkflowsItem(
                w.Id,
                w.Name,
                w.Description,
                w.Status,
                w.TriggerType,
                w.Version,
                w.CreatedAtUtc,
                w.CreatedBy,
                w.ModifiedBy,
                w.ModifiedAtUtc,
                createdEmail,
                modifiedEmail);
        }).ToList();

        return new ListWorkflowsQueryResult(items);
    }
}
