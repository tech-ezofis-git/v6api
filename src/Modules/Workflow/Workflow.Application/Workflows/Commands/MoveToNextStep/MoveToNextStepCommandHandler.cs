using MediatR;
using Microsoft.Extensions.Logging;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Application.Workflows;
using SaaSApp.Workflow.Domain.Entities;
using SaaSApp.Workflow.Domain.Enums;

namespace SaaSApp.Workflow.Application.Workflows.Commands.MoveToNextStep;

public sealed class MoveToNextStepCommandHandler : IRequestHandler<MoveToNextStepCommand, MoveToNextStepCommandResult>
{
    private readonly IWorkflowRepository _repository;
    private readonly IDynamicTableRepository _dynamicTableRepository;
    private readonly IWorkflowLegacyTransactionSyncService _legacyTransactionSync;
    private readonly IWorkflowLegacyMailboxSyncService _mailboxSync;
    private readonly IWorkflowApAgentMoveNextService _apAgentMoveNext;
    private readonly IWorkflowEzfbFormDataLoader _ezfbFormDataLoader;
    private readonly IWorkflowPdfGenerationService _pdfGeneration;
    private readonly IWorkflowMoveNotificationService _moveNotifications;
    private readonly IHanaCloudPurchaseOrderService _hanaPurchaseOrders;
    private readonly IWorkflowJsonStorageService _workflowJsonStorage;
    private readonly IWorkflowSecurityService _workflowSecurity;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUserProvider _currentUserProvider;
    private readonly IFtlAgentJobClient _ftlAgentJobClient;
    private readonly ILogger<MoveToNextStepCommandHandler> _logger;

    public MoveToNextStepCommandHandler(
        IWorkflowRepository repository,
        IDynamicTableRepository dynamicTableRepository,
        IWorkflowLegacyTransactionSyncService legacyTransactionSync,
        IWorkflowLegacyMailboxSyncService mailboxSync,
        IWorkflowApAgentMoveNextService apAgentMoveNext,
        IWorkflowEzfbFormDataLoader ezfbFormDataLoader,
        IWorkflowPdfGenerationService pdfGeneration,
        IWorkflowMoveNotificationService moveNotifications,
        IHanaCloudPurchaseOrderService hanaPurchaseOrders,
        IWorkflowJsonStorageService workflowJsonStorage,
        IWorkflowSecurityService workflowSecurity,
        IUnitOfWork unitOfWork,
        ICurrentUserProvider currentUserProvider,
        IFtlAgentJobClient ftlAgentJobClient,
        ILogger<MoveToNextStepCommandHandler> logger)
    {
        _repository = repository;
        _dynamicTableRepository = dynamicTableRepository;
        _legacyTransactionSync = legacyTransactionSync;
        _mailboxSync = mailboxSync;
        _apAgentMoveNext = apAgentMoveNext;
        _ezfbFormDataLoader = ezfbFormDataLoader;
        _pdfGeneration = pdfGeneration;
        _moveNotifications = moveNotifications;
        _hanaPurchaseOrders = hanaPurchaseOrders;
        _workflowJsonStorage = workflowJsonStorage;
        _workflowSecurity = workflowSecurity;
        _unitOfWork = unitOfWork;
        _currentUserProvider = currentUserProvider;
        _ftlAgentJobClient = ftlAgentJobClient;
        _logger = logger;
    }

