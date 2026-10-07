using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SaaSApp.MultiTenancy;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Infrastructure.Options;

namespace SaaSApp.Workflow.Infrastructure.Services;

public sealed class GmailPushService : IGmailPushService
{
    private readonly IConfiguration _configuration;
    private readonly IOptions<GmailPushOptions> _options;
    private readonly ITenantContext _tenantContext;
    private readonly IConnectorService _connectors;
    private readonly IConnectorOAuthService _oauth;
    private readonly ITenantConnectionStringResolver _connectionStrings;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<GmailPushService> _logger;

    public GmailPushService(
        IConfiguration configuration,
        IOptions<GmailPushOptions> options,
        ITenantContext tenantContext,
        IConnectorService connectors,
        IConnectorOAuthService oauth,
        ITenantConnectionStringResolver connectionStrings,
        IServiceScopeFactory scopes,
        ILogger<GmailPushService> logger)
    {
        _configuration = configuration;
        _options = options;
        _tenantContext = tenantContext;
        _connectors = connectors;
        _oauth = oauth;
        _connectionStrings = connectionStrings;
        _scopes = scopes;
        _logger = logger;
    }

    public async Task<GmailPushEnableResult> EnableAsync(Guid connectorId, CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantContext.TenantId
            ?? throw new InvalidOperationException("Tenant context is required.");
        var topic = _options.Value.ResolveTopicName()
            ?? throw new InvalidOperationException("GmailPush:ProjectId and GmailPush:TopicName are required.");

        var connector = await _connectors.GetByIdAsync(connectorId, cancellationToken)
            ?? throw new InvalidOperationException("Connector not found.");
        var email = connector.ExternalAccountEmail?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email))
            throw new InvalidOperationException("Gmail connector has no connected mailbox address.");

        var watch = await _oauth.WatchGmailAsync(connectorId, topic, cancellationToken);
        await UpsertWatchAsync(email, tenantId, connectorId, watch.HistoryId, watch.ExpirationUtc, cancellationToken);

        _logger.LogInformation(
            "Gmail watch enabled for {Email} connector {ConnectorId} until {ExpirationUtc}",
            email,
            connectorId,
            watch.ExpirationUtc);

        return new GmailPushEnableResult(email, topic, watch.HistoryId, watch.ExpirationUtc);
    }

    public async Task HandlePushAsync(string body, string? token, CancellationToken cancellationToken = default)
    {
        var expected = _options.Value.VerificationToken?.Trim();
        if (string.IsNullOrWhiteSpace(expected) || !string.Equals(expected, token?.Trim(), StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Gmail push token is invalid.");

        var notice = DecodeNotice(body);
        if (notice == null)
        {
            _logger.LogInformation("Gmail push ignored: body has no emailAddress.");
            return;
        }

        var watch = await FindWatchAsync(notice.Value.Email, cancellationToken);
        if (watch == null)
        {
            _logger.LogInformation("Gmail push ignored: no watch for {Email}", notice.Value.Email);
            return;
        }

        var connectionString = await _connectionStrings.GetConnectionStringAsync(watch.Value.TenantId, cancellationToken);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Tenant connection string not resolved for Gmail push.");

        using var scope = _scopes.CreateScope();
        var services = scope.ServiceProvider;
        services.GetRequiredService<ITenantConnectionProvider>().SetConnectionString(connectionString);
        var jobContext = services.GetRequiredService<JobExecutionContext>();
        jobContext.Set(watch.Value.TenantId, EmailIngestActorResolver.SystemUserId);
        try
        {
            var ingest = services.GetRequiredService<IEmailIngestService>();
            var mailboxes = await ingest.ListMailboxesAsync(cancellationToken);
            var mailbox = mailboxes.FirstOrDefault(m =>
                m.IsEnabled && m.ConnectorId == watch.Value.ConnectorId);
            if (mailbox == null)
            {
                _logger.LogInformation(
                    "Gmail push for {Email} has no enabled mailbox on connector {ConnectorId}",
                    notice.Value.Email,
                    watch.Value.ConnectorId);
                return;
            }

            var result = await ingest.PollMailboxAsync(mailbox.Id, cancellationToken);
            _logger.LogInformation(
                "Gmail push polled mailbox {MailboxId}: scanned={Scanned} started={Started} skipped={Skipped}",
                mailbox.Id,
                result.MessagesScanned,
                result.AttachmentsStarted,
                result.SkippedAlreadyProcessed);

            if (watch.Value.ExpirationUtc is null || watch.Value.ExpirationUtc < DateTime.UtcNow.AddDays(1))
            {
                var oauth = services.GetRequiredService<IConnectorOAuthService>();
                var topic = _options.Value.ResolveTopicName();
                if (!string.IsNullOrWhiteSpace(topic))
                {
                    var renewed = await oauth.WatchGmailAsync(watch.Value.ConnectorId, topic, cancellationToken);
                    await UpsertWatchAsync(
                        notice.Value.Email,
                        watch.Value.TenantId,
                        watch.Value.ConnectorId,
                        renewed.HistoryId,
                        renewed.ExpirationUtc,
                        cancellationToken);
                }
            }
            else
            {
                await UpsertWatchAsync(
                    notice.Value.Email,
                    watch.Value.TenantId,
                    watch.Value.ConnectorId,
                    notice.Value.HistoryId,
                    watch.Value.ExpirationUtc,
                    cancellationToken);
            }
        }
        finally
        {
            jobContext.Clear();
        }
    }

    private static (string Email, string HistoryId)? DecodeNotice(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("message", out var message)
            || !message.TryGetProperty("data", out var dataElement))
            return null;

        var data = dataElement.GetString();
        if (string.IsNullOrWhiteSpace(data))
            return null;

        var padded = data.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }

        var json = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        using var inner = JsonDocument.Parse(json);
        var email = inner.RootElement.TryGetProperty("emailAddress", out var emailElement)
            ? emailElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(email))
            return null;

        var history = inner.RootElement.TryGetProperty("historyId", out var historyElement)
            ? historyElement.ToString()
            : "";
        return (email.Trim().ToLowerInvariant(), history);
    }

    private async Task UpsertWatchAsync(
        string email,
        Guid tenantId,
        Guid connectorId,
        string? historyId,
        DateTime? expirationUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenCatalogAsync(cancellationToken);
        const string sql = """
            INSERT INTO catalog."GmailPushWatch"
                ("Email", "TenantId", "ConnectorId", "HistoryId", "ExpirationUtc", "UpdatedAtUtc")
            VALUES
                (@Email, @TenantId, @ConnectorId, @HistoryId, @ExpirationUtc, now())
            ON CONFLICT ("Email") DO UPDATE SET
                "TenantId" = EXCLUDED."TenantId",
                "ConnectorId" = EXCLUDED."ConnectorId",
                "HistoryId" = EXCLUDED."HistoryId",
                "ExpirationUtc" = EXCLUDED."ExpirationUtc",
                "UpdatedAtUtc" = now();
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Email", email);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        cmd.Parameters.AddWithValue("@ConnectorId", connectorId);
        cmd.Parameters.AddWithValue("@HistoryId", (object?)historyId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ExpirationUtc", (object?)expirationUtc ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<(Guid TenantId, Guid ConnectorId, DateTime? ExpirationUtc)?> FindWatchAsync(
        string email,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenCatalogAsync(cancellationToken);
        const string sql = """
            SELECT "TenantId", "ConnectorId", "ExpirationUtc"
            FROM catalog."GmailPushWatch"
            WHERE "Email" = @Email;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Email", email);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return (
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.IsDBNull(2) ? null : reader.GetDateTime(2));
    }

    private async Task<NpgsqlConnection> OpenCatalogAsync(CancellationToken cancellationToken)
    {
        var raw = _configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Catalog connection string is not configured.");
        var connection = new NpgsqlConnection(raw);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            CREATE TABLE IF NOT EXISTS catalog."GmailPushWatch" (
                "Email" varchar(320) NOT NULL CONSTRAINT "PK_GmailPushWatch" PRIMARY KEY,
                "TenantId" uuid NOT NULL,
                "ConnectorId" uuid NOT NULL,
                "HistoryId" varchar(64) NULL,
                "ExpirationUtc" timestamptz NULL,
                "UpdatedAtUtc" timestamptz NOT NULL DEFAULT now()
            );
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }
}
