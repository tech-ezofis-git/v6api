namespace SaaSApp.Workflow.Application.Contracts;

/// <summary>Optional form identity/data passed from move-next so inbox rows store current ezfb values.</summary>
public sealed record MailboxFormSnapshot(
    string? FormId,
    Guid? FormEntryId,
    string? FormDataJson);
