namespace SaaSApp.Repository.Application.Contracts;

public static class SignRequestModes
{
    public const string Parallel = "parallel";
    public const string Sequential = "sequential";
}

public static class SignRequestStatuses
{
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
    public const string Expired = "Expired";
}

public static class SignRequestSignerStatuses
{
    public const string Waiting = "Waiting";
    public const string Pending = "Pending";
    public const string Signed = "Signed";
    public const string Declined = "Declined";
}

public sealed record CreateSignRequestSignerDto(
    string Email,
    string? Name = null,
    int Order = 1);

public sealed record CreateSignRequestDto(
    string SigningMode,
    IReadOnlyList<CreateSignRequestSignerDto> Signers,
    string? Message = null,
    int? ExpiresInDays = null);

public sealed record SignRequestSignerDto(
    Guid SignerId,
    string Email,
    string? Name,
    int Order,
    string Status,
    string? InviteUrl,
    DateTime? InvitedAtUtc,
    DateTime? SignedAtUtc);

public sealed record SignRequestDto(
    Guid SignRequestId,
    Guid RepositoryId,
    Guid ItemId,
    string? FileName,
    string SigningMode,
    string Status,
    string? Message,
    Guid InitiatedByUserId,
    string? InitiatedByEmail,
    string? InitiatedByName,
    DateTime ExpiresAtUtc,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc,
    IReadOnlyList<SignRequestSignerDto> Signers);

public sealed record SignRequestInvitePreviewDto(
    string InviteToken,
    Guid SignRequestId,
    Guid TenantId,
    Guid RepositoryId,
    Guid ItemId,
    string? FileName,
    string? SourceOrganizationName,
    string SenderName,
    string SenderEmail,
    string RecipientEmail,
    string SigningMode,
    int SignerOrder,
    int SignerCount,
    string SignerStatus,
    string SignRequestStatus,
    DateTime ExpiresAtUtc,
    bool RequiresLogin,
    bool RequiresPasswordSetup,
    string? RequiredSocialProvider,
    IReadOnlyList<string> AllowedAuthMethods,
    string? LoginType,
    string? Message);

public sealed record SubmitSignRequestDto(
    int PageNumber,
    double X,
    double Y,
    double Width,
    double Height,
    string SignatureImageBase64,
    DateTime? SignedAtClientUtc = null);

public sealed record DeclineSignRequestDto(string? Reason = null);

/// <summary>Pending sign task for the logged-in user (no invite token required).</summary>
public sealed record MyPendingSignRequestDto(
    Guid SignRequestId,
    Guid RepositoryId,
    Guid ItemId,
    string? FileName,
    string SigningMode,
    string Status,
    string SignerStatus,
    int SignerOrder,
    string? Message,
    string SenderName,
    string SenderEmail,
    DateTime ExpiresAtUtc,
    DateTime CreatedAtUtc,
    /// <summary>Present so FE can still deep-link if desired.</summary>
    string? InviteToken);

public interface IRepositorySignRequestService
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken = default);

    Task<SignRequestDto> CreateAsync(
        Guid tenantId,
        Guid repositoryId,
        Guid itemId,
        Guid initiatedByUserId,
        string? initiatedByEmail,
        string? initiatedByName,
        CreateSignRequestDto request,
        CancellationToken cancellationToken = default);

    Task<SignRequestDto?> GetAsync(
        Guid tenantId,
        Guid signRequestId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SignRequestDto>> ListForItemAsync(
        Guid tenantId,
        Guid repositoryId,
        Guid itemId,
        CancellationToken cancellationToken = default);

    /// <summary>Pending/Waiting-active sign tasks for this email in the tenant (logged-in path, no invite token).</summary>
    Task<IReadOnlyList<MyPendingSignRequestDto>> ListPendingForMeAsync(
        Guid tenantId,
        string signerEmail,
        CancellationToken cancellationToken = default);

    Task<SignRequestInvitePreviewDto?> GetInvitePreviewAsync(
        string inviteToken,
        CancellationToken cancellationToken = default);

    Task<RepositoryItemFileContent?> OpenInviteFileAsync(
        string inviteToken,
        string viewerEmail,
        CancellationToken cancellationToken = default);

    /// <summary>Open file for logged-in signer by signRequestId + email (no invite token).</summary>
    Task<RepositoryItemFileContent?> OpenFileForSignerAsync(
        Guid tenantId,
        Guid signRequestId,
        string viewerEmail,
        CancellationToken cancellationToken = default);

    Task<SignRequestDto> SubmitSignatureAsync(
        string inviteToken,
        string signerEmail,
        Guid? signerUserId,
        SubmitSignRequestDto request,
        CancellationToken cancellationToken = default);

    /// <summary>Sign as logged-in user matched by email on this request (no invite token).</summary>
    Task<SignRequestDto> SubmitSignatureForSignerAsync(
        Guid tenantId,
        Guid signRequestId,
        string signerEmail,
        Guid? signerUserId,
        SubmitSignRequestDto request,
        CancellationToken cancellationToken = default);

    Task<SignRequestDto> DeclineAsync(
        string inviteToken,
        string signerEmail,
        DeclineSignRequestDto request,
        CancellationToken cancellationToken = default);

    Task<SignRequestDto> DeclineForSignerAsync(
        Guid tenantId,
        Guid signRequestId,
        string signerEmail,
        DeclineSignRequestDto request,
        CancellationToken cancellationToken = default);

    Task<SignRequestDto> CancelAsync(
        Guid tenantId,
        Guid signRequestId,
        Guid userId,
        CancellationToken cancellationToken = default);
}
