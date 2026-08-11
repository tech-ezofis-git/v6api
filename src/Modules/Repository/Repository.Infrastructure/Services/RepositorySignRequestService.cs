using System.Collections.Concurrent;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SaaSApp.Catalog.Persistence;
using SaaSApp.MultiTenancy;
using SaaSApp.Repository.Application.Contracts;
using SaaSApp.Repository.Infrastructure.Options;

namespace SaaSApp.Repository.Infrastructure.Services;

public sealed class RepositorySignRequestService : IRepositorySignRequestService
{
    private static readonly ConcurrentDictionary<string, byte> SchemaEnsured = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly ITenantConnectionProvider _connectionProvider;
    private readonly ITenantConnectionStringResolver _connectionResolver;
    private readonly IStaticRepositoryProvisioner _provisioner;
    private readonly IRepositoryItemQueryService _itemQuery;
    private readonly IRepositoryFileStorage _fileStorage;
    private readonly IShareGuestUserProvisioningService _guestProvisioning;
    private readonly IDbContextFactory<CatalogDbContext> _catalogFactory;
    private readonly RepositorySignRequestOptions _options;
    private readonly ILogger<RepositorySignRequestService> _logger;

    public RepositorySignRequestService(
        ITenantConnectionProvider connectionProvider,
        ITenantConnectionStringResolver connectionResolver,
        IStaticRepositoryProvisioner provisioner,
        IRepositoryItemQueryService itemQuery,
        IRepositoryFileStorage fileStorage,
        IShareGuestUserProvisioningService guestProvisioning,
        IDbContextFactory<CatalogDbContext> catalogFactory,
        IOptions<RepositorySignRequestOptions> options,
        ILogger<RepositorySignRequestService> logger)
    {
        _connectionProvider = connectionProvider;
        _connectionResolver = connectionResolver;
        _provisioner = provisioner;
        _itemQuery = itemQuery;
        _fileStorage = fileStorage;
        _guestProvisioning = guestProvisioning;
        _catalogFactory = catalogFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        var cs = RequireConnectionString();
        if (SchemaEnsured.ContainsKey(cs))
            return;

        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(EnsureSchemaSql, connection) { CommandTimeout = 120 };
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        SchemaEnsured.TryAdd(cs, 0);
    }

    public async Task<SignRequestDto> CreateAsync(
        Guid tenantId,
        Guid repositoryId,
        Guid itemId,
        Guid initiatedByUserId,
        string? initiatedByEmail,
        string? initiatedByName,
        CreateSignRequestDto request,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureTenantConnectionAsync(tenantId, cancellationToken);

        var repo = await _provisioner.GetRepositoryAsync(repositoryId, tenantId, cancellationToken)
            ?? throw new InvalidOperationException("Repository not found.");
        _ = repo;

        var item = await _itemQuery.GetItemAsync(repositoryId, tenantId, itemId, cancellationToken)
            ?? throw new InvalidOperationException("Repository item not found.");

        if (string.IsNullOrWhiteSpace(item.FilePath))
            throw new InvalidOperationException("This item has no file to sign.");

        var fileName = item.FileName ?? "document.pdf";
        if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            && !(item.FileType?.Contains("pdf", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            throw new InvalidOperationException("Sign request currently supports PDF files only.");
        }

        var mode = NormalizeMode(request.SigningMode);
        var signers = NormalizeSigners(request.Signers);
        if (signers.Count == 0)
            throw new ArgumentException("At least one signer is required.");

        var expiryDays = request.ExpiresInDays is > 0 ? request.ExpiresInDays.Value : (_options.DefaultExpiryDays <= 0 ? 14 : _options.DefaultExpiryDays);
        var requestId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var expires = now.AddDays(expiryDays);
        var senderEmail = string.IsNullOrWhiteSpace(initiatedByEmail) ? "unknown@ezofis.com" : initiatedByEmail.Trim();
        var senderName = string.IsNullOrWhiteSpace(initiatedByName) ? senderEmail : initiatedByName.Trim();
        var orgName = await GetTenantNameAsync(tenantId, cancellationToken);

        var signerRows = new List<(Guid Id, string Email, string? Name, int Order, string Status, string Token)>();
        for (var i = 0; i < signers.Count; i++)
        {
            var s = signers[i];
            var status = mode == SignRequestModes.Parallel || i == 0
                ? SignRequestSignerStatuses.Pending
                : SignRequestSignerStatuses.Waiting;
            var token = GenerateToken();
            signerRows.Add((Guid.NewGuid(), s.Email, s.Name, s.Order, status, token));
            await _guestProvisioning.EnsureGuestUserAsync(tenantId, s.Email, cancellationToken);
        }

        await using (var connection = new NpgsqlConnection(RequireConnectionString()))
        {
            await connection.OpenAsync(cancellationToken);
            await using var tx = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await using (var insert = new NpgsqlCommand("""
                    INSERT INTO repository."SignRequests"
                        ("Id", "TenantId", "RepositoryId", "ItemId", "FileName", "SigningMode", "Status", "Message",
                         "InitiatedByUserId", "InitiatedByEmail", "InitiatedByName", "ExpiresAtUtc", "CreatedAtUtc", "IsDeleted")
                    VALUES
                        (@Id, @TenantId, @RepositoryId, @ItemId, @FileName, @SigningMode, @Status, @Message,
                         @InitiatedByUserId, @InitiatedByEmail, @InitiatedByName, @ExpiresAtUtc, now(), false)
                    """, connection, tx))
                {
                    insert.Parameters.AddWithValue("@Id", requestId);
                    insert.Parameters.AddWithValue("@TenantId", tenantId);
                    insert.Parameters.AddWithValue("@RepositoryId", repositoryId);
                    insert.Parameters.AddWithValue("@ItemId", itemId);
                    insert.Parameters.AddWithValue("@FileName", (object?)fileName ?? DBNull.Value);
                    insert.Parameters.AddWithValue("@SigningMode", mode);
                    insert.Parameters.AddWithValue("@Status", SignRequestStatuses.InProgress);
                    insert.Parameters.AddWithValue("@Message", (object?)NullIfWhite(request.Message) ?? DBNull.Value);
                    insert.Parameters.AddWithValue("@InitiatedByUserId", initiatedByUserId);
                    insert.Parameters.AddWithValue("@InitiatedByEmail", senderEmail);
                    insert.Parameters.AddWithValue("@InitiatedByName", senderName);
                    insert.Parameters.AddWithValue("@ExpiresAtUtc", expires);
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }

                foreach (var row in signerRows)
                {
                    await using var insertSigner = new NpgsqlCommand("""
                        INSERT INTO repository."SignRequestSigners"
                            ("Id", "SignRequestId", "Email", "Name", "SortOrder", "Status", "InviteToken", "InvitedAtUtc", "CreatedAtUtc", "IsDeleted")
                        VALUES
                            (@Id, @SignRequestId, @Email, @Name, @SortOrder, @Status, @InviteToken,
                             CASE WHEN @Status = 'Pending' THEN now() ELSE NULL END,
                             now(), false)
                        """, connection, tx);
                    insertSigner.Parameters.AddWithValue("@Id", row.Id);
                    insertSigner.Parameters.AddWithValue("@SignRequestId", requestId);
                    insertSigner.Parameters.AddWithValue("@Email", row.Email);
                    insertSigner.Parameters.AddWithValue("@Name", (object?)row.Name ?? DBNull.Value);
                    insertSigner.Parameters.AddWithValue("@SortOrder", row.Order);
                    insertSigner.Parameters.AddWithValue("@Status", row.Status);
                    insertSigner.Parameters.AddWithValue("@InviteToken", row.Token);
                    await insertSigner.ExecuteNonQueryAsync(cancellationToken);
                }

                await tx.CommitAsync(cancellationToken);
            }
            catch
            {
                await tx.RollbackAsync(cancellationToken);
                throw;
            }
        }

        foreach (var row in signerRows)
            await IndexInviteTokenAsync(tenantId, row.Token, cancellationToken);

        foreach (var row in signerRows.Where(r => r.Status == SignRequestSignerStatuses.Pending))
        {
            // Same as file share: isnew=true → FE shows set-password; isnew=false → existing login.
            var isNew = await _guestProvisioning.RequiresPasswordSetupAsync(tenantId, row.Email, cancellationToken);
            var inviteUrl = BuildInviteUrl(row.Token, row.Email, isNew);
            await TrySendSignerInviteEmailAsync(
                row.Email,
                row.Name,
                senderName,
                senderEmail,
                orgName,
                fileName,
                mode,
                row.Order,
                signerRows.Count,
                inviteUrl,
                request.Message,
                cancellationToken);
        }

        await TrySendInitiatorEmailAsync(
            senderEmail,
            $"Sign request created: {fileName}",
            BuildInitiatorStatusEmail(
                "Your document was sent for signature",
                "You sent this document for signature and it is waiting for signers.",
                fileName,
                orgName,
                senderName,
                senderEmail,
                extraHtml: $"<p style=\"margin:16px 0 0;font-size:14px;color:#444;\">Mode: {WebUtility.HtmlEncode(mode)}. Signers: {WebUtility.HtmlEncode(string.Join(", ", signerRows.Select(s => s.Email)))}</p>"),
            cancellationToken);

        return (await GetAsync(tenantId, requestId, cancellationToken))!;
    }

