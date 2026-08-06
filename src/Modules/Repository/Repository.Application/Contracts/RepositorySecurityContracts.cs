namespace SaaSApp.Repository.Application.Contracts;

public static class RepositorySecurityActions
{
    public const string Grant = "grant";
    public const string Hide = "hide";
}

public static class RepositorySecurityMatchModes
{
    public const string All = "all";
    public const string Any = "any";
}

public static class RepositorySecurityPermissions
{
    public const string View = "view";
    public const string Upload = "upload";
    public const string Download = "download";
    public const string Print = "print";
    public const string Delete = "delete";
    public const string EditMetadata = "editMetadata";
    public const string EditDocument = "editDocument";
    public const string CheckOut = "checkOut";
    public const string CheckIn = "checkIn";
    public const string SendForSignature = "sendForSignature";
}

public sealed record RepositoryPermissionFlagsDto(
    bool View = true,
    bool Upload = false,
    bool Download = false,
    bool Print = false,
    bool Delete = false,
    bool EditMetadata = false,
    bool EditDocument = false,
    bool CheckOut = false,
    bool CheckIn = false,
    bool SendForSignature = false);

public sealed record RepositoryFolderSecurityPolicyDto(
    IReadOnlyList<Guid> UserIds,
    IReadOnlyList<Guid> GroupIds,
    RepositoryPermissionFlagsDto Permissions,
    Guid? FolderId = null);

public sealed record RepositoryFolderSecurityDto(
    Guid RepositoryId,
    IReadOnlyList<RepositoryFolderSecurityPolicyDto> Policies);

public sealed record RepositoryFolderSecurityUpsertRequest(
    IReadOnlyList<RepositoryFolderSecurityPolicyDto> Policies,
    Guid? FolderId = null);

public sealed record RepositoryDocumentSecurityConditionDto(
    string Field,
    string Op,
    string? Value);

public sealed record RepositoryDocumentSecurityRuleDto(
    string Action,
    string Match,
    IReadOnlyList<RepositoryDocumentSecurityConditionDto> Conditions,
    IReadOnlyList<Guid> UserIds,
    IReadOnlyList<Guid> GroupIds,
    /// <summary><c>Share</c> = created by file invite; preserved when Admin saves document security.</summary>
    string? Source = null);

public sealed record RepositoryDocumentSecurityDto(
    Guid RepositoryId,
    IReadOnlyList<RepositoryDocumentSecurityRuleDto> Rules);

public sealed record RepositoryDocumentSecurityUpsertRequest(
    IReadOnlyList<RepositoryDocumentSecurityRuleDto> Rules);

/// <summary>Folder + document security configuration and TenantUser access evaluation.</summary>
public interface IRepositorySecurityService
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken = default);

    Task<RepositoryFolderSecurityDto> GetFolderSecurityAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid? folderId = null,
        CancellationToken cancellationToken = default);

    Task<RepositoryFolderSecurityDto> SaveFolderSecurityAsync(
        Guid repositoryId,
        Guid tenantId,
        RepositoryFolderSecurityUpsertRequest request,
        Guid? userId,
        CancellationToken cancellationToken = default);

    Task<RepositoryDocumentSecurityDto> GetDocumentSecurityAsync(
        Guid repositoryId,
        Guid tenantId,
        CancellationToken cancellationToken = default);

    Task<RepositoryDocumentSecurityDto> SaveDocumentSecurityAsync(
        Guid repositoryId,
        Guid tenantId,
        RepositoryDocumentSecurityUpsertRequest request,
        Guid? userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Invite share: mark recipient as share-scoped on this repository, grant view on the shared item,
    /// and grant view on files they upload (<c>CreatedBy</c>). Does not lock the repo for other users.
    /// </summary>
    Task EnsureShareRecipientAccessAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid recipientUserId,
        Guid sharedItemId,
        bool canUpload,
        Guid? sharedByUserId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Admin bypasses. TenantUser needs View (or grant-only access for item paths).</summary>
    Task<bool> CanAccessRepositoryAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid userId,
        bool isAdmin,
        string permission = RepositorySecurityPermissions.View,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> FilterAccessibleRepositoryIdsAsync(
        IReadOnlyList<Guid> repositoryIds,
        Guid tenantId,
        Guid userId,
        bool isAdmin,
        CancellationToken cancellationToken = default);

    Task<bool> CanAccessItemAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid userId,
        bool isAdmin,
        IReadOnlyDictionary<string, string?> itemFields,
        string permission = RepositorySecurityPermissions.View,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<T>> FilterAccessibleItemsAsync<T>(
        Guid repositoryId,
        Guid tenantId,
        Guid userId,
        bool isAdmin,
        IReadOnlyList<T> items,
        Func<T, IReadOnlyDictionary<string, string?>> fieldSelector,
        string permission = RepositorySecurityPermissions.View,
        CancellationToken cancellationToken = default);
}
