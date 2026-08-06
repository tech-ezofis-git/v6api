using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SaaSApp.Catalog.Entities;
using SaaSApp.Catalog.Persistence;
using SaaSApp.MultiTenancy;
using SaaSApp.Repository.Application.Contracts;
using SaaSApp.Repository.Infrastructure.Options;

namespace SaaSApp.Repository.Infrastructure.Services;

public sealed class RepositoryItemShareService : IRepositoryItemShareService
{
    private readonly IDbContextFactory<CatalogDbContext> _catalogFactory;
    private readonly ITenantConnectionStringResolver _connectionResolver;
    private readonly IRepositoryItemQueryService _itemQuery;
    private readonly IRepositoryFileStorage _fileStorage;
    private readonly IShareGuestUserProvisioningService _guestProvisioning;
    private readonly IRepositorySecurityService _security;
    private readonly RepositoryShareOptions _options;
    private readonly ILogger<RepositoryItemShareService> _logger;

    public RepositoryItemShareService(
        IDbContextFactory<CatalogDbContext> catalogFactory,
        ITenantConnectionStringResolver connectionResolver,
        IRepositoryItemQueryService itemQuery,
        IRepositoryFileStorage fileStorage,
        IShareGuestUserProvisioningService guestProvisioning,
        IRepositorySecurityService security,
        IOptions<RepositoryShareOptions> options,
        ILogger<RepositoryItemShareService> logger)
    {
        _catalogFactory = catalogFactory;
        _connectionResolver = connectionResolver;
        _itemQuery = itemQuery;
        _fileStorage = fileStorage;
        _guestProvisioning = guestProvisioning;
        _security = security;
        _options = options.Value;
        _logger = logger;
    }

    public Task<CreateRepositoryItemShareResult> CreateShareAsync(
        Guid sourceTenantId,
        Guid repositoryId,
        Guid itemId,
        Guid sharedByUserId,
        CreateRepositoryItemShareRequest request,
        CancellationToken cancellationToken = default) =>
        CreateShareInternalAsync(
            sourceTenantId,
            repositoryId,
            itemId,
            sharedByUserId,
            request.Email,
            request.Message,
            request.ProvisionGuestUser,
            request.WorkflowInstanceId,
            request.Action == 0 ? 0 : 1,
            applyDocumentSecurity: true,
            cancellationToken);

    public Task<CreateRepositoryItemShareResult> CreateWorkflowInboxShareAsync(
        Guid sourceTenantId,
        Guid workflowInstanceId,
        Guid repositoryId,
        Guid itemId,
        Guid sharedByUserId,
        CreateWorkflowInboxShareRequest request,
        CancellationToken cancellationToken = default) =>
        CreateShareInternalAsync(
            sourceTenantId,
            repositoryId,
            itemId,
            sharedByUserId,
            request.Email,
            request.Message,
            provisionGuestUser: true,
            workflowInstanceId,
            action: request.Action == 0 ? 0 : 1,
            applyDocumentSecurity: true,
            cancellationToken);

