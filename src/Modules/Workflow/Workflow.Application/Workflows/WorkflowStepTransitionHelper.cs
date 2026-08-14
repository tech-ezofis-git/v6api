using SaaSApp.Workflow.Domain.Entities;
using SaaSApp.Workflow.Domain.Enums;

namespace SaaSApp.Workflow.Application.Workflows;

/// <summary>Shared step-instance transitions used by move-next and start bootstrap.</summary>
public static class WorkflowStepTransitionHelper
{
    public const string StartProceedReview = "Submit";

    /// <summary>AP Agent step only when explicitly defined (not Order=2 fallback).</summary>
    public static WorkflowStep? TryResolveDedicatedApAgentStep(IReadOnlyList<WorkflowStep> orderedSteps) =>
        orderedSteps.FirstOrDefault(IsApAgentStep)
        ?? orderedSteps.FirstOrDefault(s =>
            string.Equals(s.Name, "Ap Agent", StringComparison.OrdinalIgnoreCase));

    public static WorkflowStep? ResolveApAgentStep(IReadOnlyList<WorkflowStep> orderedSteps)
    {
        var dedicated = TryResolveDedicatedApAgentStep(orderedSteps);
        if (dedicated != null)
            return dedicated;

        return orderedSteps.FirstOrDefault(s => s.Order == 2);
    }

    public static bool IsApAgentStep(WorkflowStep step) =>
        string.Equals(step.StageType, "AP_AGENT", StringComparison.OrdinalIgnoreCase)
        || string.Equals(step.Name, "Ap Agent", StringComparison.OrdinalIgnoreCase);

    public static bool IsApproveReview(string? review) =>
        string.Equals(review?.Trim(), "Approve", StringComparison.OrdinalIgnoreCase);

    public static WorkflowStepInstance? FindStepInstance(WorkflowInstance instance, Guid workflowStepId) =>
        instance.StepInstances.FirstOrDefault(s => s.WorkflowStepId == workflowStepId);

    public static void CompleteStepInstance(WorkflowInstance instance, Guid workflowStepId, Guid userId)
    {
        var si = FindStepInstance(instance, workflowStepId);
        if (si != null && si.Status is StepInstanceStatus.InProgress or StepInstanceStatus.WaitingForApproval)
            si.Complete(userId);
    }

    public static void StartStepInstance(WorkflowInstance instance, Guid workflowStepId)
    {
        var si = FindStepInstance(instance, workflowStepId);
        if (si != null && si.Status == StepInstanceStatus.Pending)
        {
            si.Start();
            instance.SetCurrentStep(si.Id);
        }
    }
}
