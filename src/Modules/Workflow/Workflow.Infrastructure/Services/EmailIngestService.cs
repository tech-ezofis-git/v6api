using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediatR;
using Npgsql;
using Microsoft.Extensions.Logging;
using SaaSApp.MultiTenancy;
using SaaSApp.Workflow.Application.Connectors;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Application.Workflows.Commands.StartWorkflow;
using SaaSApp.Workflow.Infrastructure.Jobs;

namespace SaaSApp.Workflow.Infrastructure.Services;

public sealed class EmailIngestService : IEmailIngestService
{
    private const string DefaultExtensions = ".pdf,.tif,.tiff";
    /// <summary>Sentinel AttachmentId meaning the whole message was handled (do not poll again).</summary>
    private const string MessageHandledSentinel = "__message_handled__";

    /// <summary>Image types often used in email signatures — only used if no document attachment exists.</summary>
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"
    };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".tif", ".tiff", ".doc", ".docx", ".xls", ".xlsx"
    };
    private static readonly Guid SystemUserId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly ITenantContext _tenantContext;
    private readonly ITenantConnectionProvider _connectionProvider;
    private readonly ICurrentUserProvider _currentUserProvider;
    private readonly IConnectorService _connectorService;
    private readonly IConnectorOAuthService _oauthService;
    private readonly IMediator _mediator;
    private readonly ILogger<EmailIngestService> _logger;

    public EmailIngestService(
        ITenantContext tenantContext,
        ITenantConnectionProvider connectionProvider,
        ICurrentUserProvider currentUserProvider,
        IConnectorService connectorService,
        IConnectorOAuthService oauthService,
        IMediator mediator,
        ILogger<EmailIngestService> logger)
    {
        _tenantContext = tenantContext;
        _connectionProvider = connectionProvider;
        _currentUserProvider = currentUserProvider;
        _connectorService = connectorService;
        _oauthService = oauthService;
        _mediator = mediator;
        _logger = logger;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        var cs = RequireConnectionString();
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(cancellationToken);

        // Tables already ported at their final shape (ProviderMessageId varchar(450)) in
        // scripts/postgres/Create-EmailIngest-Tables.sql, so unlike the SQL Server original
        // there's no ALTER-widen-if-too-narrow branch needed here -- a fresh Postgres table
        // never starts undersized.
        const string sql = """
            CREATE SCHEMA IF NOT EXISTS dbo;

            CREATE TABLE IF NOT EXISTS dbo."EmailIngestMailbox" (
                "Id" uuid NOT NULL CONSTRAINT "PK_EmailIngestMailbox" PRIMARY KEY,
                "ConnectorId" uuid NOT NULL,
                "WorkflowId" uuid NOT NULL,
                "IsEnabled" boolean NOT NULL DEFAULT true,
                "PollIntervalMinutes" integer NOT NULL DEFAULT 5,
                "QueryFilter" varchar(512) NULL,
                "MasterSource" varchar(32) NOT NULL DEFAULT 'InternalForm',
                "MasterFormId" varchar(128) NULL,
                "MasterConnectorId" uuid NULL,
                "AttachmentExtensions" varchar(256) NOT NULL DEFAULT '.pdf,.tif,.tiff',
                "LastPolledAtUtc" timestamptz NULL,
                "LastError" varchar(2000) NULL,
                "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
                "ModifiedAtUtc" timestamptz NULL,
                "CreatedBy" uuid NULL,
                "ModifiedBy" uuid NULL,
                "IsDeleted" boolean NOT NULL DEFAULT false
            );
            CREATE INDEX IF NOT EXISTS "IX_EmailIngestMailbox_Enabled" ON dbo."EmailIngestMailbox" ("IsEnabled", "IsDeleted") WHERE "IsDeleted" = false;

            CREATE TABLE IF NOT EXISTS dbo."EmailIngestProcessed" (
                "Id" uuid NOT NULL CONSTRAINT "PK_EmailIngestProcessed" PRIMARY KEY,
                "MailboxId" uuid NOT NULL,
                "ProviderMessageId" varchar(450) NOT NULL,
                "AttachmentId" varchar(256) NOT NULL,
                "WorkflowInstanceId" uuid NULL,
                "ProcessedAtUtc" timestamptz NOT NULL DEFAULT now(),
                CONSTRAINT "UQ_EmailIngestProcessed" UNIQUE ("MailboxId", "ProviderMessageId", "AttachmentId")
            );
            CREATE INDEX IF NOT EXISTS "IX_EmailIngestProcessed_Mailbox" ON dbo."EmailIngestProcessed" ("MailboxId", "ProcessedAtUtc" DESC);
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EmailIngestMailboxDto>> ListMailboxesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var cs = RequireConnectionString();
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT m."Id", m."ConnectorId", m."WorkflowId", m."IsEnabled", m."PollIntervalMinutes", m."QueryFilter",
                   m."MasterSource", m."MasterFormId", m."MasterConnectorId", m."AttachmentExtensions",
                   m."LastPolledAtUtc", m."LastError", m."CreatedAtUtc", m."ModifiedAtUtc",
                   c."Name", c."ProviderCode", c."ExternalAccountEmail"
            FROM dbo."EmailIngestMailbox" m
            LEFT JOIN dbo."connector" c ON c."Id" = m."ConnectorId" AND c."IsDeleted" = false
            WHERE m."IsDeleted" = false
            ORDER BY m."CreatedAtUtc" DESC;
            """;
        var list = new List<EmailIngestMailboxDto>();
        await using var cmd = new NpgsqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            list.Add(MapMailbox(reader));
        return list;
    }

    public async Task<EmailIngestMailboxDto?> GetMailboxAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var cs = RequireConnectionString();
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT m."Id", m."ConnectorId", m."WorkflowId", m."IsEnabled", m."PollIntervalMinutes", m."QueryFilter",
                   m."MasterSource", m."MasterFormId", m."MasterConnectorId", m."AttachmentExtensions",
                   m."LastPolledAtUtc", m."LastError", m."CreatedAtUtc", m."ModifiedAtUtc",
                   c."Name", c."ProviderCode", c."ExternalAccountEmail"
            FROM dbo."EmailIngestMailbox" m
            LEFT JOIN dbo."connector" c ON c."Id" = m."ConnectorId" AND c."IsDeleted" = false
            WHERE m."Id" = @Id AND m."IsDeleted" = false;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return MapMailbox(reader);
    }

    public async Task<EmailIngestMailboxDto?> GetMailboxByWorkflowIdAsync(Guid workflowId, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var cs = RequireConnectionString();
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT m."Id", m."ConnectorId", m."WorkflowId", m."IsEnabled", m."PollIntervalMinutes", m."QueryFilter",
                   m."MasterSource", m."MasterFormId", m."MasterConnectorId", m."AttachmentExtensions",
                   m."LastPolledAtUtc", m."LastError", m."CreatedAtUtc", m."ModifiedAtUtc",
                   c."Name", c."ProviderCode", c."ExternalAccountEmail"
            FROM dbo."EmailIngestMailbox" m
            LEFT JOIN dbo."connector" c ON c."Id" = m."ConnectorId" AND c."IsDeleted" = false
            WHERE m."WorkflowId" = @WorkflowId AND m."IsDeleted" = false
            ORDER BY m."CreatedAtUtc" DESC
            LIMIT 1;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@WorkflowId", workflowId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return MapMailbox(reader);
    }

    public async Task<EmailIngestMailboxDto> CreateMailboxAsync(
        EmailIngestMailboxUpsertRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await ValidateRequestAsync(request, cancellationToken);

        var id = Guid.NewGuid();
        var userId = _currentUserProvider.GetUserId();
        var cs = RequireConnectionString();
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            INSERT INTO dbo."EmailIngestMailbox"
                ("Id", "ConnectorId", "WorkflowId", "IsEnabled", "PollIntervalMinutes", "QueryFilter",
                 "MasterSource", "MasterFormId", "MasterConnectorId", "AttachmentExtensions",
                 "CreatedAtUtc", "CreatedBy", "IsDeleted")
            VALUES
                (@Id, @ConnectorId, @WorkflowId, @IsEnabled, @PollIntervalMinutes, @QueryFilter,
                 @MasterSource, @MasterFormId, @MasterConnectorId, @AttachmentExtensions,
                 now(), @CreatedBy, false);
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        BindUpsert(cmd, id, request, userId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        if (request.IsEnabled && _tenantContext.TenantId is Guid tenantId && tenantId != Guid.Empty)
            EmailIngestTenantIndex.Register(tenantId, tenantId.ToString("D"));

        return (await GetMailboxAsync(id, cancellationToken))!;
    }

    public async Task<EmailIngestMailboxDto?> UpdateMailboxAsync(
        Guid id, EmailIngestMailboxUpsertRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        if (await GetMailboxAsync(id, cancellationToken) == null)
            return null;

        await ValidateRequestAsync(request, cancellationToken);
        var userId = _currentUserProvider.GetUserId();
        var cs = RequireConnectionString();
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            UPDATE dbo."EmailIngestMailbox"
            SET "ConnectorId" = @ConnectorId,
                "WorkflowId" = @WorkflowId,
                "IsEnabled" = @IsEnabled,
                "PollIntervalMinutes" = @PollIntervalMinutes,
                "QueryFilter" = @QueryFilter,
                "MasterSource" = @MasterSource,
                "MasterFormId" = @MasterFormId,
                "MasterConnectorId" = @MasterConnectorId,
                "AttachmentExtensions" = @AttachmentExtensions,
                "ModifiedAtUtc" = now(),
                "ModifiedBy" = @CreatedBy
            WHERE "Id" = @Id AND "IsDeleted" = false;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        BindUpsert(cmd, id, request, userId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        if (_tenantContext.TenantId is Guid tenantId && tenantId != Guid.Empty)
        {
            if (request.IsEnabled)
                EmailIngestTenantIndex.Register(tenantId, tenantId.ToString("D"));
            else if (!await HasEnabledMailboxesAsync(cancellationToken))
                EmailIngestTenantIndex.Unregister(tenantId);
        }

        return await GetMailboxAsync(id, cancellationToken);
    }

    public async Task<bool> DeleteMailboxAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var cs = RequireConnectionString();
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE dbo."EmailIngestMailbox"
            SET "IsDeleted" = true, "ModifiedAtUtc" = now()
            WHERE "Id" = @Id AND "IsDeleted" = false;
            """, connection);
        cmd.Parameters.AddWithValue("@Id", id);
        var deleted = await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (deleted
            && _tenantContext.TenantId is Guid tenantId
            && tenantId != Guid.Empty
            && !await HasEnabledMailboxesAsync(cancellationToken))
        {
            EmailIngestTenantIndex.Unregister(tenantId);
        }

        return deleted;
    }

    public async Task<IReadOnlyList<EmailIngestPollResultDto>> PollDueMailboxesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var mailboxes = await ListMailboxesAsync(cancellationToken);
        var due = mailboxes.Where(m =>
            m.IsEnabled &&
            (m.LastPolledAtUtc == null ||
             m.LastPolledAtUtc.Value.AddMinutes(Math.Max(1, m.PollIntervalMinutes)) <= DateTime.UtcNow));

        var results = new List<EmailIngestPollResultDto>();
        foreach (var mailbox in due)
            results.Add(await PollMailboxAsync(mailbox.Id, cancellationToken));
        return results;
    }

    public async Task<bool> HasEnabledMailboxesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var cs = RequireConnectionString();
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand(
            """
            SELECT 1
            FROM dbo."EmailIngestMailbox"
            WHERE "IsDeleted" = false AND "IsEnabled" = true
            LIMIT 1;
            """,
            connection);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result != null && result != DBNull.Value;
    }

    public async Task<EmailIngestPollResultDto> PollMailboxAsync(Guid mailboxId, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var mailbox = await GetMailboxAsync(mailboxId, cancellationToken)
            ?? throw new InvalidOperationException("Mailbox not found.");

        var scanned = 0;
        var started = 0;
        var skipped = 0;
        string? error = null;

        try
        {
            var messages = await _oauthService.ListGmailMessagesAsync(
                mailbox.ConnectorId,
                maxResults: 25,
                query: mailbox.QueryFilter,
                unreadOnly: true,
                cancellationToken);

            var extensions = ParseExtensions(mailbox.AttachmentExtensions);
            foreach (var message in messages.Items)
            {
                scanned++;
                var messageKey = NormalizeProviderMessageId(message.Id);

                // Already handled (or any prior attachment row) — never start again; keep trying mark-as-read.
                if (await IsMessageHandledAsync(mailboxId, messageKey, cancellationToken))
                {
                    skipped++;
                    await EnsureMessageHandledAsync(mailboxId, message.Id, messageKey, mailbox.ConnectorId, cancellationToken);
                    continue;
                }

                // Prefer real invoice docs (PDF/TIFF). Skip email-signature / inline badge images.
                var matching = SelectIngestAttachments(message.Attachments, extensions);

                if (matching.Count == 0)
                {
                    await EnsureMessageHandledAsync(mailboxId, message.Id, messageKey, mailbox.ConnectorId, cancellationToken);
                    skipped++;
                    continue;
                }

                var newlyStarted = 0;
                var alreadyDone = 0;

                // As soon as we take the email: mark READ + claim in DB.
                // Do NOT wait for StartWorkflow / AP Agent to finish.
                await EnsureMessageHandledAsync(
                    mailboxId, message.Id, messageKey, mailbox.ConnectorId, cancellationToken);

                foreach (var att in matching)
                {
                    // Stable dedup key: Outlook attachment ids are long/unstable; filename+size is stable.
                    var attKey = BuildAttachmentDedupKey(att);
                    if (await IsProcessedAsync(mailboxId, messageKey, attKey, cancellationToken)
                        || await IsProcessedAsync(mailboxId, messageKey, att.Id, cancellationToken))
                    {
                        skipped++;
                        alreadyDone++;
                        continue;
                    }

                    try
                    {
                        var (stream, contentType, fileName) = await _oauthService.DownloadGmailAttachmentAsync(
                            mailbox.ConnectorId, message.Id, att.Id, cancellationToken);
                        await using (stream)
                        {
                            await using var ms = new MemoryStream();
                            await stream.CopyToAsync(ms, cancellationToken);
                            var bytes = ms.ToArray();
                            if (bytes.Length == 0)
                            {
                                await MarkProcessedAsync(mailboxId, messageKey, attKey, null, cancellationToken);
                                alreadyDone++;
                                continue;
                            }

                            // Claim attachment row before StartWorkflow so a start failure cannot re-loop.
                            await MarkProcessedAsync(mailboxId, messageKey, attKey, null, cancellationToken);

                            var contextJson = JsonSerializer.Serialize(new Dictionary<string, object?>
                            {
                                ["emailIngest"] = true,
                                ["mailboxId"] = mailbox.Id,
                                ["connectorId"] = mailbox.ConnectorId,
                                ["messageId"] = message.Id,
                                ["from"] = message.From,
                                ["subject"] = message.Subject,
                                ["receivedAtUtc"] = message.ReceivedAtUtc,
                                ["attachmentId"] = att.Id,
                                ["attachmentFileName"] = fileName ?? att.FileName,
                                ["masterSource"] = mailbox.MasterSource,
                                ["masterFormId"] = mailbox.MasterFormId,
                                ["masterConnectorId"] = mailbox.MasterConnectorId
                            });

                            var startResult = await _mediator.Send(new StartWorkflowCommand(
                                mailbox.WorkflowId,
                                Context: contextJson,
                                Attachment: new StartWorkflowAttachmentPayload(
                                    bytes,
                                    fileName ?? att.FileName ?? "invoice.bin",
                                    contentType ?? att.MimeType),
                                TriggerApAgentPythonJob: true), cancellationToken);

                            await TrySetProcessedInstanceIdAsync(
                                mailboxId, messageKey, attKey, startResult.InstanceId, cancellationToken);
                            started++;
                            newlyStarted++;
                        }
                    }
                    catch (Exception attEx)
                    {
                        _logger.LogWarning(
                            attEx,
                            "Email ingest failed for mailbox {MailboxId} message {MessageId} attachment {AttachmentId}",
                            mailboxId,
                            message.Id,
                            att.Id);
                    }
                }

                _logger.LogInformation(
                    "Email ingest mailbox {MailboxId} message {MessageId}: started={Started}, alreadyDone={AlreadyDone}, matching={Matching}",
                    mailboxId,
                    message.Id,
                    newlyStarted,
                    alreadyDone,
                    matching.Count);
            }

            await UpdatePollStatusAsync(mailboxId, null, cancellationToken);
        }
        catch (Exception ex)
        {
            error = ex.Message.Length > 1900 ? ex.Message[..1900] : ex.Message;
            await UpdatePollStatusAsync(mailboxId, error, cancellationToken);
            _logger.LogError(ex, "Email ingest poll failed for mailbox {MailboxId}", mailboxId);
        }

        return new EmailIngestPollResultDto(mailboxId, scanned, started, skipped, error);
    }

    private async Task ValidateRequestAsync(EmailIngestMailboxUpsertRequest request, CancellationToken cancellationToken)
    {
        if (request.ConnectorId == Guid.Empty)
            throw new InvalidOperationException("connectorId is required.");
        if (request.WorkflowId == Guid.Empty)
            throw new InvalidOperationException("workflowId is required.");

        var connector = await _connectorService.GetByIdAsync(request.ConnectorId, cancellationToken)
            ?? throw new InvalidOperationException("Connector not found.");
        var code = (connector.ProviderCode ?? string.Empty).Trim().ToUpperInvariant();
        if (code is not ("GMAIL" or "OUTLOOK"))
            throw new InvalidOperationException("Mailbox connector must be GMAIL or OUTLOOK.");

        var source = (request.MasterSource ?? EmailIngestMasterSources.InternalForm).Trim();
        if (string.Equals(source, EmailIngestMasterSources.InternalForm, StringComparison.OrdinalIgnoreCase))
        {
            // masterFormId is recommended for vendor master resolve, but not required to poll mail.
        }
        else if (string.Equals(source, EmailIngestMasterSources.QuickBooks, StringComparison.OrdinalIgnoreCase))
        {
            if (request.MasterConnectorId is not { } masterConnectorId || masterConnectorId == Guid.Empty)
                throw new InvalidOperationException("masterConnectorId is required when masterSource is QuickBooks.");
            var masterConn = await _connectorService.GetByIdAsync(masterConnectorId, cancellationToken)
                ?? throw new InvalidOperationException("Master QuickBooks connector not found.");
            if (!string.Equals(masterConn.ProviderCode, "QUICKBOOKS", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("masterConnectorId must be a QUICKBOOKS connector.");
        }
        else
            throw new InvalidOperationException("masterSource must be InternalForm or QuickBooks.");

        if (request.PollIntervalMinutes < 1 || request.PollIntervalMinutes > 1440)
            throw new InvalidOperationException("pollIntervalMinutes must be between 1 and 1440.");
    }

    private static void BindUpsert(NpgsqlCommand cmd, Guid id, EmailIngestMailboxUpsertRequest request, Guid? userId)
    {
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@ConnectorId", request.ConnectorId);
        cmd.Parameters.AddWithValue("@WorkflowId", request.WorkflowId);
        cmd.Parameters.AddWithValue("@IsEnabled", request.IsEnabled);
        cmd.Parameters.AddWithValue("@PollIntervalMinutes", request.PollIntervalMinutes);
        cmd.Parameters.AddWithValue("@QueryFilter", (object?)request.QueryFilter ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@MasterSource",
            string.IsNullOrWhiteSpace(request.MasterSource)
                ? EmailIngestMasterSources.InternalForm
                : request.MasterSource.Trim());
        cmd.Parameters.AddWithValue("@MasterFormId", (object?)request.MasterFormId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@MasterConnectorId", (object?)request.MasterConnectorId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@AttachmentExtensions",
            string.IsNullOrWhiteSpace(request.AttachmentExtensions)
                ? DefaultExtensions
                : request.AttachmentExtensions.Trim());
        cmd.Parameters.AddWithValue("@CreatedBy", (object?)userId ?? SystemUserId);
    }

    private async Task<bool> IsProcessedAsync(
        Guid mailboxId, string messageId, string attachmentId, CancellationToken cancellationToken)
    {
        var cs = RequireConnectionString();
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT 1 FROM dbo."EmailIngestProcessed"
            WHERE "MailboxId" = @MailboxId AND "ProviderMessageId" = @MessageId AND "AttachmentId" = @AttachmentId
            LIMIT 1;
            """, connection);
        cmd.Parameters.AddWithValue("@MailboxId", mailboxId);
        cmd.Parameters.AddWithValue("@MessageId", messageId);
        cmd.Parameters.AddWithValue("@AttachmentId", Truncate(attachmentId, 256));
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result != null && result != DBNull.Value;
    }

    private async Task<bool> IsMessageHandledAsync(
        Guid mailboxId, string messageKey, CancellationToken cancellationToken)
    {
        if (await IsProcessedAsync(mailboxId, messageKey, MessageHandledSentinel, cancellationToken))
            return true;

        var cs = RequireConnectionString();
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT 1 FROM dbo."EmailIngestProcessed"
            WHERE "MailboxId" = @MailboxId AND "ProviderMessageId" = @MessageId
            LIMIT 1;
            """, connection);
        cmd.Parameters.AddWithValue("@MailboxId", mailboxId);
        cmd.Parameters.AddWithValue("@MessageId", messageKey);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result != null && result != DBNull.Value;
    }

    private async Task EnsureMessageHandledAsync(
        Guid mailboxId,
        string providerMessageId,
        string messageKey,
        Guid connectorId,
        CancellationToken cancellationToken)
    {
        // Mark READ in Gmail/Outlook first — do not wait for workflow start/complete.
        await TryMarkMessageReadAsync(mailboxId, connectorId, providerMessageId, cancellationToken);
        // Then claim in DB so Hangfire never picks this message again even if start fails.
        await MarkProcessedAsync(mailboxId, messageKey, MessageHandledSentinel, null, cancellationToken);
    }

    private async Task MarkProcessedAsync(
        Guid mailboxId, string messageId, string attachmentId, Guid? instanceId, CancellationToken cancellationToken)
    {
        var cs = RequireConnectionString();
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO dbo."EmailIngestProcessed" ("Id", "MailboxId", "ProviderMessageId", "AttachmentId", "WorkflowInstanceId", "ProcessedAtUtc")
            VALUES (@Id, @MailboxId, @MessageId, @AttachmentId, @InstanceId, now());
            """, connection);
        cmd.Parameters.AddWithValue("@Id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("@MailboxId", mailboxId);
        cmd.Parameters.AddWithValue("@MessageId", messageId);
        cmd.Parameters.AddWithValue("@AttachmentId", Truncate(attachmentId, 256));
        cmd.Parameters.AddWithValue("@InstanceId", (object?)instanceId ?? DBNull.Value);
        try
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            _logger.LogInformation(
                "Email ingest recorded processed MailboxId={MailboxId} MessageKey={MessageKey} AttachmentKey={AttachmentKey}",
                mailboxId,
                messageId,
                Truncate(attachmentId, 256));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // unique violation — already processed
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Email ingest FAILED to record processed MailboxId={MailboxId} MessageKey={MessageKey} AttachmentKey={AttachmentKey}",
                mailboxId,
                messageId,
                Truncate(attachmentId, 256));
            throw;
        }
    }

    private async Task TrySetProcessedInstanceIdAsync(
        Guid mailboxId,
        string messageId,
        string attachmentId,
        Guid? instanceId,
        CancellationToken cancellationToken)
    {
        if (instanceId == null || instanceId == Guid.Empty)
            return;

        try
        {
            var cs = RequireConnectionString();
            await using var connection = new NpgsqlConnection(cs);
            await connection.OpenAsync(cancellationToken);
            await using var cmd = new NpgsqlCommand(
                """
                UPDATE dbo."EmailIngestProcessed"
                SET "WorkflowInstanceId" = @InstanceId
                WHERE "MailboxId" = @MailboxId
                  AND "ProviderMessageId" = @MessageId
                  AND "AttachmentId" = @AttachmentId;
                """, connection);
            cmd.Parameters.AddWithValue("@InstanceId", instanceId.Value);
            cmd.Parameters.AddWithValue("@MailboxId", mailboxId);
            cmd.Parameters.AddWithValue("@MessageId", messageId);
            cmd.Parameters.AddWithValue("@AttachmentId", Truncate(attachmentId, 256));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Email ingest could not set WorkflowInstanceId on processed row.");
        }
    }

    private async Task TryMarkMessageReadAsync(
        Guid mailboxId, Guid connectorId, string messageId, CancellationToken cancellationToken)
    {
        try
        {
            await _oauthService.MarkMailMessageReadAsync(connectorId, messageId, cancellationToken);
        }
        catch (Exception markEx)
        {
            _logger.LogWarning(
                markEx,
                "Email ingest mark-as-read failed for message {MessageId} (dedup still prevents re-start).",
                messageId);

            // Surface scope / Graph / Gmail errors on the mailbox (does not clear LastPolledAtUtc).
            var detail = markEx.Message.Length > 1800 ? markEx.Message[..1800] : markEx.Message;
            await SetMailboxLastErrorAsync(
                mailboxId,
                $"mark-as-read failed for message {messageId}: {detail}. Re-authorize Gmail (gmail.modify) or Outlook (Mail.ReadWrite) if this is a 403.",
                cancellationToken);
        }
    }

    private async Task SetMailboxLastErrorAsync(Guid mailboxId, string error, CancellationToken cancellationToken)
    {
        try
        {
            var cs = RequireConnectionString();
            await using var connection = new NpgsqlConnection(cs);
            await connection.OpenAsync(cancellationToken);
            await using var cmd = new NpgsqlCommand(
                """
                UPDATE dbo."EmailIngestMailbox"
                SET "LastError" = @Error, "ModifiedAtUtc" = now()
                WHERE "Id" = @Id AND "IsDeleted" = false;
                """, connection);
            cmd.Parameters.AddWithValue("@Id", mailboxId);
            cmd.Parameters.AddWithValue("@Error", error.Length > 2000 ? error[..2000] : error);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist EmailIngestMailbox.LastError for {MailboxId}", mailboxId);
        }
    }

    /// <summary>Fits unique index; hashes oversize Outlook Graph message ids.</summary>
    private static string NormalizeProviderMessageId(string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            return messageId;
        if (messageId.Length <= 450)
            return messageId;
        return "h:" + Sha256Hex(messageId);
    }

    /// <summary>
    /// Stable short key for dedup. Raw Outlook attachment ids change/are huge; filename+size is stable.
    /// </summary>
    private static string BuildAttachmentDedupKey(ConnectorGmailAttachmentDto att)
    {
        var fileName = (att.FileName ?? string.Empty).Trim();
        var size = att.SizeBytes?.ToString() ?? "";
        var mime = (att.MimeType ?? string.Empty).Trim();
        var raw = !string.IsNullOrEmpty(fileName)
            ? $"fn:{fileName}|sz:{size}|mt:{mime}"
            : $"id:{att.Id}";
        return "a:" + Sha256Hex(raw);
    }

    private static string Sha256Hex(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Truncate(string value, int maxLen) =>
        value.Length <= maxLen ? value : value[..maxLen];

    private async Task UpdatePollStatusAsync(Guid mailboxId, string? error, CancellationToken cancellationToken)
    {
        var cs = RequireConnectionString();
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE dbo."EmailIngestMailbox"
            SET "LastPolledAtUtc" = now(),
                "LastError" = @Error,
                "ModifiedAtUtc" = now()
            WHERE "Id" = @Id AND "IsDeleted" = false;
            """, connection);
        cmd.Parameters.AddWithValue("@Id", mailboxId);
        cmd.Parameters.AddWithValue("@Error", (object?)error ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static HashSet<string> ParseExtensions(string? raw)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (raw ?? DefaultExtensions).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ext = part.StartsWith('.') ? part : "." + part;
            set.Add(ext);
        }
        return set;
    }

    private static bool MatchesExtension(string? fileName, HashSet<string> extensions)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;
        var ext = Path.GetExtension(fileName);
        return !string.IsNullOrEmpty(ext) && extensions.Contains(ext);
    }

    /// <summary>
    /// Prefer PDF/docs over signature images. If a message has both a PDF and badge PNGs, only the PDF is ingested.
    /// </summary>
    private static List<ConnectorGmailAttachmentDto> SelectIngestAttachments(
        IReadOnlyList<ConnectorGmailAttachmentDto> attachments,
        HashSet<string> allowedExtensions)
    {
        var candidates = attachments
            .Where(a => MatchesExtension(a.FileName, allowedExtensions))
            .Where(a => !IsLikelySignatureOrInlineImage(a))
            .ToList();

        var documents = candidates.Where(IsDocumentAttachment).ToList();
        if (documents.Count > 0)
            return documents;

        // No document — do not fall back to small signature images.
        return candidates.Where(a => !IsImageAttachment(a)).ToList();
    }

    private static bool IsDocumentAttachment(ConnectorGmailAttachmentDto a)
    {
        var ext = Path.GetExtension(a.FileName ?? string.Empty);
        if (DocumentExtensions.Contains(ext))
            return true;
        var mime = a.MimeType ?? string.Empty;
        return mime.Contains("pdf", StringComparison.OrdinalIgnoreCase)
            || mime.Contains("tiff", StringComparison.OrdinalIgnoreCase)
            || mime.Contains("msword", StringComparison.OrdinalIgnoreCase)
            || mime.Contains("officedocument", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsImageAttachment(ConnectorGmailAttachmentDto a)
    {
        var ext = Path.GetExtension(a.FileName ?? string.Empty);
        if (ImageExtensions.Contains(ext))
            return true;
        var mime = a.MimeType ?? string.Empty;
        return mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelySignatureOrInlineImage(ConnectorGmailAttachmentDto a)
    {
        if (!IsImageAttachment(a))
            return false;

        // Signature / badge images are usually small; invoice scans are larger.
        if (a.SizeBytes is > 0 and < 200_000)
            return true;

        var name = Path.GetFileNameWithoutExtension(a.FileName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name))
            return true;

        if (Regex.IsMatch(name, @"^(image|img|logo|signature|sig|banner|badge|icon|cid_|untitled)\d*$", RegexOptions.IgnoreCase))
            return true;

        return false;
    }

    private string RequireConnectionString() =>
        _connectionProvider.ConnectionString
        ?? _tenantContext.ConnectionString
        ?? throw new InvalidOperationException("Tenant connection string not resolved.");

    private static EmailIngestMailboxDto MapMailbox(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetBoolean(3),
            reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetGuid(8),
            reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetDateTime(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.GetDateTime(12),
            reader.IsDBNull(13) ? null : reader.GetDateTime(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16));
}
