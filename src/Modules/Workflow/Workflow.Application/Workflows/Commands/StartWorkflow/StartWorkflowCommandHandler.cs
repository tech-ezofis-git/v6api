using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Logging;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Application.Workflows;
using SaaSApp.Workflow.Domain.Entities;
using SaaSApp.Workflow.Domain.Enums;

namespace SaaSApp.Workflow.Application.Workflows.Commands.StartWorkflow;

public sealed class StartWorkflowCommandHandler : IRequestHandler<StartWorkflowCommand, StartWorkflowCommandResult>
{
    private readonly IWorkflowRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUserProvider _currentUserProvider;
    private readonly IWorkflowTableCreator _tableCreator;
    private readonly IWorkflowStartBootstrapService _startBootstrap;
    private readonly IWorkflowTicketNumberService _ticketNumbers;
    private readonly IApAgentPythonJobClient _apAgentPythonJobClient;
    private readonly IFtlAgentJobClient _ftlAgentJobClient;
    private readonly IApAgentPythonPipelineService _apAgentPythonPipeline;
    private readonly IApAgentJobProgressService _apAgentJobProgress;
    private readonly IWorkflowSecurityService _security;
    private readonly ILogger<StartWorkflowCommandHandler> _logger;

    public StartWorkflowCommandHandler(
        IWorkflowRepository repository,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext,
        ICurrentUserProvider currentUserProvider,
        IWorkflowTableCreator tableCreator,
        IWorkflowStartBootstrapService startBootstrap,
        IWorkflowTicketNumberService ticketNumbers,
        IApAgentPythonJobClient apAgentPythonJobClient,
        IFtlAgentJobClient ftlAgentJobClient,
        IApAgentPythonPipelineService apAgentPythonPipeline,
        IApAgentJobProgressService apAgentJobProgress,
        IWorkflowSecurityService security,
        ILogger<StartWorkflowCommandHandler> logger)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _tenantContext = tenantContext;
        _currentUserProvider = currentUserProvider;
        _tableCreator = tableCreator;
        _startBootstrap = startBootstrap;
        _ticketNumbers = ticketNumbers;
        _apAgentPythonJobClient = apAgentPythonJobClient;
        _ftlAgentJobClient = ftlAgentJobClient;
        _apAgentPythonPipeline = apAgentPythonPipeline;
        _apAgentJobProgress = apAgentJobProgress;
        _security = security;
        _logger = logger;
    }

    public async Task<StartWorkflowCommandResult> Handle(StartWorkflowCommand request, CancellationToken cancellationToken)
    {
        var tenantId = _tenantContext.TenantId ?? throw new InvalidOperationException("Tenant context is required.");
        var userId = _currentUserProvider.GetUserId() ?? throw new InvalidOperationException("User context is required.");

        var workflow = await _repository.GetByIdWithStepsAsync(request.WorkflowId, cancellationToken);
        if (workflow == null || workflow.IsDeleted || workflow.TenantId != tenantId)
            throw new InvalidOperationException("Workflow not found.");

        if (!await _security.CanAccessWorkflowAsync(request.WorkflowId, userId, cancellationToken))
            throw new InvalidOperationException("You do not have access to this workflow.");

        if (workflow.Status != WorkflowStatus.Active)
            throw new InvalidOperationException("Only active workflows can be started.");

        var connectionString = _tenantContext.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await _tableCreator.EnsureWorkflowTablesForStartAsync(workflow.Id, connectionString, cancellationToken);
        await _apAgentJobProgress.EnsureProgressTableAsync(cancellationToken);

        var ticketNumber = await _ticketNumbers.AllocateNextAsync(workflow.Id, cancellationToken);

        var instance = WorkflowInstance.Create(
            tenantId,
            workflow.Id,
            workflow.Name,
            workflow.Version,
            userId,
            request.Context,
            referenceNumber: ticketNumber);
        instance.Start();

        foreach (var step in workflow.Steps.OrderBy(s => s.Order))
        {
            var stepInstance = WorkflowStepInstance.Create(
                instance.Id,
                step.Id,
                step.Name,
                step.StepType,
                step.Order,
                step.AssignedToUserId,
                step.AssignedToRole,
                step.ActivityId,
                step.StageType);
            instance.AddStepInstance(stepInstance);
        }

        var firstStep = instance.StepInstances.OrderBy(s => s.Order).FirstOrDefault();
        if (firstStep != null)
        {
            firstStep.Start();
            instance.SetCurrentStep(firstStep.Id);
        }

        if (workflow.Sla != null)
        {
            var instanceSla = WorkflowInstanceSla.Create(
                instance.Id,
                workflow.Sla.Priority,
                workflow.Sla.ResponseTimeMinutes,
                workflow.Sla.ResolutionTimeMinutes,
                workflow.Sla.EscalationTimeMinutes);
            instance.SetSla(instanceSla);
            if (firstStep != null)
                instanceSla.MarkResponseAchieved();
        }

        await _repository.AddInstanceAsync(instance, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        Stream? attachmentStream = null;
        if (request.Attachment is { Content.Length: > 0 } att)
            attachmentStream = new MemoryStream(att.Content);

        try
        {
            var orderedSteps = workflow.Steps.OrderBy(s => s.Order).ToList();
            var dedicatedApAgent = WorkflowStepTransitionHelper.TryResolveDedicatedApAgentStep(orderedSteps);

            var bootstrap = await _startBootstrap.RunAsync(
                new WorkflowStartBootstrapRequest(
                    workflow,
                    instance,
                    userId,
                    StartTransactionId: null,
                    request.EnvType,
                    attachmentStream,
                    request.Attachment?.FileName,
                    request.Attachment?.ContentType,
                    request.FormDataFields,
                    request.FormLineItemsJson,
                    request.StagedFiles),
                cancellationToken);

            _logger.LogInformation(
                "Started workflow {WorkflowId}: instance {WorkflowInstanceId}, first transaction {FirstTransactionId}, current transaction {CurrentTransactionId}",
                workflow.Id,
                instance.Id,
                bootstrap.FirstTransactionId,
                bootstrap.CurrentTransactionId);

            string? apAgentJobId = null;
            object? pythonInput = null;
            // Empty/omitted → null (agents full plan). Non-empty → subset for /chat.
            var skills = ApAgentStartPayloadJson.NormalizeSkills(request.Skills);
            var formDataJson = ApAgentStartPayloadJson.MergeSkillsIntoPayloadJson(
                bootstrap.FormDataJson,
                skills);
            var startPayload = ApAgentStartPayloadJson.MergeSkillsIntoStartPayload(
                bootstrap.StartPayload,
                skills);

            // Only enqueue Hangfire when the caller opted in (file attached / email ingest).
            // Do not queue for every ticket that merely has an AP_AGENT step in the designer.
            if (request.TriggerApAgentPythonJob
                && dedicatedApAgent != null
                && !string.IsNullOrWhiteSpace(formDataJson))
            {
                var existingJobId = await _apAgentJobProgress.GetLatestActiveJobIdForInstanceAsync(
                    instance.Id,
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(existingJobId))
                {
                    apAgentJobId = existingJobId;
                    _logger.LogInformation(
                        "AP Agent job {JobId} already active for instance {InstanceId}; skipping enqueue.",
                        apAgentJobId,
                        instance.Id);
                }
                else
                {
                    var jobArgs = new ApAgentPythonJobArgs(
                        tenantId,
                        userId,
                        request.WorkflowId,
                        instance.Id,
                        formDataJson,
                        Skills: skills);

                    apAgentJobId = await _apAgentPythonJobClient.EnqueueAsync(jobArgs, cancellationToken);

                    var chatJson = _apAgentPythonPipeline.BuildChatRequestJson(jobArgs, apAgentJobId);
                    pythonInput = JsonSerializer.Deserialize<JsonElement>(chatJson);

                    _logger.LogInformation(
                        "Enqueued AP Agent Python job {JobId} for instance {InstanceId}.",
                        apAgentJobId,
                        instance.Id);
                }
            }
            else if (dedicatedApAgent != null && !request.TriggerApAgentPythonJob)
            {
                _logger.LogInformation(
                    "Skipping AP Agent Hangfire enqueue for instance {InstanceId} (TriggerApAgentPythonJob=false).",
                    instance.Id);
            }
            else if (dedicatedApAgent == null && request.Attachment is { Content.Length: > 0 })
            {
                var qualifyStep = orderedSteps.FirstOrDefault(FtlAgentStepDetector.IsQualifyAgent);
                if (qualifyStep != null)
                {
                    try
                    {
                        var qualifyActivityId = !string.IsNullOrWhiteSpace(qualifyStep.ActivityId)
                            ? qualifyStep.ActivityId!
                            : qualifyStep.Id.ToString("D");
                        var ftlJobId = await _ftlAgentJobClient.EnqueueAsync(
                            new FtlAgentJobArgs(
                                tenantId,
                                userId,
                                request.WorkflowId,
                                instance.Id,
                                qualifyActivityId,
                                FtlAgentStepDetector.Qualifier,
                                workflow.RepositoryId,
                                workflow.FormId),
                            cancellationToken);
                        apAgentJobId = ftlJobId;
                        pythonInput = JsonSerializer.SerializeToElement(new
                        {
                            session_id = ftlJobId,
                            intent = "ftl_qualifier",
                            file = request.Attachment?.FileName,
                            workflowId = request.WorkflowId,
                            instanceId = instance.Id,
                            repositoryId = workflow.RepositoryId,
                            formId = workflow.FormId,
                            apAgentJobId = ftlJobId
                        });
                        _logger.LogInformation(
                            "Enqueued FTL qualifier job {JobId} for instance {InstanceId}. Start returns the qualifier input; move-next runs after the agent output.",
                            ftlJobId,
                            instance.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "FTL qualifier enqueue failed for instance {InstanceId}.", instance.Id);
                    }
                }
            }

            return new StartWorkflowCommandResult(
                instance.Id,
                bootstrap.FirstTransactionId,
                bootstrap.CurrentTransactionId,
                bootstrap.FormEntryId,
                bootstrap.ApAgentStepInstanceId,
                formDataJson,
                bootstrap.FormDataBlobPath,
                startPayload,
                apAgentJobId,
                skills,
                pythonInput);
        }
        finally
        {
            if (attachmentStream != null)
                await attachmentStream.DisposeAsync();
        }
    }
}
