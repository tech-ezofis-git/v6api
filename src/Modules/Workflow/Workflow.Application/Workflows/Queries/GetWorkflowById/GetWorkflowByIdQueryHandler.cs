using System.Text.Json;
using MediatR;
using SaaSApp.Workflow.Application.Connectors;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Application.Workflows;

namespace SaaSApp.Workflow.Application.Workflows.Queries.GetWorkflowById;

public sealed class GetWorkflowByIdQueryHandler : IRequestHandler<GetWorkflowByIdQuery, GetWorkflowByIdQueryResult?>
{
    private readonly IWorkflowRepository _repository;
    private readonly IWorkflowJsonStorageService _jsonStorage;
    private readonly IEmailIngestService _emailIngest;
    private readonly ICurrentUserProvider _currentUser;
    private readonly IWorkflowSecurityService _security;

    public GetWorkflowByIdQueryHandler(
        IWorkflowRepository repository,
        IWorkflowJsonStorageService jsonStorage,
        IEmailIngestService emailIngest,
        ICurrentUserProvider currentUser,
        IWorkflowSecurityService security)
    {
        _repository = repository;
        _jsonStorage = jsonStorage;
        _emailIngest = emailIngest;
        _currentUser = currentUser;
        _security = security;
    }

    public async Task<GetWorkflowByIdQueryResult?> Handle(GetWorkflowByIdQuery request, CancellationToken cancellationToken)
    {
        var workflow = await _repository.GetByIdWithStepsAsync(request.WorkflowId, cancellationToken);
        if (workflow == null || workflow.IsDeleted)
            return null;

        var userId = _currentUser.GetUserId();
        if (userId is Guid uid
            && !await _security.CanAccessWorkflowAsync(request.WorkflowId, uid, cancellationToken))
            return null;

        var steps = workflow.Steps
            .Select(s => new WorkflowStepItem(
                s.Id, s.Name, s.Description, s.StepType, s.Order, s.IsRequired,
                s.AssignedToUserId, s.AssignedToRole, s.ActivityId, s.StageType))
            .ToList();

        var workflowJson = await LoadWorkflowJsonElementAsync(request.WorkflowId, cancellationToken);
        workflowJson = WorkflowJsonDbSyncHelper.ApplyDbMetadata(
            workflowJson,
            workflow.Name,
            workflow.Description,
            workflow.Status);

        EmailIngestMailboxDto? mailbox = null;
        try
        {
            mailbox = await _emailIngest.GetMailboxByWorkflowIdAsync(request.WorkflowId, cancellationToken);
        }
        catch
        {
            // Schema may not exist yet on older tenants
        }

        return new GetWorkflowByIdQueryResult(
            workflow.Id,
            workflow.Name,
            workflow.Description,
            workflow.Status,
            workflow.TriggerType,
            workflow.TriggerConfig,
            workflow.Version,
            workflow.CreatedAtUtc,
            steps,
            workflowJson,
            workflow.RepositoryId,
            workflow.FormId,
            mailbox?.Id,
            mailbox?.ConnectorId,
            mailbox?.IsEnabled ?? false);
    }

    private async Task<JsonElement?> LoadWorkflowJsonElementAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        var json = await _jsonStorage.GetWorkflowJsonAsync(workflowId, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
