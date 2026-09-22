namespace SaaSApp.Workflow.Application.Contracts;

/// <summary>
/// Keeps workflow.Inbox_{suffix}, Sent_{suffix}, and Completed_{suffix} (first 8 chars of workflow id, no hyphens)
/// in sync with workflow.transaction_{suffix} rows.
/// </summary>
public interface IWorkflowLegacyMailboxSyncService
{
    /// <summary>Upserts/removes mailbox row for a single transaction row after insert or update.</summary>
    /// <param name="inboxAction">
    /// Optional Inbox <c>action</c> flag (0/1). When null, defaults to 1 (show verify/approve).
    /// </param>
    /// <param name="ownerMailboxCopy">
    /// What to do with <c>modified_by</c> when they differ from the assignee:
    /// <c>Inbox</c> = share-file (default), <c>Sent</c> = watch in Sent, <c>None</c> = assignee Inbox only (Forward).
    /// </param>
    Task SyncTransactionRowAsync(
        Guid workflowId,
        int transactionRowId,
        CancellationToken cancellationToken = default,
        int? inboxAction = null,
        MailboxOwnerCopyKind ownerMailboxCopy = MailboxOwnerCopyKind.Inbox);

    /// <summary>Syncs all END-stage transactions for a workflow instance (e.g. when workflow completes).</summary>
    Task SyncInstanceEndTransactionsAsync(
        Guid workflowId,
        Guid workflowInstanceId,
        CancellationToken cancellationToken = default);

    /// <summary>Updates formId, formEntryId, and formData on all Inbox/Sent/Completed rows for a workflow instance.</summary>
    Task PropagateInstanceFormDataAsync(
        Guid workflowId,
        Guid workflowInstanceId,
        MailboxFormSnapshot formData,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads <c>process_form_*</c> for the instance (w_form_id + form_entry_id).
    /// Used when move-next omits formEntryId but the start already linked an ezfb row.
    /// </summary>
    Task<(string? FormId, Guid? FormEntryId)?> TryGetProcessFormIdentityAsync(
        Guid workflowId,
        Guid workflowInstanceId,
        CancellationToken cancellationToken = default);
}

/// <summary>Where the previous actor (<c>modified_by</c>) is placed when an open task is assigned to someone else.</summary>
public enum MailboxOwnerCopyKind
{
    /// <summary>Share-file: keep owner on Inbox (often action=0).</summary>
    Inbox = 0,
    /// <summary>Owner watches in Sent.</summary>
    Sent = 1,
    /// <summary>No copy — only the assignee gets Inbox (Forward).</summary>
    None = 2
}