    private async Task<CreateRepositoryItemShareResult> CreateShareInternalAsync(
        Guid sourceTenantId,
        Guid repositoryId,
        Guid itemId,
        Guid sharedByUserId,
        string email,
        string? message,
        bool provisionGuestUser,
        Guid? workflowInstanceId,
        int action,
        bool applyDocumentSecurity,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email) || email.IndexOf('@') < 1)
            throw new ArgumentException("A valid recipient email is required.");

        var item = await _itemQuery.GetItemAsync(repositoryId, sourceTenantId, itemId, cancellationToken)
            ?? throw new InvalidOperationException("Repository item not found.");

        var recipientEmail = email.Trim().ToLowerInvariant();
        Guid? guestUserId = null;

        if (provisionGuestUser)
            guestUserId = await _guestProvisioning.EnsureGuestUserAsync(sourceTenantId, recipientEmail, cancellationToken);

        // Existing tenant users (password/social already set) → isnew=false (skip set-password page).
        // New / incomplete guests → isnew=true.
        var inviteAuth = await _guestProvisioning.GetShareInviteAuthInfoAsync(
            sourceTenantId, recipientEmail, cancellationToken);
        var isNew = inviteAuth.RequiresPasswordSetup;

        var shareToken = GenerateShareToken();
        var expiresAt = DateTime.UtcNow.AddDays(_options.DefaultExpiryDays <= 0 ? 30 : _options.DefaultExpiryDays);
        var shareId = Guid.NewGuid();
        var normalizedAction = action == 0 ? 0 : 1;

        await RepositoryItemShareCatalogStore.EnsureTableAsync(_catalogFactory, cancellationToken);

        await using (var catalog = await _catalogFactory.CreateDbContextAsync(cancellationToken))
        {
            catalog.RepositoryItemShares.Add(new RepositoryItemShare
            {
                Id = shareId,
                ShareToken = shareToken,
                SourceTenantId = sourceTenantId,
                SourceRepositoryId = repositoryId,
                SourceItemId = itemId,
                SharedByUserId = sharedByUserId,
                RecipientEmail = recipientEmail,
                Message = string.IsNullOrWhiteSpace(message) ? null : message.Trim(),
                Status = ShareStatuses.Active,
                ExpiresAtUtc = expiresAt,
                CreatedAtUtc = DateTime.UtcNow,
                AutoProvisionGuest = provisionGuestUser,
                WorkflowInstanceId = workflowInstanceId,
                Action = normalizedAction
            });
            await catalog.SaveChangesAsync(cancellationToken);
        }

        if (applyDocumentSecurity && guestUserId is { } recipientId && recipientId != Guid.Empty)
        {
            await _security.EnsureShareRecipientAccessAsync(
                repositoryId,
                sourceTenantId,
                recipientId,
                itemId,
                canUpload: normalizedAction == 1,
                sharedByUserId,
                cancellationToken);
        }

        var shareUrl = BuildShareUrl(shareToken, recipientEmail, isNew);
        await TrySendShareEmailAsync(
            recipientEmail,
            item.FileName,
            shareUrl,
            message,
            provisionGuestUser,
            isNew,
            cancellationToken);

        return new CreateRepositoryItemShareResult(
            shareId,
            shareToken,
            repositoryId,
            itemId,
            recipientEmail,
            expiresAt,
            shareUrl,
            guestUserId,
            normalizedAction,
            isNew,
            inviteAuth.RequiresPasswordSetup,
            inviteAuth.AllowedAuthMethods,
            sourceTenantId,
            PermissionLabel(normalizedAction));
    }

    public async Task<bool> RecipientRequiresPasswordSetupAsync(
        string shareToken,
        CancellationToken cancellationToken = default)
    {
        var share = await LoadActiveShareAsync(shareToken, cancellationToken, requireViewerEmail: false);
        if (share == null || !share.AutoProvisionGuest)
            return false;

        return await _guestProvisioning.RequiresPasswordSetupAsync(
            share.SourceTenantId,
            share.RecipientEmail,
            cancellationToken);
    }

    public async Task<RepositoryShareAccess?> ResolveShareAccessAsync(
        string shareToken,
        string viewerEmail,
        Guid? repositoryId = null,
        Guid? itemId = null,
        CancellationToken cancellationToken = default)
    {
        var share = await LoadActiveShareAsync(shareToken, cancellationToken, viewerEmail);
        if (share == null)
            return null;

        // Share token is authoritative — use source tenant/repo/item from catalog, not caller tenant.
        await TouchLastAccessedAsync(share.Id, cancellationToken);

        return new RepositoryShareAccess(
            share.SourceTenantId,
            share.SourceRepositoryId,
            share.SourceItemId,
            share.ShareToken);
    }

    public async Task<RepositoryItemSharePreviewDto?> GetPreviewAsync(
        string shareToken,
        CancellationToken cancellationToken = default)
    {
        var share = await LoadActiveShareAsync(shareToken, cancellationToken, requireViewerEmail: false);
        if (share == null)
            return null;

        var connectionString = await _connectionResolver.GetConnectionStringAsync(share.SourceTenantId, cancellationToken);
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;

        var repo = await RepositoryCrossTenantItemReader.GetRepositoryAsync(
            connectionString, share.SourceTenantId, share.SourceRepositoryId, cancellationToken);
        if (repo == null)
            return null;

        var item = await RepositoryCrossTenantItemReader.GetItemAsync(
            connectionString, repo, share.SourceRepositoryId, share.SourceItemId, cancellationToken);

        var orgName = await GetTenantNameAsync(share.SourceTenantId, cancellationToken);
        var authInfo = share.AutoProvisionGuest
            ? await _guestProvisioning.GetShareInviteAuthInfoAsync(
                share.SourceTenantId,
                share.RecipientEmail,
                cancellationToken)
            : new ShareInviteAuthInfo(
                false,
                false,
                null,
                ["password_login"],
                null);

        return new RepositoryItemSharePreviewDto(
            share.ShareToken,
            share.SourceTenantId,
            share.SourceRepositoryId,
            share.SourceItemId,
            item?.FileName,
            orgName,
            share.RecipientEmail,
            share.ExpiresAtUtc,
            RequiresLogin: true,
            authInfo.RequiresPasswordSetup,
            authInfo.RequiredSocialProvider,
            authInfo.AllowedAuthMethods,
            authInfo.LoginType,
            share.AutoProvisionGuest,
            share.WorkflowInstanceId,
            share.Action == 0 ? 0 : 1,
            PermissionLabel(share.Action));
    }

    public async Task<IReadOnlyList<SharedWithMeItemDto>> ListSharesForRecipientAsync(
        string recipientEmail,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(recipientEmail))
            return Array.Empty<SharedWithMeItemDto>();

        var email = recipientEmail.Trim().ToLowerInvariant();
        await RepositoryItemShareCatalogStore.EnsureTableAsync(_catalogFactory, cancellationToken);

        List<RepositoryItemShare> shares;
        await using (var catalog = await _catalogFactory.CreateDbContextAsync(cancellationToken))
        {
            shares = await catalog.RepositoryItemShares
                .AsNoTracking()
                .Where(s => s.RecipientEmail == email
                            && s.Status == ShareStatuses.Active
                            && s.ExpiresAtUtc > DateTime.UtcNow)
                .OrderByDescending(s => s.CreatedAtUtc)
                .ToListAsync(cancellationToken);
        }

        if (shares.Count == 0)
            return Array.Empty<SharedWithMeItemDto>();

        var results = new List<SharedWithMeItemDto>(shares.Count);
        var orgNameCache = new Dictionary<Guid, string?>();
        var connectionCache = new Dictionary<Guid, string?>();

        foreach (var share in shares)
        {
            if (!orgNameCache.TryGetValue(share.SourceTenantId, out var orgName))
            {
                orgName = await GetTenantNameAsync(share.SourceTenantId, cancellationToken);
                orgNameCache[share.SourceTenantId] = orgName;
            }

            string? fileName = null;
            try
            {
                if (!connectionCache.TryGetValue(share.SourceTenantId, out var connectionString))
                {
                    connectionString = await _connectionResolver.GetConnectionStringAsync(share.SourceTenantId, cancellationToken);
                    connectionCache[share.SourceTenantId] = connectionString;
                }

                if (!string.IsNullOrWhiteSpace(connectionString))
                {
                    var repo = await RepositoryCrossTenantItemReader.GetRepositoryAsync(
                        connectionString, share.SourceTenantId, share.SourceRepositoryId, cancellationToken);
                    if (repo != null)
                    {
                        var item = await RepositoryCrossTenantItemReader.GetItemAsync(
                            connectionString, repo, share.SourceRepositoryId, share.SourceItemId, cancellationToken);
                        fileName = item?.FileName;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read shared file name for share {ShareId}", share.Id);
            }

            results.Add(new SharedWithMeItemDto(
                share.Id,
                share.ShareToken,
                share.SourceRepositoryId,
                share.SourceItemId,
                fileName,
                orgName,
                share.CreatedAtUtc,
                share.ExpiresAtUtc,
                share.Action == 0 ? 0 : 1,
                PermissionLabel(share.Action)));
        }

        return results;
    }

    public async Task<bool> RevokeShareAsync(
        Guid shareId,
        Guid sourceTenantId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await RepositoryItemShareCatalogStore.EnsureTableAsync(_catalogFactory, cancellationToken);

        await using var catalog = await _catalogFactory.CreateDbContextAsync(cancellationToken);
        var share = await catalog.RepositoryItemShares
            .FirstOrDefaultAsync(
                s => s.Id == shareId
                     && s.SourceTenantId == sourceTenantId
                     && s.SharedByUserId == userId
                     && s.Status == ShareStatuses.Active,
                cancellationToken);

        if (share == null)
            return false;

        share.Status = ShareStatuses.Revoked;
        await catalog.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<Guid?> GetActiveWorkflowShareOwnerUserIdAsync(
        Guid workflowInstanceId,
        CancellationToken cancellationToken = default)
    {
        if (workflowInstanceId == Guid.Empty)
            return null;

        await RepositoryItemShareCatalogStore.EnsureTableAsync(_catalogFactory, cancellationToken);

        await using var catalog = await _catalogFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var ownerId = await catalog.RepositoryItemShares
            .AsNoTracking()
            .Where(s =>
                s.WorkflowInstanceId == workflowInstanceId
                && s.Status == ShareStatuses.Active
                && s.AutoProvisionGuest
                && s.ExpiresAtUtc > now)
            .OrderByDescending(s => s.CreatedAtUtc)
            .Select(s => (Guid?)s.SharedByUserId)
            .FirstOrDefaultAsync(cancellationToken);

        return ownerId is Guid id && id != Guid.Empty ? id : null;
    }

    private async Task<RepositoryItemShare?> LoadActiveShareAsync(
        string shareToken,
        CancellationToken cancellationToken,
        string? viewerEmail = null,
        bool requireViewerEmail = true)
    {
        if (string.IsNullOrWhiteSpace(shareToken))
            return null;

        await RepositoryItemShareCatalogStore.EnsureTableAsync(_catalogFactory, cancellationToken);

        await using var catalog = await _catalogFactory.CreateDbContextAsync(cancellationToken);
        var share = await catalog.RepositoryItemShares
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.ShareToken == shareToken.Trim(), cancellationToken);

        if (share == null
            || !string.Equals(share.Status, ShareStatuses.Active, StringComparison.OrdinalIgnoreCase)
            || share.ExpiresAtUtc <= DateTime.UtcNow)
        {
            return null;
        }

        if (requireViewerEmail)
        {
            if (string.IsNullOrWhiteSpace(viewerEmail))
                return null;

            if (!string.Equals(share.RecipientEmail, viewerEmail.Trim(), StringComparison.OrdinalIgnoreCase))
                return null;
        }

        return share;
    }

    private async Task TouchLastAccessedAsync(Guid shareId, CancellationToken cancellationToken)
    {
        try
        {
            await using var catalog = await _catalogFactory.CreateDbContextAsync(cancellationToken);
            var share = await catalog.RepositoryItemShares.FirstOrDefaultAsync(s => s.Id == shareId, cancellationToken);
            if (share == null)
                return;

            share.LastAccessedAtUtc = DateTime.UtcNow;
            await catalog.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update LastAccessedAtUtc for share {ShareId}", shareId);
        }
    }

    private async Task<string?> GetTenantNameAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var catalog = await _catalogFactory.CreateDbContextAsync(cancellationToken);
        return await catalog.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => t.Name)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private string BuildShareUrl(string shareToken, string recipientEmail, bool isNew)
    {
        var baseUrl = (_options.FrontendBaseUrl ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
            baseUrl = "https://demoapp.ezofis.com";

        var signInPath = string.IsNullOrWhiteSpace(_options.SignInPath) ? "/sign-in" : _options.SignInPath.Trim();
        if (!signInPath.StartsWith('/'))
            signInPath = "/" + signInPath;

        var emailQuery = Uri.EscapeDataString(recipientEmail);
        var isNewQuery = isNew ? "true" : "false";
        return $"{baseUrl}{signInPath}?shareToken={Uri.EscapeDataString(shareToken)}&email={emailQuery}&isnew={isNewQuery}";
    }

    private async Task TrySendShareEmailAsync(
        string recipientEmail,
        string? fileName,
        string shareUrl,
        string? message,
        bool guestInvite,
        bool isNew,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var catalog = await _catalogFactory.CreateDbContextAsync(cancellationToken);
            var settings = await catalog.MailSettings
                .AsNoTracking()
                .Where(x => x.Preference == 1 && !x.Isdeleted)
                .OrderByDescending(x => x.SettingId)
                .FirstOrDefaultAsync(cancellationToken);

            if (settings == null
                || string.IsNullOrWhiteSpace(settings.EmailId)
                || string.IsNullOrWhiteSpace(settings.Password)
                || string.IsNullOrWhiteSpace(settings.OutgoingServer)
                || settings.OutgoingPort <= 0)
            {
                _logger.LogWarning("Share email not sent: mailsettings not configured.");
                return;
            }

            var docLabel = string.IsNullOrWhiteSpace(fileName) ? "a document" : $"'{fileName}'";
            var guestNote = !guestInvite
                ? "<p>If you do not have an account, sign up with this email address, then open the link again after login.</p>"
                : isNew
                    ? "<p>An account has been prepared for you. Open the link to <strong>set your password</strong> or sign in with Google/Microsoft, then view the shared file.</p>"
                    : "<p>Open the link and <strong>sign in</strong> with your existing account to view the shared file.</p>";
            var body = $"""
                <p>A document has been shared with you: <strong>{WebUtility.HtmlEncode(docLabel)}</strong>.</p>
                {(string.IsNullOrWhiteSpace(message) ? "" : $"<p>{WebUtility.HtmlEncode(message)}</p>")}
                <p><a href="{WebUtility.HtmlEncode(shareUrl)}">Open shared document</a></p>
                <p style="word-break:break-all;color:#555;font-size:12px">{WebUtility.HtmlEncode(shareUrl)}</p>
                {guestNote}
                """;

            using var mail = new MailMessage
            {
                From = new MailAddress(settings.EmailId),
                Subject = _options.EmailSubject,
                Body = body,
                IsBodyHtml = true
            };
            mail.To.Add(recipientEmail);

            using var smtp = new SmtpClient(settings.OutgoingServer, settings.OutgoingPort)
            {
                EnableSsl = true,
                Credentials = new NetworkCredential(settings.EmailId, settings.Password)
            };
            await smtp.SendMailAsync(mail, cancellationToken);
            _logger.LogInformation("Share invite email sent to {Email} (isNew={IsNew})", recipientEmail, isNew);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send repository share email to {Email}", recipientEmail);
        }
    }

    private static string PermissionLabel(int action) =>
        action == 0 ? "Can View" : "Can Edit";

    private static string GenerateShareToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
