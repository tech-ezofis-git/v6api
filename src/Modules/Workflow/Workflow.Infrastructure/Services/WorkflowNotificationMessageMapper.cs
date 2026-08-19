namespace SaaSApp.Workflow.Infrastructure.Services;

internal static class WorkflowNotificationMessageMapper
{
    public static string SubmittedMessage(string? stageName)
    {
        var stage = stageName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(stage))
            return "The ticket has been submitted.";

        if (IsStart(stage))
            return "The ticket has been initiated";

        if (IsApprove(stage))
            return "The ticket has been submitted for approve";

        return $"The ticket has been submitted for {stage}";
    }

    public static string ReceivedMessage(string? workflowName, Guid instanceId)
    {
        var name = workflowName?.Trim();
        var instanceSuffix = instanceId.ToString("N")[..8];
        if (!string.IsNullOrWhiteSpace(name))
            return $"Ticket received for {name} - {instanceSuffix}";

        return $"Ticket received - {instanceSuffix}";
    }

    private static bool IsStart(string stage) =>
        string.Equals(stage, "Start", StringComparison.OrdinalIgnoreCase)
        || string.Equals(stage, "START", StringComparison.OrdinalIgnoreCase);

    private static bool IsApprove(string stage) =>
        string.Equals(stage, "approve", StringComparison.OrdinalIgnoreCase)
        || string.Equals(stage, "approver", StringComparison.OrdinalIgnoreCase);
}
