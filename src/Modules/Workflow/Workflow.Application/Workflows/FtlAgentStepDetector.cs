using SaaSApp.Workflow.Domain.Entities;

namespace SaaSApp.Workflow.Application.Workflows;

/// <summary>FTL qualifier / quote / document-generate steps. Does not match AP Agent.</summary>
public static class FtlAgentStepDetector
{
    public const string Qualifier = "qualifier";
    public const string Quote = "quote";
    public const string Document = "document";

    public static bool IsQualifyAgent(WorkflowStep step) =>
        IsStage(step, "QUALIFY_AGENT");

    public static bool IsQuoteAgent(WorkflowStep step) =>
        IsStage(step, "QUOTE_AGENT");

    public static bool IsDocumentGenerateAgent(WorkflowStep step) =>
        IsStage(step, "DOCUMENT_GENERATE_AGENT");

    /// <summary>Hangfire mode when this step should call FTL /chat. Null for every other step.</summary>
    public static string? HangfireMode(WorkflowStep? step)
    {
        if (step == null)
            return null;
        if (IsQualifyAgent(step))
            return Qualifier;
        if (IsQuoteAgent(step))
            return Quote;
        if (IsDocumentGenerateAgent(step))
            return Document;
        return null;
    }

    private static bool IsStage(WorkflowStep step, string stageType) =>
        string.Equals(step.StageType?.Trim(), stageType, StringComparison.OrdinalIgnoreCase)
        || string.Equals(step.Name?.Trim(), stageType.Replace('_', ' '), StringComparison.OrdinalIgnoreCase);
}