    public async Task<SignRequestDto?> GetAsync(
        Guid tenantId,
        Guid signRequestId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureTenantConnectionAsync(tenantId, cancellationToken);
        return await LoadRequestAsync(signRequestId, tenantId, cancellationToken);
    }

    public async Task<IReadOnlyList<SignRequestDto>> ListForItemAsync(
        Guid tenantId,
        Guid repositoryId,
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureTenantConnectionAsync(tenantId, cancellationToken);

        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand("""
            SELECT "Id" FROM repository."SignRequests"
            WHERE "TenantId" = @TenantId AND "RepositoryId" = @RepositoryId AND "ItemId" = @ItemId AND "IsDeleted" = false
            ORDER BY "CreatedAtUtc" DESC
            """, connection);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        cmd.Parameters.AddWithValue("@RepositoryId", repositoryId);
        cmd.Parameters.AddWithValue("@ItemId", itemId);

        var ids = new List<Guid>();
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                ids.Add(reader.GetGuid(0));
        }

        var list = new List<SignRequestDto>();
        foreach (var id in ids)
        {
            var dto = await LoadRequestAsync(id, tenantId, cancellationToken);
            if (dto != null)
                list.Add(dto);
        }

        return list;
    }

    public async Task<IReadOnlyList<MyPendingSignRequestDto>> ListPendingForMeAsync(
        Guid tenantId,
        string signerEmail,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(signerEmail))
            return Array.Empty<MyPendingSignRequestDto>();

        await EnsureSchemaAsync(cancellationToken);
        await EnsureTenantConnectionAsync(tenantId, cancellationToken);

        var email = signerEmail.Trim().ToLowerInvariant();
        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand("""
            SELECT r."Id", r."RepositoryId", r."ItemId", r."FileName", r."SigningMode", r."Status",
                   s."Status", s."SortOrder", r."Message", r."InitiatedByName", r."InitiatedByEmail",
                   r."ExpiresAtUtc", r."CreatedAtUtc", s."InviteToken"
            FROM repository."SignRequestSigners" s
            INNER JOIN repository."SignRequests" r ON r."Id" = s."SignRequestId"
            WHERE r."TenantId" = @TenantId
              AND r."IsDeleted" = false
              AND s."IsDeleted" = false
              AND r."Status" = 'InProgress'
              AND r."ExpiresAtUtc" > now()
              AND LOWER(s."Email") = @Email
              AND s."Status" = 'Pending'
            ORDER BY r."CreatedAtUtc" DESC
            """, connection);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        cmd.Parameters.AddWithValue("@Email", email);

        var list = new List<MyPendingSignRequestDto>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new MyPendingSignRequestDto(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetDateTime(11),
                reader.GetDateTime(12),
                reader.GetString(13)));
        }

        return list;
    }

    public async Task<SignRequestInvitePreviewDto?> GetInvitePreviewAsync(
        string inviteToken,
        CancellationToken cancellationToken = default)
    {
        var ctx = await ResolveInviteAsync(inviteToken, cancellationToken);
        if (ctx == null)
            return null;

        await EnsureTenantConnectionAsync(ctx.TenantId, cancellationToken);
        var auth = await _guestProvisioning.GetShareInviteAuthInfoAsync(ctx.TenantId, ctx.SignerEmail, cancellationToken);
        var orgName = await GetTenantNameAsync(ctx.TenantId, cancellationToken);

        return new SignRequestInvitePreviewDto(
            ctx.InviteToken,
            ctx.SignRequestId,
            ctx.TenantId,
            ctx.RepositoryId,
            ctx.ItemId,
            ctx.FileName,
            orgName,
            ctx.InitiatedByName,
            ctx.InitiatedByEmail,
            ctx.SignerEmail,
            ctx.SigningMode,
            ctx.SortOrder,
            ctx.SignerCount,
            ctx.SignerStatus,
            ctx.RequestStatus,
            ctx.ExpiresAtUtc,
            RequiresLogin: true,
            auth.RequiresPasswordSetup,
            auth.RequiredSocialProvider,
            auth.AllowedAuthMethods,
            auth.LoginType,
            ctx.Message);
    }

    public async Task<RepositoryItemFileContent?> OpenInviteFileAsync(
        string inviteToken,
        string viewerEmail,
        CancellationToken cancellationToken = default)
    {
        var ctx = await ResolveInviteAsync(inviteToken, cancellationToken)
            ?? throw new UnauthorizedAccessException("Invalid or expired sign invite.");
        EnsureEmailMatches(ctx, viewerEmail);
        return await OpenFileCoreAsync(ctx, cancellationToken);
    }

    public async Task<RepositoryItemFileContent?> OpenFileForSignerAsync(
        Guid tenantId,
        Guid signRequestId,
        string viewerEmail,
        CancellationToken cancellationToken = default)
    {
        var ctx = await ResolveByRequestAndEmailAsync(tenantId, signRequestId, viewerEmail, cancellationToken)
            ?? throw new UnauthorizedAccessException("No pending sign request found for this user.");
        return await OpenFileCoreAsync(ctx, cancellationToken);
    }

    public async Task<SignRequestDto> SubmitSignatureAsync(
        string inviteToken,
        string signerEmail,
        Guid? signerUserId,
        SubmitSignRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var ctx = await ResolveInviteAsync(inviteToken, cancellationToken)
            ?? throw new UnauthorizedAccessException("Invalid or expired sign invite.");
        EnsureEmailMatches(ctx, signerEmail);
        return await SubmitSignatureCoreAsync(ctx, signerUserId, request, cancellationToken);
    }

    public async Task<SignRequestDto> SubmitSignatureForSignerAsync(
        Guid tenantId,
        Guid signRequestId,
        string signerEmail,
        Guid? signerUserId,
        SubmitSignRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var ctx = await ResolveByRequestAndEmailAsync(tenantId, signRequestId, signerEmail, cancellationToken)
            ?? throw new UnauthorizedAccessException("No pending sign request found for this user.");
        return await SubmitSignatureCoreAsync(ctx, signerUserId, request, cancellationToken);
    }

    public async Task<SignRequestDto> DeclineAsync(
        string inviteToken,
        string signerEmail,
        DeclineSignRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var ctx = await ResolveInviteAsync(inviteToken, cancellationToken)
            ?? throw new UnauthorizedAccessException("Invalid or expired sign invite.");
        EnsureEmailMatches(ctx, signerEmail);
        return await DeclineCoreAsync(ctx, request, cancellationToken);
    }

    public async Task<SignRequestDto> DeclineForSignerAsync(
        Guid tenantId,
        Guid signRequestId,
        string signerEmail,
        DeclineSignRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var ctx = await ResolveByRequestAndEmailAsync(tenantId, signRequestId, signerEmail, cancellationToken)
            ?? throw new UnauthorizedAccessException("No pending sign request found for this user.");
        return await DeclineCoreAsync(ctx, request, cancellationToken);
    }

    private static void EnsureEmailMatches(InviteContext ctx, string email)
    {
        if (!string.Equals(ctx.SignerEmail, email.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Email does not match this sign invite.");
    }

    private async Task<RepositoryItemFileContent?> OpenFileCoreAsync(
        InviteContext ctx,
        CancellationToken cancellationToken)
    {
        if (ctx.SignerStatus is not (SignRequestSignerStatuses.Pending or SignRequestSignerStatuses.Signed))
            throw new InvalidOperationException("This signer is not allowed to open the file yet.");

        await EnsureTenantConnectionAsync(ctx.TenantId, cancellationToken);
        return await _itemQuery.OpenItemFileAsync(ctx.RepositoryId, ctx.TenantId, ctx.ItemId, cancellationToken);
    }

    private async Task<SignRequestDto> SubmitSignatureCoreAsync(
        InviteContext ctx,
        Guid? signerUserId,
        SubmitSignRequestDto request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(ctx.RequestStatus, SignRequestStatuses.InProgress, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This sign request is no longer open.");

        if (!string.Equals(ctx.SignerStatus, SignRequestSignerStatuses.Pending, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This signer cannot sign in the current state.");

        await EnsureTenantConnectionAsync(ctx.TenantId, cancellationToken);

        var item = await _itemQuery.GetItemAsync(ctx.RepositoryId, ctx.TenantId, ctx.ItemId, cancellationToken)
            ?? throw new InvalidOperationException("Repository item not found.");
        if (string.IsNullOrWhiteSpace(item.FilePath))
            throw new InvalidOperationException("Item file path is missing.");

        var providerCode = string.IsNullOrWhiteSpace(item.StorageProviderCode) ? "EZOFIS" : item.StorageProviderCode;
        await using var source = await _fileStorage.OpenReadAsync(ctx.TenantId, item.FilePath, providerCode, cancellationToken);
        using var buffered = new MemoryStream();
        await source.CopyToAsync(buffered, cancellationToken);
        buffered.Position = 0;

        var imageBytes = PdfSignatureStampService.DecodeSignatureImage(request.SignatureImageBase64);
        var stamped = PdfSignatureStampService.StampSignature(
            buffered,
            request.PageNumber,
            request.X,
            request.Y,
            request.Width,
            request.Height,
            imageBytes);

        await using (var stampedStream = new MemoryStream(stamped))
        {
            await _fileStorage.SaveAsync(
                ctx.TenantId,
                ctx.RepositoryId,
                ctx.ItemId,
                item.FileName ?? ctx.FileName ?? "document.pdf",
                stampedStream,
                providerCode,
                storageRelativePath: item.FilePath,
                cancellationToken);
        }

        await UpdateItemFileSizeAsync(ctx.RepositoryId, ctx.ItemId, stamped.Length, signerUserId, cancellationToken);

        var metaJson = JsonSerializer.Serialize(new
        {
            pageNumber = request.PageNumber,
            x = request.X,
            y = request.Y,
            width = request.Width,
            height = request.Height,
            signedAtClientUtc = request.SignedAtClientUtc
        }, JsonOptions);

        Guid? nextSignerId = null;
        string? nextToken = null;
        string? nextEmail = null;
        string? nextName = null;
        var completed = false;

        await using (var connection = new NpgsqlConnection(RequireConnectionString()))
        {
            await connection.OpenAsync(cancellationToken);
            await using var tx = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await using (var updateSigner = new NpgsqlCommand("""
                    UPDATE repository."SignRequestSigners"
                    SET "Status" = 'Signed',
                        "SignedAtUtc" = now(),
                        "UserId" = COALESCE(@UserId, "UserId"),
                        "SignatureMetaJson" = @Meta,
                        "ModifiedAtUtc" = now()
                    WHERE "Id" = @SignerId AND "Status" = 'Pending'
                    """, connection, tx))
                {
                    updateSigner.Parameters.AddWithValue("@SignerId", ctx.SignerId);
                    updateSigner.Parameters.AddWithValue("@UserId", (object?)signerUserId ?? DBNull.Value);
                    updateSigner.Parameters.AddWithValue("@Meta", metaJson);
                    var affected = await updateSigner.ExecuteNonQueryAsync(cancellationToken);
                    if (affected == 0)
                        throw new InvalidOperationException("Signer was already updated by another request.");
                }

                if (string.Equals(ctx.SigningMode, SignRequestModes.Sequential, StringComparison.OrdinalIgnoreCase))
                {
                    await using var nextCmd = new NpgsqlCommand("""
                        SELECT "Id", "Email", "Name", "InviteToken"
                        FROM repository."SignRequestSigners"
                        WHERE "SignRequestId" = @RequestId AND "IsDeleted" = false AND "Status" = 'Waiting'
                        ORDER BY "SortOrder", "CreatedAtUtc"
                        LIMIT 1
                        """, connection, tx);
                    nextCmd.Parameters.AddWithValue("@RequestId", ctx.SignRequestId);
                    await using var reader = await nextCmd.ExecuteReaderAsync(cancellationToken);
                    if (await reader.ReadAsync(cancellationToken))
                    {
                        nextSignerId = reader.GetGuid(0);
                        nextEmail = reader.GetString(1);
                        nextName = reader.IsDBNull(2) ? null : reader.GetString(2);
                        nextToken = reader.GetString(3);
                    }
                }

                if (nextSignerId is Guid nid)
                {
                    await using var activate = new NpgsqlCommand("""
                        UPDATE repository."SignRequestSigners"
                        SET "Status" = 'Pending', "InvitedAtUtc" = now(), "ModifiedAtUtc" = now()
                        WHERE "Id" = @Id
                        """, connection, tx);
                    activate.Parameters.AddWithValue("@Id", nid);
                    await activate.ExecuteNonQueryAsync(cancellationToken);
                }
                else
                {
                    await using var pendingCount = new NpgsqlCommand("""
                        SELECT COUNT(1) FROM repository."SignRequestSigners"
                        WHERE "SignRequestId" = @RequestId AND "IsDeleted" = false
                          AND "Status" IN ('Pending', 'Waiting')
                        """, connection, tx);
                    pendingCount.Parameters.AddWithValue("@RequestId", ctx.SignRequestId);
                    var remaining = Convert.ToInt32(await pendingCount.ExecuteScalarAsync(cancellationToken));
                    if (remaining == 0)
                    {
                        await using var complete = new NpgsqlCommand("""
                            UPDATE repository."SignRequests"
                            SET "Status" = 'Completed', "CompletedAtUtc" = now(), "ModifiedAtUtc" = now()
                            WHERE "Id" = @Id
                            """, connection, tx);
                        complete.Parameters.AddWithValue("@Id", ctx.SignRequestId);
                        await complete.ExecuteNonQueryAsync(cancellationToken);
                        completed = true;
                    }
                }

                await tx.CommitAsync(cancellationToken);
            }
            catch
            {
                await tx.RollbackAsync(cancellationToken);
                throw;
            }
        }

        var orgName = await GetTenantNameAsync(ctx.TenantId, cancellationToken);
        var fileLabel = ctx.FileName ?? "document.pdf";
        await TrySendInitiatorEmailAsync(
            ctx.InitiatedByEmail,
            $"Signed: {fileLabel}",
            BuildInitiatorStatusEmail(
                "A signer completed their signature",
                $"<strong>{WebUtility.HtmlEncode(ctx.SignerEmail)}</strong> signed this document.",
                fileLabel,
                orgName,
                ctx.InitiatedByName,
                ctx.InitiatedByEmail),
            cancellationToken);

        if (nextToken != null && nextEmail != null)
        {
            var isNew = await _guestProvisioning.RequiresPasswordSetupAsync(ctx.TenantId, nextEmail, cancellationToken);
            var inviteUrl = BuildInviteUrl(nextToken, nextEmail, isNew);
            await TrySendSignerInviteEmailAsync(
                nextEmail,
                nextName,
                ctx.InitiatedByName,
                ctx.InitiatedByEmail,
                orgName,
                fileLabel,
                ctx.SigningMode,
                ctx.SortOrder + 1,
                ctx.SignerCount,
                inviteUrl,
                ctx.Message,
                cancellationToken);
        }

        if (completed)
        {
            await TrySendInitiatorEmailAsync(
                ctx.InitiatedByEmail,
                $"Your document has been completed: {fileLabel}",
                BuildCompletedEmailBody(fileLabel, orgName, ctx.InitiatedByName, ctx.InitiatedByEmail),
                cancellationToken);
        }

        return (await LoadRequestAsync(ctx.SignRequestId, ctx.TenantId, cancellationToken))!;
    }

    private async Task<SignRequestDto> DeclineCoreAsync(
        InviteContext ctx,
        DeclineSignRequestDto request,
        CancellationToken cancellationToken)
    {
        await EnsureTenantConnectionAsync(ctx.TenantId, cancellationToken);

        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand("""
            UPDATE repository."SignRequestSigners"
            SET "Status" = 'Declined',
                "DeclineReason" = @Reason,
                "ModifiedAtUtc" = now()
            WHERE "Id" = @Id AND "Status" = 'Pending'
            """, connection);
        cmd.Parameters.AddWithValue("@Id", ctx.SignerId);
        cmd.Parameters.AddWithValue("@Reason", (object?)NullIfWhite(request.Reason) ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        await using var cancel = new NpgsqlCommand("""
            UPDATE repository."SignRequests"
            SET "Status" = 'Cancelled', "ModifiedAtUtc" = now()
            WHERE "Id" = @Id AND "Status" = 'InProgress'
            """, connection);
        cancel.Parameters.AddWithValue("@Id", ctx.SignRequestId);
        await cancel.ExecuteNonQueryAsync(cancellationToken);

        var orgName = await GetTenantNameAsync(ctx.TenantId, cancellationToken);
        var fileLabel = ctx.FileName ?? "document.pdf";
        var reasonHtml = string.IsNullOrWhiteSpace(request.Reason)
            ? ""
            : $"<p style=\"margin:16px 0 0;font-size:14px;color:#444;\">Reason: {WebUtility.HtmlEncode(request.Reason)}</p>";
        await TrySendInitiatorEmailAsync(
            ctx.InitiatedByEmail,
            $"Sign declined: {fileLabel}",
            BuildInitiatorStatusEmail(
                "A signer declined this document",
                $"<strong>{WebUtility.HtmlEncode(ctx.SignerEmail)}</strong> declined to sign this document.",
                fileLabel,
                orgName,
                ctx.InitiatedByName,
                ctx.InitiatedByEmail,
                extraHtml: reasonHtml),
            cancellationToken);

        return (await LoadRequestAsync(ctx.SignRequestId, ctx.TenantId, cancellationToken))!;
    }

    public async Task<SignRequestDto> CancelAsync(
        Guid tenantId,
        Guid signRequestId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureTenantConnectionAsync(tenantId, cancellationToken);

        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand("""
            UPDATE repository."SignRequests"
            SET "Status" = 'Cancelled', "ModifiedAtUtc" = now()
            WHERE "Id" = @Id AND "TenantId" = @TenantId AND "InitiatedByUserId" = @UserId AND "Status" = 'InProgress'
            """, connection);
        cmd.Parameters.AddWithValue("@Id", signRequestId);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        cmd.Parameters.AddWithValue("@UserId", userId);
        var affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
        if (affected == 0)
            throw new InvalidOperationException("Sign request not found or cannot be cancelled.");

        return (await LoadRequestAsync(signRequestId, tenantId, cancellationToken))!;
    }

    private async Task UpdateItemFileSizeAsync(
        Guid repositoryId,
        Guid itemId,
        long fileSize,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(cancellationToken);

        string? itemsTable = null;
        await using (var find = new NpgsqlCommand("""
            SELECT "ItemsTableName" FROM repository."Repositories" WHERE "Id" = @Id AND "IsDeleted" = false
            """, connection))
        {
            find.Parameters.AddWithValue("@Id", repositoryId);
            itemsTable = (await find.ExecuteScalarAsync(cancellationToken)) as string;
        }

        if (string.IsNullOrWhiteSpace(itemsTable) || !RepositorySqlHelper.IsValidItemsTableName(itemsTable))
            return;

        var sql = $"""
            UPDATE repository.{itemsTable}
            SET file_size = @FileSize,
                modified_at_utc = now(),
                modified_by = COALESCE(@UserId, modified_by)
            WHERE id = @ItemId AND is_deleted = false
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@FileSize", fileSize);
        cmd.Parameters.AddWithValue("@UserId", (object?)userId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ItemId", itemId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SignRequestDto?> LoadRequestAsync(
        Guid signRequestId,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand("""
            SELECT "Id", "RepositoryId", "ItemId", "FileName", "SigningMode", "Status", "Message",
                   "InitiatedByUserId", "InitiatedByEmail", "InitiatedByName", "ExpiresAtUtc", "CreatedAtUtc", "CompletedAtUtc"
            FROM repository."SignRequests"
            WHERE "Id" = @Id AND "TenantId" = @TenantId AND "IsDeleted" = false
            """, connection);
        cmd.Parameters.AddWithValue("@Id", signRequestId);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);

        Guid repositoryId;
        Guid itemId;
        string? fileName;
        string mode;
        string status;
        string? message;
        Guid initiatedBy;
        string initiatedEmail;
        string initiatedName;
        DateTime expires;
        DateTime created;
        DateTime? completed;

        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            repositoryId = reader.GetGuid(1);
            itemId = reader.GetGuid(2);
            fileName = reader.IsDBNull(3) ? null : reader.GetString(3);
            mode = reader.GetString(4);
            status = reader.GetString(5);
            message = reader.IsDBNull(6) ? null : reader.GetString(6);
            initiatedBy = reader.GetGuid(7);
            initiatedEmail = reader.GetString(8);
            initiatedName = reader.GetString(9);
            expires = reader.GetDateTime(10);
            created = reader.GetDateTime(11);
            completed = reader.IsDBNull(12) ? null : reader.GetDateTime(12);
        }

        var signerRows = new List<(Guid Id, string Email, string? Name, int Order, string Status, string Token, DateTime? InvitedAt, DateTime? SignedAt)>();
        await using (var sCmd = new NpgsqlCommand("""
            SELECT "Id", "Email", "Name", "SortOrder", "Status", "InviteToken", "InvitedAtUtc", "SignedAtUtc"
            FROM repository."SignRequestSigners"
            WHERE "SignRequestId" = @RequestId AND "IsDeleted" = false
            ORDER BY "SortOrder", "CreatedAtUtc"
            """, connection))
        {
            sCmd.Parameters.AddWithValue("@RequestId", signRequestId);
            await using var reader = await sCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                signerRows.Add((
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetDateTime(6),
                    reader.IsDBNull(7) ? null : reader.GetDateTime(7)));
            }
        }

        var signers = new List<SignRequestSignerDto>();
        foreach (var row in signerRows)
        {
            string? inviteUrl = null;
            if (string.Equals(row.Status, SignRequestSignerStatuses.Pending, StringComparison.OrdinalIgnoreCase)
                || string.Equals(row.Status, SignRequestSignerStatuses.Signed, StringComparison.OrdinalIgnoreCase))
            {
                var isNew = await _guestProvisioning.RequiresPasswordSetupAsync(tenantId, row.Email, cancellationToken);
                inviteUrl = BuildInviteUrl(row.Token, row.Email, isNew);
            }

            signers.Add(new SignRequestSignerDto(
                row.Id,
                row.Email,
                row.Name,
                row.Order,
                row.Status,
                inviteUrl,
                row.InvitedAt,
                row.SignedAt));
        }

        return new SignRequestDto(
            signRequestId,
            repositoryId,
            itemId,
            fileName,
            mode,
            status,
            message,
            initiatedBy,
            initiatedEmail,
            initiatedName,
            expires,
            created,
            completed,
            signers);
    }

    private sealed record InviteContext(
        Guid SignerId,
        string InviteToken,
        Guid SignRequestId,
        Guid TenantId,
        Guid RepositoryId,
        Guid ItemId,
        string? FileName,
        string SigningMode,
        string RequestStatus,
        string SignerStatus,
        string SignerEmail,
        int SortOrder,
        int SignerCount,
        string InitiatedByEmail,
        string InitiatedByName,
        string? Message,
        DateTime ExpiresAtUtc);

    private async Task<InviteContext?> ResolveByRequestAndEmailAsync(
        Guid tenantId,
        Guid signRequestId,
        string signerEmail,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(signerEmail))
            return null;

        await EnsureTenantConnectionAsync(tenantId, cancellationToken);
        await EnsureSchemaAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(RequireConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var load = new NpgsqlCommand("""
            SELECT s."Id", s."InviteToken", r."Id", r."TenantId", r."RepositoryId", r."ItemId", r."FileName", r."SigningMode", r."Status",
                   s."Status", s."Email", s."SortOrder",
                   (SELECT COUNT(1) FROM repository."SignRequestSigners" x WHERE x."SignRequestId" = r."Id" AND x."IsDeleted" = false),
                   r."InitiatedByEmail", r."InitiatedByName", r."Message", r."ExpiresAtUtc"
            FROM repository."SignRequestSigners" s
            INNER JOIN repository."SignRequests" r ON r."Id" = s."SignRequestId"
            WHERE r."Id" = @RequestId
              AND r."TenantId" = @TenantId
              AND s."IsDeleted" = false AND r."IsDeleted" = false
              AND LOWER(s."Email") = @Email
            """, connection);
        load.Parameters.AddWithValue("@RequestId", signRequestId);
        load.Parameters.AddWithValue("@TenantId", tenantId);
        load.Parameters.AddWithValue("@Email", signerEmail.Trim().ToLowerInvariant());
        await using var reader = await load.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var expires = reader.GetDateTime(16);
        if (expires <= DateTime.UtcNow)
            return null;

        return new InviteContext(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetGuid(2),
            reader.GetGuid(3),
            reader.GetGuid(4),
            reader.GetGuid(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetInt32(11),
            reader.GetInt32(12),
            reader.GetString(13),
            reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            expires);
    }

    private async Task<InviteContext?> ResolveInviteAsync(string inviteToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(inviteToken))
            return null;

        await EnsureCatalogInviteIndexAsync(cancellationToken);

        Guid? tenantId = null;
        await using (var catalog = await _catalogFactory.CreateDbContextAsync(cancellationToken))
        {
            var cs = catalog.Database.GetConnectionString()
                ?? throw new InvalidOperationException("Catalog connection string is not configured.");
            await using var connection = new NpgsqlConnection(cs);
            await connection.OpenAsync(cancellationToken);
            await using var cmd = new NpgsqlCommand("""
                SELECT "TenantId" FROM catalog."SignRequestInviteIndex"
                WHERE "InviteToken" = @Token
                """, connection);
            cmd.Parameters.AddWithValue("@Token", inviteToken.Trim());
            var scalar = await cmd.ExecuteScalarAsync(cancellationToken);
            if (scalar is Guid tid)
                tenantId = tid;
            else if (scalar != null && Guid.TryParse(scalar.ToString(), out var parsed))
                tenantId = parsed;
        }

        if (tenantId is null)
            return null;

        await EnsureTenantConnectionAsync(tenantId.Value, cancellationToken);
        await EnsureSchemaAsync(cancellationToken);

        await using var tenantConn = new NpgsqlConnection(RequireConnectionString());
        await tenantConn.OpenAsync(cancellationToken);
        await using var load = new NpgsqlCommand("""
            SELECT s."Id", s."InviteToken", r."Id", r."TenantId", r."RepositoryId", r."ItemId", r."FileName", r."SigningMode", r."Status",
                   s."Status", s."Email", s."SortOrder",
                   (SELECT COUNT(1) FROM repository."SignRequestSigners" x WHERE x."SignRequestId" = r."Id" AND x."IsDeleted" = false),
                   r."InitiatedByEmail", r."InitiatedByName", r."Message", r."ExpiresAtUtc"
            FROM repository."SignRequestSigners" s
            INNER JOIN repository."SignRequests" r ON r."Id" = s."SignRequestId"
            WHERE s."InviteToken" = @Token AND s."IsDeleted" = false AND r."IsDeleted" = false
            """, tenantConn);
        load.Parameters.AddWithValue("@Token", inviteToken.Trim());
        await using var reader = await load.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var expires = reader.GetDateTime(16);
        if (expires <= DateTime.UtcNow)
            return null;

        return new InviteContext(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetGuid(2),
            reader.GetGuid(3),
            reader.GetGuid(4),
            reader.GetGuid(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetInt32(11),
            reader.GetInt32(12),
            reader.GetString(13),
            reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            expires);
    }

    private async Task IndexInviteTokenAsync(Guid tenantId, string inviteToken, CancellationToken cancellationToken)
    {
        await EnsureCatalogInviteIndexAsync(cancellationToken);
        await using var catalog = await _catalogFactory.CreateDbContextAsync(cancellationToken);
        var cs = catalog.Database.GetConnectionString()!;
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(cancellationToken);
        // InviteToken is the primary key -- ON CONFLICT DO NOTHING is the direct equivalent of
        // the original's IF NOT EXISTS guard.
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO catalog."SignRequestInviteIndex" ("InviteToken", "TenantId", "CreatedAtUtc")
            VALUES (@Token, @TenantId, now())
            ON CONFLICT ("InviteToken") DO NOTHING
            """, connection);
        cmd.Parameters.AddWithValue("@Token", inviteToken);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureCatalogInviteIndexAsync(CancellationToken cancellationToken)
    {
        await using var catalog = await _catalogFactory.CreateDbContextAsync(cancellationToken);
        var cs = catalog.Database.GetConnectionString()
            ?? throw new InvalidOperationException("Catalog connection string is not configured.");
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand("""
            CREATE SCHEMA IF NOT EXISTS catalog;
            CREATE TABLE IF NOT EXISTS catalog."SignRequestInviteIndex" (
                "InviteToken" varchar(128) NOT NULL CONSTRAINT "PK_SignRequestInviteIndex" PRIMARY KEY,
                "TenantId" uuid NOT NULL,
                "CreatedAtUtc" timestamptz NOT NULL CONSTRAINT "DF_SignRequestInviteIndex_CreatedAt" DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS "IX_SignRequestInviteIndex_Tenant" ON catalog."SignRequestInviteIndex" ("TenantId");
            """, connection);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureTenantConnectionAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var cs = await _connectionResolver.GetConnectionStringAsync(tenantId, cancellationToken);
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("Tenant connection string not resolved.");
        _connectionProvider.SetConnectionString(cs);
    }

    private string RequireConnectionString() =>
        _connectionProvider.ConnectionString
        ?? throw new InvalidOperationException("Tenant connection string not resolved.");

    /// <summary>
    /// Same pattern as file share: <c>isnew=true</c> → set password; <c>isnew=false</c> → existing login.
    /// </summary>
    private string BuildInviteUrl(string inviteToken, string email, bool isNew)
    {
        var baseUrl = (_options.FrontendBaseUrl ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
            baseUrl = "https://demoapp.ezofis.com";

        var path = string.IsNullOrWhiteSpace(_options.SignRequestPath) ? "/sign-request" : _options.SignRequestPath.Trim();
        if (!path.StartsWith('/'))
            path = "/" + path;

        var isNewQuery = isNew ? "true" : "false";
        return $"{baseUrl}{path}/{Uri.EscapeDataString(inviteToken)}?email={Uri.EscapeDataString(email)}&isnew={isNewQuery}";
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string NormalizeMode(string? mode) =>
        string.Equals(mode, SignRequestModes.Parallel, StringComparison.OrdinalIgnoreCase)
            ? SignRequestModes.Parallel
            : SignRequestModes.Sequential;

    private static List<CreateSignRequestSignerDto> NormalizeSigners(IReadOnlyList<CreateSignRequestSignerDto>? signers)
    {
        var list = (signers ?? Array.Empty<CreateSignRequestSignerDto>())
            .Where(s => !string.IsNullOrWhiteSpace(s.Email) && s.Email.Contains('@'))
            .Select(s => new CreateSignRequestSignerDto(
                s.Email.Trim().ToLowerInvariant(),
                string.IsNullOrWhiteSpace(s.Name) ? null : s.Name.Trim(),
                s.Order <= 0 ? 0 : s.Order))
            .GroupBy(s => s.Email, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        // Assign order if missing / zero
        var ordered = list.OrderBy(s => s.Order <= 0 ? int.MaxValue : s.Order).ThenBy(s => s.Email).ToList();
        var result = new List<CreateSignRequestSignerDto>();
        for (var i = 0; i < ordered.Count; i++)
            result.Add(ordered[i] with { Order = i + 1 });
        return result;
    }

    private static string? NullIfWhite(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<string?> GetTenantNameAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var catalog = await _catalogFactory.CreateDbContextAsync(cancellationToken);
        return await catalog.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => t.Name)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task TrySendSignerInviteEmailAsync(
        string recipientEmail,
        string? recipientName,
        string senderName,
        string senderEmail,
        string? orgName,
        string fileName,
        string mode,
        int order,
        int total,
        string inviteUrl,
        string? message,
        CancellationToken cancellationToken)
    {
        var subject = $"{_options.EmailSubjectPrefix}: {fileName}";
        var company = orgName ?? "ezofis";
        var messageBlock = string.IsNullOrWhiteSpace(message)
            ? ""
            : $"<p style=\"margin:16px 0 0;font-size:14px;color:#444;\">{WebUtility.HtmlEncode(message)}</p>";
        var body = WrapBrandedEmail($"""
            <h1 style="margin:0 0 12px;font-size:22px;font-weight:700;color:#1a1a1a;line-height:1.3;">Do you recognize this document?</h1>
            <p style="margin:0 0 20px;font-size:15px;line-height:1.5;color:#333;">We have received a request to send you a document for signature{(string.IsNullOrWhiteSpace(recipientName) ? "" : $", {WebUtility.HtmlEncode(recipientName)}")}.</p>
            <p style="margin:0 0 8px;font-size:15px;color:#333;">Take a moment to verify the following details</p>
            {BuildDetailsList(fileName, company, senderName, senderEmail)}
            <p style="margin:12px 0 0;font-size:14px;color:#555;">Your role: Signer {order} of {total} ({WebUtility.HtmlEncode(mode)})</p>
            {messageBlock}
            <p style="margin:24px 0 12px;font-size:15px;color:#333;">Select <strong>Continue</strong> if you recognize this request.</p>
            <p style="margin:0 0 16px;">
              <a href="{WebUtility.HtmlEncode(inviteUrl)}" style="display:inline-block;padding:12px 22px;background:#111111;color:#ffffff;text-decoration:none;border-radius:4px;font-size:14px;font-weight:600;">Continue</a>
            </p>
            <p style="margin:0 0 24px;word-break:break-all;color:#777;font-size:12px;">{WebUtility.HtmlEncode(inviteUrl)}</p>
            {BuildSecurityFooter(isInitiator: false)}
            """);
        await TrySendEmailAsync(recipientEmail, subject, body, cancellationToken);
    }

    private async Task TrySendInitiatorEmailAsync(
        string recipientEmail,
        string subject,
        string htmlBody,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(recipientEmail) || !recipientEmail.Contains('@'))
            return;
        await TrySendEmailAsync(recipientEmail, subject, htmlBody, cancellationToken);
    }

    private string BuildCompletedEmailBody(
        string fileName,
        string? orgName,
        string senderName,
        string senderEmail) =>
        WrapBrandedEmail($"""
            <h1 style="margin:0 0 12px;font-size:22px;font-weight:700;color:#1a1a1a;line-height:1.3;">Your document has been completed</h1>
            <p style="margin:0 0 20px;font-size:15px;line-height:1.5;color:#333;">You sent this document for signature and it is now completed</p>
            <p style="margin:0 0 8px;font-size:15px;color:#333;">Take a moment to verify the following details</p>
            {BuildDetailsList(fileName, orgName ?? "ezofis", senderName, senderEmail)}
            <p style="margin:20px 0 0;font-size:14px;color:#444;">The signed file is saved in the repository (same name and path).</p>
            {BuildSecurityFooter(isInitiator: true)}
            """);

    private string BuildInitiatorStatusEmail(
        string heading,
        string introHtml,
        string fileName,
        string? orgName,
        string senderName,
        string senderEmail,
        string? extraHtml = null) =>
        WrapBrandedEmail($"""
            <h1 style="margin:0 0 12px;font-size:22px;font-weight:700;color:#1a1a1a;line-height:1.3;">{WebUtility.HtmlEncode(heading)}</h1>
            <p style="margin:0 0 20px;font-size:15px;line-height:1.5;color:#333;">{introHtml}</p>
            <p style="margin:0 0 8px;font-size:15px;color:#333;">Take a moment to verify the following details</p>
            {BuildDetailsList(fileName, orgName ?? "ezofis", senderName, senderEmail)}
            {extraHtml ?? ""}
            {BuildSecurityFooter(isInitiator: true)}
            """);

    private static string BuildDetailsList(
        string fileName,
        string company,
        string senderName,
        string senderEmail)
    {
        var email = WebUtility.HtmlEncode(senderEmail);
        return $"""
            <ul style="margin:0 0 8px;padding-left:20px;font-size:15px;line-height:1.8;color:#222;">
              <li><strong>Document name:</strong> {WebUtility.HtmlEncode(fileName)}</li>
              <li><strong>Company:</strong> {WebUtility.HtmlEncode(company)}</li>
              <li><strong>Sender:</strong> {WebUtility.HtmlEncode(senderName)}</li>
              <li><strong>Sender Email:</strong> <a href="mailto:{email}" style="color:#0b57d0;text-decoration:underline;">{email}</a></li>
            </ul>
            """;
    }

    private string BuildSecurityFooter(bool isInitiator)
    {
        var support = WebUtility.HtmlEncode(_options.SupportEmail);
        var concern = isInitiator
            ? $"If you did not send this document or have any concerns, reply to this email or contact <a href=\"mailto:{support}\" style=\"color:#0b57d0;text-decoration:underline;\">{support}</a>."
            : $"If you do not recognize this request or have any concerns, do not continue and contact <a href=\"mailto:{support}\" style=\"color:#0b57d0;text-decoration:underline;\">{support}</a>.";
        return $"""
            <p style="margin:28px 0 8px;font-size:13px;line-height:1.5;color:#555;">{concern}</p>
            <p style="margin:0;font-size:13px;line-height:1.5;color:#555;">Your security is our top priority and we appreciate your attention to this matter.</p>
            """;
    }

    private string WrapBrandedEmail(string innerHtml) =>
        $"""
        <!DOCTYPE html>
        <html>
        <body style="margin:0;padding:0;background:#ffffff;font-family:Arial,Helvetica,sans-serif;color:#222222;">
          <div style="max-width:560px;margin:0 auto;padding:28px 20px;">
            <div style="margin-bottom:28px;">
              <img src="cid:{LogoContentId}" alt="ezofis" width="80" style="display:block;border:0;outline:none;text-decoration:none;height:auto;" />
            </div>
            {innerHtml}
          </div>
        </body>
        </html>
        """;

    private const string LogoContentId = "ezofis-logo";

    private string? ResolveEmailLogoPath()
    {
        if (!string.IsNullOrWhiteSpace(_options.EmailLogoPath) && File.Exists(_options.EmailLogoPath))
            return _options.EmailLogoPath;

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "ezofis-logo-mark.png"),
            Path.Combine(AppContext.BaseDirectory, "wwwroot", "branding", "ezofis-logo-mark.png"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private async Task TrySendEmailAsync(
        string recipientEmail,
        string subject,
        string htmlBody,
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
                _logger.LogWarning("Sign-request email not sent: mailsettings not configured.");
                return;
            }

            using var mail = new MailMessage
            {
                From = new MailAddress(settings.EmailId),
                Subject = subject,
                IsBodyHtml = true
            };
            mail.To.Add(recipientEmail);

            var logoPath = ResolveEmailLogoPath();
            if (logoPath != null)
            {
                var htmlView = AlternateView.CreateAlternateViewFromString(htmlBody, null, "text/html");
                var logo = new LinkedResource(logoPath, "image/png")
                {
                    ContentId = LogoContentId,
                    TransferEncoding = System.Net.Mime.TransferEncoding.Base64
                };
                htmlView.LinkedResources.Add(logo);
                mail.AlternateViews.Add(htmlView);
            }
            else
            {
                _logger.LogWarning("Sign-request email logo not found; sending without logo.");
                mail.Body = htmlBody.Replace($"cid:{LogoContentId}", "", StringComparison.Ordinal);
            }

            using var smtp = new SmtpClient(settings.OutgoingServer, settings.OutgoingPort)
            {
                EnableSsl = true,
                Credentials = new NetworkCredential(settings.EmailId, settings.Password)
            };
            await smtp.SendMailAsync(mail, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send sign-request email to {Email}", recipientEmail);
        }
    }

    // Matches scripts/postgres/CreateRepositorySchema.sql's repository schema conventions
    // (PascalCase quoted). These two tables have no static-script counterpart on SQL Server
    // either -- only ever created via this inline path.
    private const string EnsureSchemaSql = """
        CREATE TABLE IF NOT EXISTS repository."SignRequests" (
            "Id" uuid NOT NULL CONSTRAINT "PK_SignRequests" PRIMARY KEY,
            "TenantId" uuid NOT NULL,
            "RepositoryId" uuid NOT NULL,
            "ItemId" uuid NOT NULL,
            "FileName" varchar(512) NULL,
            "SigningMode" varchar(32) NOT NULL,
            "Status" varchar(32) NOT NULL,
            "Message" varchar(2000) NULL,
            "InitiatedByUserId" uuid NOT NULL,
            "InitiatedByEmail" varchar(256) NOT NULL,
            "InitiatedByName" varchar(256) NOT NULL,
            "ExpiresAtUtc" timestamptz NOT NULL,
            "CreatedAtUtc" timestamptz NOT NULL CONSTRAINT "DF_SignRequests_CreatedAtUtc" DEFAULT now(),
            "ModifiedAtUtc" timestamptz NULL,
            "CompletedAtUtc" timestamptz NULL,
            "IsDeleted" boolean NOT NULL CONSTRAINT "DF_SignRequests_IsDeleted" DEFAULT false
        );
        CREATE INDEX IF NOT EXISTS "IX_SignRequests_Item" ON repository."SignRequests" ("TenantId", "RepositoryId", "ItemId", "IsDeleted");

        CREATE TABLE IF NOT EXISTS repository."SignRequestSigners" (
            "Id" uuid NOT NULL CONSTRAINT "PK_SignRequestSigners" PRIMARY KEY,
            "SignRequestId" uuid NOT NULL,
            "Email" varchar(256) NOT NULL,
            "Name" varchar(256) NULL,
            "UserId" uuid NULL,
            "SortOrder" integer NOT NULL,
            "Status" varchar(32) NOT NULL,
            "InviteToken" varchar(128) NOT NULL,
            "InvitedAtUtc" timestamptz NULL,
            "SignedAtUtc" timestamptz NULL,
            "SignatureMetaJson" text NULL,
            "DeclineReason" varchar(1000) NULL,
            "CreatedAtUtc" timestamptz NOT NULL CONSTRAINT "DF_SignRequestSigners_CreatedAtUtc" DEFAULT now(),
            "ModifiedAtUtc" timestamptz NULL,
            "IsDeleted" boolean NOT NULL CONSTRAINT "DF_SignRequestSigners_IsDeleted" DEFAULT false,
            CONSTRAINT "FK_SignRequestSigners_Request" FOREIGN KEY ("SignRequestId")
                REFERENCES repository."SignRequests" ("Id") ON DELETE CASCADE
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_SignRequestSigners_Token" ON repository."SignRequestSigners" ("InviteToken");
        CREATE INDEX IF NOT EXISTS "IX_SignRequestSigners_Request" ON repository."SignRequestSigners" ("SignRequestId", "SortOrder");
        """;
}