    public async Task<MoveToNextStepCommandResult> Handle(MoveToNextStepCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ActivityId))
            throw new ArgumentException("activityId is required.");

        var userId = _currentUserProvider.GetUserId() ?? throw new InvalidOperationException("User context is required.");

        var instance = await _repository.GetInstanceByIdAsync(request.WorkflowInstanceId, cancellationToken);
        if (instance == null)
            throw new InvalidOperationException("Workflow instance not found.");

        if (instance.Status == WorkflowInstanceStatus.Completed)
        {
            var legacyFlowStatus = await _legacyTransactionSync.GetLegacyProcessFlowStatusAsync(
                instance.WorkflowId,
                instance.Id,
                instance.ReferenceNumber,
                cancellationToken);

            // Allow move-next when legacy process is still running (FlowStatus = 0), e.g. after manual reset for testing.
            if (legacyFlowStatus == 0)
            {
                instance.Reopen(userId);
                await _repository.UpdateInstanceAsync(instance, cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }
            else
                throw new InvalidOperationException("Workflow is already completed.");
        }

        if (instance.Status == WorkflowInstanceStatus.Cancelled)
            throw new InvalidOperationException("Workflow is cancelled.");

        var lineItemsJson = request.FormLineItemsJson;
        var formId = !string.IsNullOrWhiteSpace(request.FormId)
            ? request.FormId
            : request.ApAgent?.FormId;
        var formEntryId = ResolveFormEntryId(request);

        if ((formEntryId is null || formEntryId == Guid.Empty)
            && HasUserFormData(request))
        {
            var fromProcess = await _mailboxSync.TryGetProcessFormIdentityAsync(
                instance.WorkflowId,
                instance.Id,
                cancellationToken);
            if (fromProcess != null)
            {
                if (string.IsNullOrWhiteSpace(formId) && !string.IsNullOrWhiteSpace(fromProcess.Value.FormId))
                    formId = fromProcess.Value.FormId;
                if (fromProcess.Value.FormEntryId is { } processEntry && processEntry != Guid.Empty)
                    formEntryId = processEntry;
            }
        }

        if (!string.IsNullOrWhiteSpace(formId)
            && formEntryId is { } reqEntryId && reqEntryId != Guid.Empty
            && HasUserFormData(request))
        {
            await _apAgentMoveNext.ApplyFormDataToEzfbAsync(
                formId,
                reqEntryId,
                request.FormDataFields ?? new Dictionary<string, string>(),
                lineItemsJson,
                cancellationToken);
        }

        var workflow = await _repository.GetByIdWithStepsAsync(instance.WorkflowId, cancellationToken);
        if (workflow == null || workflow.IsDeleted)
            throw new InvalidOperationException("Workflow not found.");

        var orderedSteps = workflow.Steps.OrderBy(s => s.Order).ToList();
        if (orderedSteps.Count == 0)
            throw new InvalidOperationException("Workflow has no steps defined.");

        var targetDefinitionStep = ResolveStepByActivityId(orderedSteps, request.ActivityId);
        if (targetDefinitionStep == null)
            throw new InvalidOperationException($"No workflow step found for activityId '{request.ActivityId.Trim()}'.");

        var isApAgentMoveNext = ApAgentStepDetector.IsApAgentMoveNext(targetDefinitionStep, request.ActivityId);
        var isApAgentStep = WorkflowStepTransitionHelper.IsApAgentStep(targetDefinitionStep) || isApAgentMoveNext;
        var routesByAction = WorkflowStepActionsHelper.HasMatchingAction(targetDefinitionStep, request.Review)
            || WorkflowStepTransitionHelper.IsApproveReview(request.Review)
            || (isApAgentStep && WorkflowStepTransitionHelper.IsApAgentDecisionReview(request.Review));

        if (string.IsNullOrWhiteSpace(formId))
            formId = request.ApAgent?.FormId;
        if (formEntryId is null || formEntryId == Guid.Empty)
            formEntryId = ResolveFormEntryId(request);

        if (isApAgentStep && !string.IsNullOrWhiteSpace(request.Review) && !routesByAction)
        {
            if (request.ApAgent != null)
            {
                await _apAgentMoveNext.SaveAgentValidationAsync(
                    instance.WorkflowId,
                    instance.Id,
                    targetDefinitionStep,
                    userId,
                    request.ApAgent,
                    legacyTransactionId: null,
                    cancellationToken);
            }

            // Still push po_row → form even when review does not advance the workflow.
            if (!string.IsNullOrWhiteSpace(formId) && formEntryId is { } entry && entry != Guid.Empty)
            {
                await _apAgentMoveNext.ApplyPoRowFromStoredAgentValidationAsync(
                    instance.WorkflowId,
                    instance.Id,
                    formId!,
                    formEntryId.Value,
                    request.ApAgent?.AiAgentResponseJson,
                    cancellationToken);
            }

            await NotifyMoveAsync(
                workflow,
                instance,
                request.Review,
                targetDefinitionStep,
                nextStep: null,
                submittedModifiedByUserId: userId,
                receivedCreatedByUserId: userId,
                cancellationToken);

            return new MoveToNextStepCommandResult(
                true,
                "AP agent review recorded; workflow not advanced (review is not Approve).",
                null,
                null,
                null,
                WorkflowCompleted: false);
        }

        if (isApAgentStep && routesByAction)
        {
            var apStepInstance = WorkflowStepTransitionHelper.FindStepInstance(instance, targetDefinitionStep.Id);
            if (apStepInstance == null)
                throw new InvalidOperationException(
                    $"No workflow step instance for AP agent activity '{request.ActivityId.Trim()}'.");

            if (apStepInstance.Status == StepInstanceStatus.Completed)
            {
                var nextAfterAp = orderedSteps
                    .Where(s => s.Order > targetDefinitionStep.Order)
                    .OrderBy(s => s.Order)
                    .FirstOrDefault();
                var nextInstance = nextAfterAp != null
                    ? WorkflowStepTransitionHelper.FindStepInstance(instance, nextAfterAp.Id)
                    : null;

                await NotifyMoveAsync(
                    workflow,
                    instance,
                    request.Review,
                    targetDefinitionStep,
                    nextAfterAp,
                    submittedModifiedByUserId: userId,
                    receivedCreatedByUserId: userId,
                    cancellationToken);

                return new MoveToNextStepCommandResult(
                    true,
                    "AP agent step already completed.",
                    nextInstance?.Id,
                    nextAfterAp?.Name,
                    nextAfterAp?.Order,
                    instance.Status == WorkflowInstanceStatus.Completed,
                    instance.Id);
            }

            if (apStepInstance.Status is not (StepInstanceStatus.InProgress or StepInstanceStatus.WaitingForApproval))
                throw new InvalidOperationException(
                    $"AP agent step is not active (status: {apStepInstance.Status}).");
        }

        // Apply po_row from request AIAGENTResponse (preferred) or stored validation → ezfb BEFORE inbox snapshot.
        var appliedPoRowToEzfb = false;
        if (isApAgentMoveNext
            && !string.IsNullOrWhiteSpace(formId)
            && formEntryId is { } poEntry && poEntry != Guid.Empty)
        {
            await _apAgentMoveNext.ApplyPoRowFromStoredAgentValidationAsync(
                instance.WorkflowId,
                instance.Id,
                formId!,
                formEntryId.Value,
                request.ApAgent?.AiAgentResponseJson,
                cancellationToken);
            // Always prefer ezfb for inbox after AP-agent move-next (full row incl. PO Amount/Date/Line Item).
            appliedPoRowToEzfb = true;
        }

        // After po_row → ezfb, prefer ezfb for inbox (client formData usually omits PO Amount/Date/Line Item).
        var mailboxForm = await BuildMailboxFormSnapshotAsync(
            request,
            formId,
            formEntryId,
            cancellationToken,
            preferEzfb: appliedPoRowToEzfb);

        // Forward to a user without workflow access: grant WorkflowUsers + WorkflowSecurity first.
        if (IsForwardReview(request.Review)
            && request.ActivityUserId is Guid forwardToUserId
            && forwardToUserId != Guid.Empty)
        {
            await _workflowSecurity.EnsureUserWorkflowAccessAsync(
                instance.WorkflowId,
                forwardToUserId,
                userId,
                cancellationToken);
        }

        var legacySync = await _legacyTransactionSync.SyncTransactionByActivityIdAsync(
            instance.WorkflowId,
            instance.Id,
            instance.ReferenceNumber,
            targetDefinitionStep,
            orderedSteps,
            request.ActivityId.Trim(),
            userId,
            request.ActivityUserId,
            request.Review,
            mailboxForm,
            cancellationToken);

        WorkflowStep? nextDefinitionStep = null;
        var workflowCompleted = legacySync.WorkflowCompleted;
        WorkflowPdfGenerationResult? generatedPdf = null;

        if (legacySync.Status == LegacyTransactionSyncStatus.Forwarded)
        {
            // Reassign only — do not complete the step or route to END.
            if (legacySync.NextActivityUserId is Guid forwardedTo && forwardedTo != Guid.Empty)
                instance.Reassign(forwardedTo);
            await _repository.UpdateInstanceAsync(instance, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        else if (legacySync.Status == LegacyTransactionSyncStatus.ReviewUpdated)
        {
            // Generate PDF when leaving this step — including when this move completes the workflow.
            if (!FtlAgentStepDetector.IsDocumentGenerateAgent(targetDefinitionStep))
            {
                generatedPdf = await _pdfGeneration.TryGenerateOnStepCompleteAsync(
                    workflow,
                    instance,
                    targetDefinitionStep,
                    formId,
                    formEntryId,
                    userId,
                    legacySync.CurrentTransactionId,
                    cancellationToken,
                    submittedFormDataJson: request.SubmittedFormDataJson);
            }

            // PDF is archived after the first mailbox sync — re-sync so itemId / FILE binds appear.
            if (generatedPdf != null)
            {
                if (legacySync.CurrentTransactionId is > 0)
                {
                    await _mailboxSync.SyncTransactionRowAsync(
                        instance.WorkflowId,
                        legacySync.CurrentTransactionId.Value,
                        cancellationToken);
                }

                if (legacySync.NextTransactionId is > 0)
                {
                    await _mailboxSync.SyncTransactionRowAsync(
                        instance.WorkflowId,
                        legacySync.NextTransactionId.Value,
                        cancellationToken);
                }

                if (workflowCompleted)
                {
                    await _mailboxSync.SyncInstanceEndTransactionsAsync(
                        instance.WorkflowId,
                        instance.Id,
                        cancellationToken);
                }
            }

            nextDefinitionStep = WorkflowStepActionsHelper.ResolveNextStepByReview(
                    targetDefinitionStep, request.Review, orderedSteps)
                ?? orderedSteps
                    .Where(s => s.Order > targetDefinitionStep.Order)
                    .OrderBy(s => s.Order)
                    .FirstOrDefault();

            WorkflowStepTransitionHelper.CompleteStepInstance(instance, targetDefinitionStep.Id, userId);
            if (nextDefinitionStep != null && !workflowCompleted)
            {
                WorkflowStepTransitionHelper.StartStepInstance(instance, nextDefinitionStep.Id);
                if (legacySync.NextActivityUserId is Guid nextAssignee && nextAssignee != Guid.Empty)
                    instance.Reassign(nextAssignee);
            }
            else
            {
                instance.Complete(userId);
                workflowCompleted = true;
            }

            await _repository.UpdateInstanceAsync(instance, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            if (isApAgentStep && routesByAction && request.ApAgent != null)
            {
                await _apAgentMoveNext.AfterApAgentApproveAsync(
                    instance.WorkflowId,
                    instance.Id,
                    targetDefinitionStep,
                    instance.TenantId,
                    userId,
                    request.ApAgent,
                    legacySync.CurrentTransactionId,
                    cancellationToken);
            }
        }
        else if (workflowCompleted)
        {
            WorkflowStepTransitionHelper.CompleteStepInstance(instance, targetDefinitionStep.Id, userId);
            instance.Complete(userId);
            await _repository.UpdateInstanceAsync(instance, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var message = legacySync.Status switch
        {
            LegacyTransactionSyncStatus.StepInserted when workflowCompleted =>
                "END stage inserted; workflow completed.",
            LegacyTransactionSyncStatus.StepInserted => $"Step {targetDefinitionStep.Order} inserted in transaction table.",
            LegacyTransactionSyncStatus.StepAlreadyThere => "Step is already there.",
            LegacyTransactionSyncStatus.ReviewAlreadyUpdated => "Review is already updated.",
            LegacyTransactionSyncStatus.Forwarded =>
                "Forwarded to user; step remains open.",
            LegacyTransactionSyncStatus.ReviewUpdated when workflowCompleted =>
                "Review updated; workflow completed.",
            LegacyTransactionSyncStatus.ReviewUpdated when legacySync.NextTransactionId.HasValue =>
                "Review updated and next step inserted.",
            LegacyTransactionSyncStatus.ReviewUpdated =>
                "Review updated.",
            _ => "OK"
        };

        // Skip v5 proceed echoes like "2MH_xxx: Matched" — those belong on review, not comments.
        if (!string.IsNullOrWhiteSpace(request.Comments)
            && !WorkflowCommentHelper.IsAutomaticRuleProceedComment(request.Comments))
        {
            var stepInstanceId = WorkflowStepTransitionHelper.FindStepInstance(instance, targetDefinitionStep.Id)?.Id;
            await _dynamicTableRepository.AddCommentAsync(
                instance.WorkflowId,
                instance.Id,
                request.Comments.Trim(),
                userId,
                stepInstanceId,
                cancellationToken: cancellationToken);
        }

        var isCompleted = workflowCompleted || instance.Status == WorkflowInstanceStatus.Completed;
        int? legacyNextTransactionId = isCompleted ? 0 : legacySync.NextTransactionId;
        Guid? legacyNextTransactionGuid = isCompleted ? null : legacySync.NextTransactionGuid;

        // Refresh mailbox formData after any later form writes (user formData / PDF FILE binds / comments).
        await PropagateMailboxFormDataAsync(
            request,
            instance,
            formId,
            formEntryId,
            preferEzfb: appliedPoRowToEzfb || generatedPdf != null,
            cancellationToken);

        // On completion, do not surface END/"Workflow Success" as a move-to next step.
        var notifyNextStep = isCompleted ? null : nextDefinitionStep;
        var displayStep = isCompleted ? targetDefinitionStep : (nextDefinitionStep ?? targetDefinitionStep);
        var displayStepInstance = WorkflowStepTransitionHelper.FindStepInstance(instance, displayStep.Id);
        var resultMessage = isCompleted ? "Workflow completed." : message;

        await NotifyMoveAsync(
            workflow,
            instance,
            request.Review,
            targetDefinitionStep,
            notifyNextStep,
            submittedModifiedByUserId: userId,
            receivedCreatedByUserId: isCompleted ? userId : (legacySync.NextCreatedByUserId ?? userId),
            cancellationToken,
            currentTransactionId: legacySync.CurrentTransactionId,
            nextTransactionId: isCompleted ? null : legacySync.NextTransactionId);

        // HANA invoices only: Paid when review is Paid, or when moving to / completing a Paid-named step.
        // Uses apAgent.connectorId (or PoMaster.masterConnectorId) → PO_INVOICE_MATCH.
        if (IsPaidReview(request.Review)
            || IsPaidStep(nextDefinitionStep)
            || (isCompleted && IsPaidStep(targetDefinitionStep)))
        {
            try
            {
                var workflowJson = await _workflowJsonStorage.GetWorkflowJsonAsync(
                    instance.WorkflowId,
                    cancellationToken);
                if (TryResolveHanaConnectorId(workflowJson, out var hanaConnectorId))
                {
                    await _hanaPurchaseOrders.TryMarkPaidByInstanceIdAsync(
                        hanaConnectorId,
                        instance.Id,
                        cancellationToken);
                }
            }
            catch
            {
                // Leave workflow move successful; HANA mark can be retried via match API.
            }
        }

        if (!isCompleted && nextDefinitionStep != null)
            await TryEnqueueFtlAgentAsync(instance, workflow, nextDefinitionStep, userId, formId, cancellationToken);

        return new MoveToNextStepCommandResult(
            true,
            resultMessage,
            isCompleted ? null : displayStepInstance?.Id,
            isCompleted ? null : displayStep.Name,
            isCompleted ? null : displayStep.Order,
            isCompleted,
            legacySync.WorkflowInstanceId,
            legacySync.CurrentTransactionId,
            legacyNextTransactionId,
            legacyNextTransactionGuid,
            GeneratedPdfAttachmentId: generatedPdf?.AttachmentId,
            GeneratedPdfFileName: generatedPdf?.FileName,
            GeneratedPdfInput: generatedPdf?.PythonRequest);
    }

    private async Task TryEnqueueFtlAgentAsync(
        WorkflowInstance instance,
        Domain.Entities.Workflow workflow,
        WorkflowStep nextStep,
        Guid userId,
        string? formId,
        CancellationToken cancellationToken)
    {
        var mode = FtlAgentStepDetector.HangfireMode(nextStep);
        if (mode == null || mode == FtlAgentStepDetector.Qualifier)
            return;

        var activityId = !string.IsNullOrWhiteSpace(nextStep.ActivityId)
            ? nextStep.ActivityId!
            : nextStep.Id.ToString("D");
        try
        {
            var jobId = await _ftlAgentJobClient.EnqueueAsync(
                new FtlAgentJobArgs(
                    instance.TenantId,
                    userId,
                    instance.WorkflowId,
                    instance.Id,
                    activityId,
                    mode,
                    workflow.RepositoryId,
                    formId ?? workflow.FormId),
                cancellationToken);
            _logger.LogInformation(
                "Enqueued FTL {Mode} job {JobId} for instance {InstanceId} activity {ActivityId}.",
                mode,
                jobId,
                instance.Id,
                activityId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "FTL {Mode} enqueue failed for instance {InstanceId}. The ticket was already moved.",
                mode,
                instance.Id);
        }
    }

    private Task NotifyMoveAsync(
        Domain.Entities.Workflow workflow,
        WorkflowInstance instance,
        string? review,
        WorkflowStep currentStep,
        WorkflowStep? nextStep,
        Guid submittedModifiedByUserId,
        Guid? receivedCreatedByUserId,
        CancellationToken cancellationToken,
        int? currentTransactionId = null,
        int? nextTransactionId = null)
        => _moveNotifications.TryInsertMoveNotificationsAsync(
            new WorkflowMoveNotificationContext(
                instance.WorkflowId,
                instance.Id,
                workflow.Name,
                review,
                currentStep.Name,
                currentStep.StageType,
                nextStep?.Name,
                nextStep?.StageType,
                submittedModifiedByUserId,
                receivedCreatedByUserId,
                instance.ReferenceNumber,
                currentTransactionId,
                nextTransactionId),
            cancellationToken);

    internal static WorkflowStep? ResolveStepByActivityId(IReadOnlyList<WorkflowStep> orderedSteps, string activityId)
    {
        var id = activityId.Trim();
        return orderedSteps.FirstOrDefault(s =>
            (!string.IsNullOrWhiteSpace(s.ActivityId) &&
             string.Equals(s.ActivityId, id, StringComparison.OrdinalIgnoreCase))
            || string.Equals(s.Id.ToString("D"), id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.Id.ToString("N"), id, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPaidStep(WorkflowStep? step) =>
        !string.IsNullOrWhiteSpace(step?.Name)
        && step!.Name.Contains("paid", StringComparison.OrdinalIgnoreCase);

    private static bool IsPaidReview(string? review) =>
        !string.IsNullOrWhiteSpace(review)
        && review.Trim().Contains("paid", StringComparison.OrdinalIgnoreCase);

    private static bool IsForwardReview(string? review) =>
        string.Equals(review?.Trim(), "Forward", StringComparison.OrdinalIgnoreCase);

    private static bool TryResolveHanaConnectorId(string? workflowJson, out Guid connectorId)
    {
        if (WorkflowApAgentJson.TryReadConnectorId(workflowJson, out connectorId))
            return true;

        if (WorkflowPoMasterJson.TryRead(workflowJson, out _, out var masterConnectorId, out _)
            && masterConnectorId is { } mid
            && mid != Guid.Empty)
        {
            connectorId = mid;
            return true;
        }

        connectorId = Guid.Empty;
        return false;
    }

    private static Guid? ResolveFormEntryId(MoveToNextStepCommand request)
    {
        if (request.FormEntryId is { } fe && fe != Guid.Empty)
            return fe;
        if (request.ApAgent?.FormEntryId is { } afe && afe != Guid.Empty)
            return afe;
        if (request.ApAgent?.RepositoryItemId is { } itemId && itemId != Guid.Empty)
            return itemId;
        return null;
    }

    private static bool HasUserFormData(MoveToNextStepCommand request) =>
        !string.IsNullOrWhiteSpace(request.SubmittedFormDataJson)
        || request.FormDataFields is { Count: > 0 }
        || !string.IsNullOrWhiteSpace(request.FormLineItemsJson);

    private async Task<MailboxFormSnapshot?> BuildMailboxFormSnapshotAsync(
        MoveToNextStepCommand request,
        string? formId,
        Guid? formEntryId,
        CancellationToken cancellationToken,
        bool preferEzfb = false)
    {
        var resolvedFormId = !string.IsNullOrWhiteSpace(formId) ? formId : request.FormId;
        var resolvedEntryId = formEntryId is { } e && e != Guid.Empty
            ? formEntryId
            : ResolveFormEntryId(request);

        string? formDataJson = null;

        // Inbox/sent keep jsonId keys from the client payload. Ezfb stores Label/column names.
        if (HasUserFormData(request))
        {
            formDataJson = MoveToNextStepFormDataComposer.ForMailbox(request.SubmittedFormDataJson)
                ?? MoveToNextStepFormDataComposer.FromParsedFields(
                    request.FormDataFields,
                    request.FormLineItemsJson);
        }

        if (string.IsNullOrWhiteSpace(formDataJson)
            && !string.IsNullOrWhiteSpace(resolvedFormId)
            && resolvedEntryId is { } resolved && resolved != Guid.Empty)
        {
            formDataJson = await _ezfbFormDataLoader.LoadFormDataJsonAsync(
                resolvedFormId,
                resolvedEntryId.Value,
                cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(formDataJson))
            return null;

        return new MailboxFormSnapshot(resolvedFormId, resolvedEntryId, formDataJson);
    }

    private async Task PropagateMailboxFormDataAsync(
        MoveToNextStepCommand request,
        WorkflowInstance instance,
        string? formId,
        Guid? formEntryId,
        bool preferEzfb,
        CancellationToken cancellationToken)
    {
        var snapshot = await BuildMailboxFormSnapshotAsync(
            request,
            formId,
            formEntryId,
            cancellationToken,
            preferEzfb);
        if (snapshot == null)
            return;

        await _mailboxSync.PropagateInstanceFormDataAsync(
            instance.WorkflowId,
            instance.Id,
            snapshot,
            cancellationToken);
    }

}
