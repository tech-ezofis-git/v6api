namespace SaaSApp.Workflow.Infrastructure.Options;

public sealed class WorkflowMoveNotificationOptions
{
    public const string SectionName = "WorkflowMoveNotifications";

    public bool Enabled { get; set; } = true;

    public string Category { get; set; } = "workflow";
}

