using System.Text.Json;
using System.Text.Json.Serialization;
using SaaSApp.Workflow.Domain.Entities;

namespace SaaSApp.Workflow.Application.Workflows;

/// <summary>Outgoing designer Rules stored on each workflow step (ProceedAction → ToBlockId).</summary>
public sealed record WorkflowStepActionDto(
    [property: JsonPropertyName("Id")] string Id,
    [property: JsonPropertyName("ProceedAction")] string? ProceedAction,
    [property: JsonPropertyName("ToBlockId")] string ToBlockId);

public static class WorkflowStepActionsHelper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static IReadOnlyList<WorkflowStepActionDto> ParseActions(string? actionsJson)
    {
        if (string.IsNullOrWhiteSpace(actionsJson))
            return Array.Empty<WorkflowStepActionDto>();

        try
        {
            var parsed = JsonSerializer.Deserialize<List<WorkflowStepActionDto>>(actionsJson, JsonOptions);
            return parsed ?? new List<WorkflowStepActionDto>();
        }
        catch
        {
            return Array.Empty<WorkflowStepActionDto>();
        }
    }

    public static string? SerializeActions(IEnumerable<WorkflowStepActionDto> actions)
    {
        var list = actions.ToList();
        return list.Count == 0 ? null : JsonSerializer.Serialize(list, JsonOptions);
    }

    public static bool HasMatchingAction(WorkflowStep currentStep, string? review)
    {
        if (string.IsNullOrWhiteSpace(review))
            return false;

        return ParseActions(currentStep.ActionsJson)
            .Any(a => ReviewMatchesProceedAction(review, a.ProceedAction));
    }

    public static WorkflowStepActionDto? FindMatchingAction(WorkflowStep currentStep, string? review)
    {
        if (string.IsNullOrWhiteSpace(review))
            return null;

        return ParseActions(currentStep.ActionsJson)
            .FirstOrDefault(a => ReviewMatchesProceedAction(review, a.ProceedAction));
    }

    /// <summary>Resolve next step from Actions JSON using review / ProceedAction. Returns null when no rule matches.</summary>
    public static WorkflowStep? ResolveNextStepByReview(
        WorkflowStep currentStep,
        string? review,
        IReadOnlyList<WorkflowStep> allSteps)
    {
        var action = FindMatchingAction(currentStep, review);
        if (action == null || string.IsNullOrWhiteSpace(action.ToBlockId))
            return null;

        return ResolveStepByBlockId(allSteps, action.ToBlockId);
    }

    public static WorkflowStep? ResolveStepByBlockId(IReadOnlyList<WorkflowStep> steps, string blockId)
    {
        var id = blockId.Trim();
        return steps.FirstOrDefault(s =>
            (!string.IsNullOrWhiteSpace(s.ActivityId) &&
             string.Equals(s.ActivityId, id, StringComparison.OrdinalIgnoreCase))
            || string.Equals(s.Id.ToString("D"), id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.Id.ToString("N"), id, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool ReviewMatchesProceedAction(string? review, string? proceedAction)
    {
        if (string.IsNullOrWhiteSpace(review) || string.IsNullOrWhiteSpace(proceedAction))
            return false;

        var normalizedReview = NormalizeAction(review);
        var normalizedProceed = NormalizeAction(proceedAction);

        if (string.Equals(normalizedReview, normalizedProceed, StringComparison.OrdinalIgnoreCase))
            return true;

        var reviewBucket = GetActionBucket(normalizedReview);
        var proceedBucket = GetActionBucket(normalizedProceed);
        return reviewBucket != null
            && proceedBucket != null
            && string.Equals(reviewBucket, proceedBucket, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeAction(string value) =>
        string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string? GetActionBucket(string normalized)
    {
        var upper = normalized.ToUpperInvariant();
        if (upper is "APPROVE" or "APPROVED")
            return "APPROVE";
        if (upper is "REJECT" or "REJECTED")
            return "REJECT";
        if (upper.Contains("PARTIALLY", StringComparison.Ordinal) && upper.Contains("APPROV", StringComparison.Ordinal))
            return "PARTIAL_APPROVE";
        if (upper.Contains("PARTIAL", StringComparison.Ordinal) && upper.Contains("MATCH", StringComparison.Ordinal))
            return "PARTIAL_MATCH";
        if (upper is "VERIFY" or "VERIFIED")
            return "VERIFY";
        if (upper is "MATCH" or "MATCHED")
            return "MATCH";
        if (upper is "NOT MATCHED" or "NOTMATCHED" or "UNMATCHED")
            return "NOT_MATCH";
        if (upper is "NON-INVOICE" or "NON INVOICE" or "NONINVOICE")
            return "NON_INVOICE";
        if (upper is "SUBMIT")
            return "SUBMIT";
        return null;
    }

    /// <summary>True for workflow proceed/review labels (not free-text user comments).</summary>
    public static bool IsProceedActionLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return GetActionBucket(NormalizeAction(value)) != null;
    }
}
