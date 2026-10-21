namespace SaaSApp.Workflow.Application.Workflows;

/// <summary>
/// Quantics (Kenneth) "New Packaging Requisition": when a ticket completes, its form
/// fields are synced to the packaging master form via the Python chat API
/// (intent <c>form_sync</c>). Scoped to one tenant + workflow, like <see cref="MjbUsAgent"/>.
/// </summary>
public static class QuanticsPackaging
{
    public static readonly Guid TenantId = Guid.Parse("1f10e791-b054-483a-8414-e77961f3e282");
    public static readonly Guid WorkflowId = Guid.Parse("663804ee-3def-440f-80d8-5fa1b4635e41");

    /// <summary>Source form on the requisition ticket.</summary>
    public static readonly Guid SourceFormId = Guid.Parse("aa038e45-b62f-4b4d-8dd8-638c75c3f447");

    /// <summary>Packaging master form updated by the Python form_sync intent.</summary>
    public static readonly Guid TargetFormId = Guid.Parse("f271d1d3-a0ac-4747-a9f4-09aab4214a88");

    public const string FormSyncIntent = "form_sync";
    public const string FormSyncLabel = "Master form sync";

    public static bool IsThisWorkflow(Guid workflowId, Guid? tenantId) =>
        workflowId == WorkflowId && tenantId == TenantId;
}
