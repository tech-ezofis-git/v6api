using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Logging;
using SaaSApp.Workflow.Application.Contracts;
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
    private readonly IApAgentPythonJobClient _apAgentPythonJobClient;
    private readonly IApAgentPythonPipelineService _apAgentPythonPipeline;
    private readonly IApAgentJobProgressService _apAgentJobProgress;
    private readonly ILogger<StartWorkflowCommandHandler> _logger;

    public StartWorkflowCommandHandler(
        IWorkflowRepository repository,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext,
        ICurrentUserProvider currentUserProvider,
        IWorkflowTableCreator tableCreator,
        IWorkflowStartBootstrapService startBootstrap,
        IApAgentPythonJobClient apAgentPythonJobClient,
        IApAgentPythonPipelineService apAgentPythonPipeline,
        IApAgentJobProgressService apAgentJobProgress,
        ILogger<StartWorkflowCommandHandler> logger)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _tenantContext = tenantContext;
        _currentUserProvider = currentUserProvider;
        _tableCreator = tableCreator;
        _startBootstrap = startBootstrap;
        _apAgentPythonJobClient = apAgentPythonJobClient;
        _apAgentPythonPipeline = apAgentPythonPipeline;
        _apAgentJobProgress = apAgentJobProgress;
        _logger = logger;
    }

    public async Task<StartWorkflowCommandResult> Handle(StartWorkflowCommand request, CancellationToken cancellationToken)
    {
        var tenantId = _tenantContext.TenantId ?? throw new InvalidOperationException("Tenant context is required.");
        var userId = _currentUserProvider.GetUserId() ?? throw new InvalidOperationException("User context is required.");

        var workflow = await _repository.GetByIdWithStepsAsync(request.WorkflowId, cancellationToken);
        if (workflow == null || workflow.IsDeleted || workflow.TenantId != tenantId)
            throw new InvalidOperationException("Workflow not found.");

        if (workflow.Status != WorkflowStatus.Active)
            throw new InvalidOperationException("Only active workflows can be started.");

        var connectionString = _tenantContext.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await _tableCreator.EnsureWorkflowTablesForStartAsync(workflow.Id, connectionString, cancellationToken);
        await _apAgentJobProgress.EnsureProgressTableAsync(cancellationToken);

        var instance = WorkflowInstance.Create(tenantId, workflow.Id, workflow.Name, workflow.Version, userId, request.Context);
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
            var bootstrap = await _startBootstrap.RunAsync(
                new WorkflowStartBootstrapRequest(
                    workflow,
                    instance,
                    userId,
                    StartTransactionId: null,
                    request.EnvType,
                    attachmentStream,
                    request.Attachment?.FileName,
                    request.Attachment?.ContentType),
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

            if (request.TriggerApAgentPythonJob
                && !string.IsNullOrWhiteSpace(formDataJson))
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
                    "Enqueued AP Agent Python job {JobId} for instance {InstanceId} (multipart start with file).",
                    apAgentJobId,
                    instance.Id);
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
