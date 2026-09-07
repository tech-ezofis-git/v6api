using System.Text.Json;
using Npgsql;
using Microsoft.Extensions.Logging;
using SaaSApp.MultiTenancy;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>Resolves the human user to attribute email-ingest workflow starts to.</summary>
public sealed class EmailIngestActorResolver
{
    public static readonly Guid SystemUserId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly ITenantConnectionProvider _connectionProvider;
    private readonly IConnectorService _connectorService;
    private readonly IWorkflowRepository _workflowRepository;
    private readonly ILogger<EmailIngestActorResolver> _logger;

    public EmailIngestActorResolver(
        ITenantConnectionProvider connectionProvider,
        IConnectorService connectorService,
        IWorkflowRepository workflowRepository,
        ILogger<EmailIngestActorResolver> logger)
    {
        _connectionProvider = connectionProvider;
        _connectorService = connectorService;
        _workflowRepository = workflowRepository;
        _logger = logger;
    }

    public static bool IsSystemUser(Guid userId) =>
        userId == Guid.Empty || userId == SystemUserId;

    public static bool IsSystemUser(string? userIdText)
    {
        if (string.IsNullOrWhiteSpace(userIdText))
            return true;

        return Guid.TryParse(userIdText.Trim(), out var userId) && IsSystemUser(userId);
    }

    public async Task<Guid> ResolveAsync(
        Guid connectorId,
        Guid? mailboxId,
        Guid workflowId,
        CancellationToken cancellationToken = default)
    {
        var resolved = await TryResolveAsync(connectorId, mailboxId, workflowId, cancellationToken);
        if (resolved is { } actor && IsRealUser(actor))
            return actor;

        _logger.LogWarning(
            "Email ingest actor not resolved for connector {ConnectorId}, mailbox {MailboxId}, workflow {WorkflowId}; using system user.",
            connectorId,
            mailboxId,
            workflowId);
        return SystemUserId;
    }

    public async Task<Guid?> TryResolveAsync(
        Guid connectorId,
        Guid? mailboxId,
        Guid workflowId,
        CancellationToken cancellationToken = default)
    {
        if (connectorId != Guid.Empty)
        {
            var connector = await _connectorService.GetByIdAsync(connectorId, cancellationToken);
            if (connector is not null)
            {
                if (IsRealUser(connector.CreatedBy))
                    return connector.CreatedBy;

                if (!string.IsNullOrWhiteSpace(connector.ExternalAccountEmail))
                {
                    var byEmail = await TryResolveUserIdByEmailAsync(
                        connector.ExternalAccountEmail.Trim(),
                        cancellationToken);
                    if (byEmail is { } emailUser)
                        return emailUser;
                }
            }
        }

        if (mailboxId is { } mbId)
        {
            var mailboxUser = await TryGetMailboxCreatedByAsync(mbId, cancellationToken);
            if (mailboxUser is { } mailboxCreatedBy && IsRealUser(mailboxCreatedBy))
                return mailboxCreatedBy;
        }

        var workflow = await _workflowRepository.GetByIdWithStepsAsync(workflowId, cancellationToken);
        if (workflow is not null && IsRealUser(workflow.CreatedBy))
            return workflow.CreatedBy;

        return null;
    }

    public async Task<Guid?> TryResolveFromInstanceContextAsync(
        string? instanceContext,
        Guid workflowId,
        CancellationToken cancellationToken = default)
    {
        if (TryParseEmailIngestContext(instanceContext, out var connectorId, out var mailboxId))
        {
            var fromContext = await TryResolveAsync(connectorId, mailboxId, workflowId, cancellationToken);
            if (fromContext is { } contextUser && IsRealUser(contextUser))
                return contextUser;
        }

        return await TryResolveFromWorkflowMailboxAsync(workflowId, cancellationToken);
    }

    public async Task<(string Email, string? DisplayName)?> TryGetUserDisplayAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (!IsRealUser(userId))
            return null;

