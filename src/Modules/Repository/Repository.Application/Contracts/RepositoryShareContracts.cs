namespace SaaSApp.Repository.Application.Contracts;

public sealed record CreateRepositoryItemShareRequest(
    string Email,
    string? Message = null,
    /// <summary>
    /// When true (default), creates a guest <c>TenantUser</c> and document-security grants
    /// so the recipient can open this repository and see only the shared file (+ their uploads).
    /// </summary>
    bool ProvisionGuestUser = true,
    Guid? WorkflowInstanceId = null,
    /// <summary>
    /// Invite permission (same UX as workflow share): <c>0</c> = Can View, <c>1</c> = Can Edit (upload).
    /// </summary>
    int Action = 0);

public sealed record CreateWorkflowInboxShareRequest(
    string Email,
    Guid RepositoryId,
    Guid ItemId,
    string? Message = null,
    /// <summary>
    /// Inbox action flag for the shared ticket: <c>1</c> (default) = show verify/approve buttons,
    /// <c>0</c> = hide action buttons in the UI.
    /// </summary>
    int Action = 1);

public sealed record CreateRepositoryItemShareResult(
    Guid ShareId,
    string ShareToken,
    Guid SourceRepositoryId,
    Guid SourceItemId,
    string RecipientEmail,
    DateTime ExpiresAtUtc,
    /// <summary>
    /// Invite link (same as workflow share): <c>/sign-in?shareToken=...&amp;email=...&amp;isnew=true|false</c>.
    /// Email is also sent with this URL.
    /// </summary>
    string ShareUrl,
    /// <summary>Guest tenant user id when auto-provisioned; otherwise null.</summary>
    Guid? GuestUserId = null,
    /// <summary>0 = Can View, 1 = Can Edit (upload).</summary>
    int Action = 0,
    /// <summary>True when recipient still needs first-time password / social setup (<c>isnew=true</c> in ShareUrl).</summary>
    bool IsNew = false,
    /// <summary>Same as preview — what the sign-in page should show for this email.</summary>
    bool RequiresPasswordSetup = false,
    IReadOnlyList<string>? AllowedAuthMethods = null,
    Guid? SourceTenantId = null,
    /// <summary>UI label: <c>Can View</c> or <c>Can Edit</c>.</summary>
    string Permission = "Can View");

/// <summary>How a share invite recipient should authenticate.</summary>
public sealed record ShareInviteAuthInfo(
    bool UserExists,
    /// <summary>True when recipient may set a first-time EZOFIS password.</summary>
    bool RequiresPasswordSetup,
    /// <summary>When set, account is locked to this provider only (<c>google</c> or <c>microsoft</c>).</summary>
    string? RequiredSocialProvider,
    /// <summary>
    /// What the sign-in page may offer: <c>password_setup</c>, <c>google</c>, <c>microsoft</c>, <c>password_login</c>.
    /// For new share guests (auth not chosen yet), all three first-time options are returned.
    /// </summary>
    IReadOnlyList<string> AllowedAuthMethods,
    string? LoginType);

public sealed record RepositoryItemSharePreviewDto(
    string ShareToken,
    Guid SourceTenantId,
    Guid SourceRepositoryId,
    Guid SourceItemId,
    string? FileName,
    string? SourceOrganizationName,
    string RecipientEmail,
    DateTime ExpiresAtUtc,
    bool RequiresLogin,
    bool RequiresPasswordSetup,
    /// <summary>When set, account already uses this provider only.</summary>
    string? RequiredSocialProvider,
    /// <summary>Options to show on sign-in. New guests get password_setup + google + microsoft.</summary>
    IReadOnlyList<string> AllowedAuthMethods,
    string? LoginType,
    bool AutoProvisionGuest,
    Guid? WorkflowInstanceId,
    /// <summary>0 = Can View, 1 = Can Edit (upload).</summary>
    int Action = 0,
    /// <summary>UI label: <c>Can View</c> or <c>Can Edit</c>.</summary>
    string Permission = "Can View");

/// <summary>A file that was shared with the logged-in user (for the "Shared with me" list).</summary>
public sealed record SharedWithMeItemDto(
    Guid ShareId,
    string ShareToken,
    Guid SourceRepositoryId,
    Guid SourceItemId,
    string? FileName,
    string? SourceOrganizationName,
    DateTime SharedAtUtc,
    DateTime ExpiresAtUtc,
    /// <summary>0 = Can View, 1 = Can Edit (upload).</summary>
    int Action = 0,
    /// <summary>UI label: <c>Can View</c> or <c>Can Edit</c>.</summary>
    string Permission = "Can View");

public interface IRepositoryItemShareService
{
    Task<CreateRepositoryItemShareResult> CreateShareAsync(
        Guid sourceTenantId,
        Guid repositoryId,
        Guid itemId,
        Guid sharedByUserId,
        CreateRepositoryItemShareRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Workflow inbox share: provisions guest user in tenant and creates read-only file share.</summary>
    Task<CreateRepositoryItemShareResult> CreateWorkflowInboxShareAsync(
        Guid sourceTenantId,
        Guid workflowInstanceId,
        Guid repositoryId,
        Guid itemId,
        Guid sharedByUserId,
        CreateWorkflowInboxShareRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Whether the share recipient still needs to set their first password.</summary>
    Task<bool> RecipientRequiresPasswordSetupAsync(
        string shareToken,
        CancellationToken cancellationToken = default);

    Task<RepositoryItemSharePreviewDto?> GetPreviewAsync(
        string shareToken,
        CancellationToken cancellationToken = default);

    /// <summary>Active shares for a logged-in recipient (so they can reopen without the email link).</summary>
    Task<IReadOnlyList<SharedWithMeItemDto>> ListSharesForRecipientAsync(
        string recipientEmail,
        CancellationToken cancellationToken = default);

    /// <summary>Validate share token + viewer email for existing repository API routes.</summary>
    Task<RepositoryShareAccess?> ResolveShareAccessAsync(
        string shareToken,
        string viewerEmail,
        Guid? repositoryId = null,
        Guid? itemId = null,
        CancellationToken cancellationToken = default);

    Task<bool> RevokeShareAsync(
        Guid shareId,
        Guid sourceTenantId,
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Active workflow inbox share owner for an instance, if any.
    /// Used to keep the sharer on inbox while the guest holds the task, and to return the next step to the owner.
    /// </summary>
    Task<Guid?> GetActiveWorkflowShareOwnerUserIdAsync(
        Guid workflowInstanceId,
        CancellationToken cancellationToken = default);
}
