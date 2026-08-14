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

    public static string ReceivedMessage(string? stageName)
    {
        var stage = stageName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(stage))
            return "You have received the ticket.";

        if (IsStart(stage))
            return "You have received the ticket to initiate";

        if (IsApprove(stage))
            return "You have received the ticket for approve";

        return $"You have received the ticket for {stage}";
    }

    private static bool IsStart(string stage) =>
        string.Equals(stage, "Start", StringComparison.OrdinalIgnoreCase)
        || string.Equals(stage, "START", StringComparison.OrdinalIgnoreCase);

    private static bool IsApprove(string stage) =>
        string.Equals(stage, "approve", StringComparison.OrdinalIgnoreCase)
        || string.Equals(stage, "approver", StringComparison.OrdinalIgnoreCase);
}