        var connectionString = _connectionProvider.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT "Email", "DisplayName"
            FROM users."Users"
            WHERE "Id" = @UserId AND "IsDeleted" = false
            LIMIT 1;
            """,
            connection);
        cmd.Parameters.AddWithValue("@UserId", userId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var email = reader.IsDBNull(0) ? null : reader.GetString(0);
        if (string.IsNullOrWhiteSpace(email))
            return null;

        var displayName = reader.IsDBNull(1) ? null : reader.GetString(1);
        return (email.Trim(), string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim());
    }

    public static bool TryParseEmailIngestSender(
        string? instanceContext,
        out string? senderEmail,
        out string? senderDisplayName)
    {
        senderEmail = null;
        senderDisplayName = null;

        if (string.IsNullOrWhiteSpace(instanceContext))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(instanceContext);
            var root = doc.RootElement;
            if (!IsEmailIngestContext(root))
                return false;

            if (!root.TryGetProperty("from", out var fromElement))
                return false;

            var from = fromElement.ValueKind == JsonValueKind.String
                ? fromElement.GetString()
                : fromElement.GetRawText();

            return TryParseMailAddress(from, out senderEmail, out senderDisplayName);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryParseEmailIngestContext(
        string? instanceContext,
        out Guid connectorId,
        out Guid? mailboxId)
    {
        connectorId = Guid.Empty;
        mailboxId = null;

        if (string.IsNullOrWhiteSpace(instanceContext))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(instanceContext);
            var root = doc.RootElement;
            if (!IsEmailIngestContext(root))
                return false;

            connectorId = ReadGuidProperty(root, "connectorId");
            mailboxId = ReadNullableGuidProperty(root, "mailboxId");
            return connectorId != Guid.Empty || mailboxId.HasValue;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<Guid?> TryResolveFromWorkflowMailboxAsync(
        Guid workflowId,
        CancellationToken cancellationToken)
    {
        var connectionString = _connectionProvider.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT "Id", "ConnectorId"
            FROM dbo."EmailIngestMailbox"
            WHERE "WorkflowId" = @WorkflowId AND "IsDeleted" = false
            ORDER BY "IsEnabled" DESC, "ModifiedAtUtc" DESC NULLS LAST, "CreatedAtUtc" DESC
            LIMIT 1;
            """,
            connection);
        cmd.Parameters.AddWithValue("@WorkflowId", workflowId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var mailboxId = reader.GetGuid(0);
        var connectorId = reader.GetGuid(1);
        return await TryResolveAsync(connectorId, mailboxId, workflowId, cancellationToken);
    }

    private static bool IsEmailIngestContext(JsonElement root)
    {
        if (root.TryGetProperty("emailIngest", out var flag))
        {
            return flag.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => string.Equals(flag.GetString(), "true", StringComparison.OrdinalIgnoreCase),
                JsonValueKind.Number => flag.TryGetInt32(out var n) && n != 0,
                _ => false
            };
        }

        return root.TryGetProperty("connectorId", out _)
            || root.TryGetProperty("mailboxId", out _);
    }

    private static Guid ReadGuidProperty(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
            return Guid.Empty;

        if (value.ValueKind == JsonValueKind.String
            && Guid.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return Guid.Empty;
    }

    private static Guid? ReadNullableGuidProperty(JsonElement root, string propertyName)
    {
        var parsed = ReadGuidProperty(root, propertyName);
        return parsed == Guid.Empty ? null : parsed;
    }

    private async Task<Guid?> TryGetMailboxCreatedByAsync(Guid mailboxId, CancellationToken cancellationToken)
    {
        var connectionString = _connectionProvider.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT "CreatedBy"
            FROM dbo."EmailIngestMailbox"
            WHERE "Id" = @Id AND "IsDeleted" = false
            LIMIT 1;
            """,
            connection);
        cmd.Parameters.AddWithValue("@Id", mailboxId);
        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return value is Guid g && g != Guid.Empty ? g : null;
    }

    private async Task<Guid?> TryResolveUserIdByEmailAsync(string email, CancellationToken cancellationToken)
    {
        var connectionString = _connectionProvider.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT "Id"
            FROM users."Users"
            WHERE "IsDeleted" = false AND LOWER("Email") = LOWER(@Email)
            LIMIT 1;
            """,
            connection);
        cmd.Parameters.AddWithValue("@Email", email);
        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return value is Guid g && IsRealUser(g) ? g : null;
    }

    private static bool IsRealUser(Guid userId) =>
        userId != Guid.Empty && userId != SystemUserId;

    private static bool TryParseMailAddress(string? from, out string? email, out string? displayName)
    {
        email = null;
        displayName = null;
        if (string.IsNullOrWhiteSpace(from))
            return false;

        var trimmed = from.Trim().Trim('"');
        var angleStart = trimmed.LastIndexOf('<');
        var angleEnd = trimmed.LastIndexOf('>');
        if (angleStart >= 0 && angleEnd > angleStart)
        {
            email = trimmed[(angleStart + 1)..angleEnd].Trim();
            displayName = trimmed[..angleStart].Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = email;
            return !string.IsNullOrWhiteSpace(email);
        }

        if (trimmed.Contains('@', StringComparison.Ordinal))
        {
            email = trimmed;
            displayName = trimmed;
            return true;
        }

        return false;
    }
}
