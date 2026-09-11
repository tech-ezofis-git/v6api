namespace SaaSApp.Workflow.Application.Connectors;

public static class EmailIngestMasterSources
{
    public const string InternalForm = "InternalForm";
    public const string QuickBooks = "QuickBooks";
    /// <summary>PO Master = SAP connector (sample or live). Payload resource becomes SAP.</summary>
    public const string Sap = "SAP";
}

public sealed record EmailIngestMailboxDto(
    Guid Id,
    Guid ConnectorId,
    Guid WorkflowId,
    bool IsEnabled,
    int PollIntervalMinutes,
    string? QueryFilter,
    string MasterSource,
    string? MasterFormId,
    Guid? MasterConnectorId,
    string AttachmentExtensions,
    DateTime? LastPolledAtUtc,
    string? LastError,
    DateTime CreatedAtUtc,
    DateTime? ModifiedAtUtc,
    string? ConnectorName,
    string? ConnectorProviderCode,
    string? ExternalAccountEmail);

public sealed record EmailIngestMailboxUpsertRequest(
    Guid ConnectorId,
    Guid WorkflowId,
    bool IsEnabled = true,
    int PollIntervalMinutes = 5,
    string? QueryFilter = null,
    string MasterSource = EmailIngestMasterSources.InternalForm,
    string? MasterFormId = null,
    Guid? MasterConnectorId = null,
    string? AttachmentExtensions = null);

public sealed record EmailIngestPollResultDto(
    Guid MailboxId,
    int MessagesScanned,
    int AttachmentsStarted,
    int SkippedAlreadyProcessed,
    string? Error);

public sealed record EmailIngestProcessedDto(
    Guid Id,
    Guid MailboxId,
    string ProviderMessageId,
    string AttachmentId,
    Guid? WorkflowInstanceId,
    DateTime ProcessedAtUtc);
