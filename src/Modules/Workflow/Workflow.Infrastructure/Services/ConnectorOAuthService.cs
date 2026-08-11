using Npgsql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SaaSApp.Catalog;
using SaaSApp.Catalog.Entities;
using SaaSApp.MultiTenancy;
using SaaSApp.Workflow.Application.Connectors;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Infrastructure.Options;

namespace SaaSApp.Workflow.Infrastructure.Services;

public sealed class ConnectorOAuthService : IConnectorOAuthService
{
    private readonly IConnectorProviderCatalog _providerCatalog;
    private readonly IEnumerable<IConnectorProviderAdapter> _adapters;
    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUserProvider _currentUserProvider;
    private readonly ITenantConnectionStringResolver _connectionResolver;
    private readonly ITenantConnectionProvider _connectionProvider;
    private readonly IConnectorService _connectorService;
    private readonly ConnectorOAuthOptions _options;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ConnectorOAuthService> _logger;

    public ConnectorOAuthService(
        IConnectorProviderCatalog providerCatalog,
        IEnumerable<IConnectorProviderAdapter> adapters,
        ITenantContext tenantContext,
        ICurrentUserProvider currentUserProvider,
        ITenantConnectionStringResolver connectionResolver,
        ITenantConnectionProvider connectionProvider,
        IConnectorService connectorService,
        IOptions<ConnectorOAuthOptions> options,
        IConfiguration configuration,
        ILogger<ConnectorOAuthService> logger)
    {
        _providerCatalog = providerCatalog;
        _adapters = adapters;
        _tenantContext = tenantContext;
        _currentUserProvider = currentUserProvider;
        _connectionResolver = connectionResolver;
        _connectionProvider = connectionProvider;
        _connectorService = connectorService;
        _options = options.Value;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ConnectorProviderPublicDto>> ListProvidersAsync(CancellationToken cancellationToken = default)
    {
        var providers = await _providerCatalog.ListActiveAsync(cancellationToken);
        var adapterMap = _adapters.ToDictionary(a => a.ProviderCode, StringComparer.OrdinalIgnoreCase);
        return providers.Select(p =>
        {
            adapterMap.TryGetValue(p.ProviderCode, out var adapter);
            return new ConnectorProviderPublicDto(
                p.ProviderCode,
                p.DisplayName,
                !string.IsNullOrWhiteSpace(p.ClientId) && !string.IsNullOrWhiteSpace(p.RedirectUri),
                adapter?.SupportsFiles ?? false,
                adapter?.SupportsGmail ?? false,
                adapter?.SupportsQuickBooks ?? false);
        }).ToList();
    }

    public async Task<ConnectorOAuthAuthorizeResponse> BeginAuthorizeAsync(
        ConnectorOAuthAuthorizeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ProviderCode))
            throw new InvalidOperationException("providerCode is required.");

        var provider = await RequireProviderAsync(request.ProviderCode, cancellationToken);
        var adapter = RequireAdapter(provider.ProviderCode);
        ValidateProviderConfigured(provider);

        var tenantId = RequireTenantId();
        var userId = _currentUserProvider.GetUserId()
            ?? throw new InvalidOperationException("User context is required.");

        Guid connectorId;
        if (request.ConnectorId is { } existing && existing != Guid.Empty)
        {
            var current = await _connectorService.GetByIdAsync(existing, cancellationToken)
                ?? throw new InvalidOperationException("Connector not found.");
            connectorId = current.Id;
            await UpdateOAuthStatusAsync(connectorId, "Pending", cancellationToken);
            if (!string.IsNullOrWhiteSpace(request.ConfigJson))
            {
                await _connectorService.UpdateAsync(connectorId, new ConnectorUpsertRequest(
                    current.Name,
                    provider.ProviderCode,
                    request.ConfigJson,
                    current.IsDefault), cancellationToken);
            }
        }
        else
        {
            var created = await _connectorService.CreateAsync(new ConnectorUpsertRequest(
                request.Name ?? provider.DisplayName,
                provider.ProviderCode,
                request.ConfigJson,
                false), cancellationToken);
            connectorId = created.Id;
            await UpdateOAuthStatusAsync(connectorId, "Pending", cancellationToken);
        }

        var state = ConnectorOAuthStateHelper.Create(new ConnectorOAuthStatePayload
        {
            TenantId = tenantId,
            UserId = userId,
            ConnectorId = connectorId,
            ProviderCode = provider.ProviderCode,
            SuccessRedirectUrl = request.SuccessRedirectUrl ?? _options.DefaultSuccessRedirectUrl,
            Exp = DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, _options.StateTtlMinutes)).ToUnixTimeSeconds()
        }, ResolveSigningKey());

        var config = ToConfig(provider);
        var url = adapter.BuildAuthorizeUrl(config, state);
        return new ConnectorOAuthAuthorizeResponse(connectorId, url, state);
    }

    public async Task<string> CompleteCallbackAsync(
        string? code,
        string? state,
        string? error,
        string? realmId = null,
        CancellationToken cancellationToken = default)
    {
        var payload = ConnectorOAuthStateHelper.Parse(state ?? string.Empty, ResolveSigningKey());
        var redirectBase = string.IsNullOrWhiteSpace(payload.SuccessRedirectUrl)
            ? (_options.DefaultSuccessRedirectUrl ?? "/")
            : payload.SuccessRedirectUrl!;

        if (!string.IsNullOrWhiteSpace(error))
            return AppendQuery(redirectBase, new Dictionary<string, string>
            {
                ["connectorOAuth"] = "error",
                ["error"] = error,
                ["connectorId"] = payload.ConnectorId.ToString("D")
            });

        if (string.IsNullOrWhiteSpace(code))
            return AppendQuery(redirectBase, new Dictionary<string, string>
            {
                ["connectorOAuth"] = "error",
                ["error"] = "missing_code",
                ["connectorId"] = payload.ConnectorId.ToString("D")
            });

        var connectionString = await _connectionResolver.GetConnectionStringAsync(payload.TenantId, cancellationToken)
            ?? throw new InvalidOperationException("Tenant not found for OAuth callback.");
        _connectionProvider.SetConnectionString(connectionString);

        try
        {
            var provider = await RequireProviderAsync(payload.ProviderCode, cancellationToken);
            var adapter = RequireAdapter(provider.ProviderCode);
            ValidateProviderConfigured(provider);

            var token = await adapter.ExchangeCodeAsync(ToConfig(provider), code, cancellationToken);
            if (!string.IsNullOrWhiteSpace(realmId) && string.IsNullOrWhiteSpace(token.ExternalAccountId))
                token = token with { ExternalAccountId = realmId.Trim() };

            await SaveTokensAsync(
                payload.ConnectorId,
                token,
                "Connected",
                cancellationToken);

            _logger.LogInformation(
                "OAuth connected connector {ConnectorId} provider {Provider} tenant {TenantId}",
                payload.ConnectorId, payload.ProviderCode, payload.TenantId);

            return AppendQuery(redirectBase, new Dictionary<string, string>
            {
                ["connectorOAuth"] = "success",
                ["connectorId"] = payload.ConnectorId.ToString("D"),
                ["provider"] = payload.ProviderCode
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OAuth callback failed for connector {ConnectorId}", payload.ConnectorId);
            try
            {
                await UpdateOAuthStatusAsync(payload.ConnectorId, "Expired", cancellationToken);
            }
            catch
            {
                // ignore secondary failure
            }

            return AppendQuery(redirectBase, new Dictionary<string, string>
            {
                ["connectorOAuth"] = "error",
                ["error"] = "token_exchange_failed",
                ["connectorId"] = payload.ConnectorId.ToString("D")
            });
        }
    }

    public async Task<ConnectorOAuthStatusDto?> RefreshAsync(Guid connectorId, CancellationToken cancellationToken = default)
    {
        var row = await LoadTokenRowAsync(connectorId, cancellationToken);
        if (row == null)
            return null;
        if (string.IsNullOrWhiteSpace(row.RefreshToken))
            throw new InvalidOperationException("No refresh token stored for this connector.");

        var provider = await RequireProviderAsync(row.ProviderCode ?? string.Empty, cancellationToken);
        var adapter = RequireAdapter(provider.ProviderCode);
        var token = await adapter.RefreshTokenAsync(ToConfig(provider), row.RefreshToken, cancellationToken);
        await SaveTokensAsync(connectorId, token, "Connected", cancellationToken);
        return await GetStatusAsync(connectorId, cancellationToken);
    }

    public async Task<ConnectorOAuthStatusDto?> DisconnectAsync(Guid connectorId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenTenantConnectionAsync(cancellationToken);
        await EnsureOAuthColumnsAsync(connection, cancellationToken);
        const string sql = """
            UPDATE dbo."connector"
            SET "AccessToken" = NULL,
                "RefreshToken" = NULL,
                "TokenExpiresAtUtc" = NULL,
                "OAuthStatus" = 'Revoked',
                "ModifiedAtUtc" = @Now
            WHERE "Id" = @Id AND "IsDeleted" = false;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", connectorId);
        cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow);
        var updated = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return updated == 0 ? null : await GetStatusAsync(connectorId, cancellationToken);
    }

    public async Task<ConnectorOAuthStatusDto?> GetStatusAsync(Guid connectorId, CancellationToken cancellationToken = default)
    {
        var row = await LoadTokenRowAsync(connectorId, cancellationToken);
        if (row == null)
            return null;
        var connected = string.Equals(row.OAuthStatus, "Connected", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(row.AccessToken);
        return new ConnectorOAuthStatusDto(
            connectorId,
            row.ProviderCode,
            row.OAuthStatus ?? "Pending",
            row.ExternalAccountEmail,
            row.TokenExpiresAtUtc,
            connected);
    }

    public async Task EnsureValidAccessTokenAsync(Guid connectorId, CancellationToken cancellationToken = default)
    {
        var row = await LoadTokenRowAsync(connectorId, cancellationToken)
            ?? throw new InvalidOperationException("Connector not found.");
        if (string.IsNullOrWhiteSpace(row.AccessToken))
            throw new InvalidOperationException("Connector is not connected. Complete OAuth first.");

        var skew = TimeSpan.FromMinutes(Math.Max(0, _options.RefreshSkewMinutes));
        if (row.TokenExpiresAtUtc is null || row.TokenExpiresAtUtc > DateTime.UtcNow.Add(skew))
            return;

        if (string.IsNullOrWhiteSpace(row.RefreshToken))
            throw new InvalidOperationException("Access token expired and no refresh token is available. Re-authorize the connector.");

        await RefreshAsync(connectorId, cancellationToken);
    }

    public async Task<ConnectorFileListResponse> ListFilesAsync(Guid connectorId, string? path, CancellationToken cancellationToken = default)
    {
        var (adapter, accessToken, credentialJson) = await PrepareOpsAsync(connectorId, files: true, gmail: false, quickBooks: false, cancellationToken);
        var items = await adapter.ListFilesAsync(accessToken, path, credentialJson, cancellationToken);
        return new ConnectorFileListResponse(items.Select(i =>
            new ConnectorFileEntryDto(i.Path, i.Name, i.IsFolder, i.SizeBytes, i.ModifiedAtUtc)).ToList());
    }

    public async Task UploadFileAsync(
        Guid connectorId, string path, string fileName, Stream content, string? contentType, CancellationToken cancellationToken = default)
    {
        var (adapter, accessToken, credentialJson) = await PrepareOpsAsync(connectorId, files: true, gmail: false, quickBooks: false, cancellationToken);
        await adapter.UploadFileAsync(accessToken, path, fileName, content, contentType, credentialJson, cancellationToken);
    }

    public async Task<(Stream Content, string ContentType, string FileName)> DownloadFileAsync(
        Guid connectorId, string path, CancellationToken cancellationToken = default)
    {
        var (adapter, accessToken, credentialJson) = await PrepareOpsAsync(connectorId, files: true, gmail: false, quickBooks: false, cancellationToken);
        return await adapter.DownloadFileAsync(accessToken, path, credentialJson, cancellationToken);
    }

    public async Task<ConnectorMailSummaryDto> GetMailSummaryAsync(
        Guid connectorId, CancellationToken cancellationToken = default)
    {
        var (adapter, accessToken, _) = await PrepareOpsAsync(connectorId, files: false, gmail: true, quickBooks: false, cancellationToken);
        var (total, unread) = await adapter.GetMailSummaryAsync(accessToken, cancellationToken);
        return new ConnectorMailSummaryDto(total, unread);
    }

    public async Task<ConnectorGmailMessageListResponse> ListGmailMessagesAsync(
        Guid connectorId, int maxResults, string? query, bool unreadOnly = false, CancellationToken cancellationToken = default)
    {
        var (adapter, accessToken, _) = await PrepareOpsAsync(connectorId, files: false, gmail: true, quickBooks: false, cancellationToken);
        var items = await adapter.ListGmailMessagesAsync(accessToken, maxResults, query, unreadOnly, cancellationToken);
        return new ConnectorGmailMessageListResponse(items.Select(m =>
            new ConnectorGmailMessageDto(
                m.Id,
                m.ThreadId,
                m.Subject,
                m.From,
                m.Snippet,
                m.ReceivedAtUtc,
                m.Attachments.Select(a => new ConnectorGmailAttachmentDto(a.Id, a.FileName, a.MimeType, a.SizeBytes)).ToList(),
                m.IsUnread)).ToList());
    }

    public async Task<ConnectorGmailMessageDto> GetMailMessageAsync(
        Guid connectorId, string messageId, CancellationToken cancellationToken = default)
    {
        var (adapter, accessToken, _) = await PrepareOpsAsync(connectorId, files: false, gmail: true, quickBooks: false, cancellationToken);
        var m = await adapter.GetMailMessageAsync(accessToken, messageId, cancellationToken);
        return new ConnectorGmailMessageDto(
            m.Id,
            m.ThreadId,
            m.Subject,
            m.From,
            m.Snippet,
            m.ReceivedAtUtc,
            m.Attachments.Select(a => new ConnectorGmailAttachmentDto(a.Id, a.FileName, a.MimeType, a.SizeBytes)).ToList(),
            m.IsUnread,
            m.BodyText,
            m.BodyHtml);
    }

    public async Task<ConnectorGmailMessageDto?> GetTopMailMessageAsync(
        Guid connectorId, bool unreadOnly = true, string? query = null, CancellationToken cancellationToken = default)
    {
        var list = await ListGmailMessagesAsync(connectorId, 1, query, unreadOnly, cancellationToken);
        var top = list.Items.FirstOrDefault();
        if (top == null)
            return null;
        return await GetMailMessageAsync(connectorId, top.Id, cancellationToken);
    }

    public async Task MarkMailMessageReadAsync(
        Guid connectorId, string messageId, CancellationToken cancellationToken = default)
    {
        var (adapter, accessToken, _) = await PrepareOpsAsync(connectorId, files: false, gmail: true, quickBooks: false, cancellationToken);
        await adapter.MarkMailMessageReadAsync(accessToken, messageId, cancellationToken);
    }

    public async Task<(Stream Content, string ContentType, string FileName)> DownloadGmailAttachmentAsync(
        Guid connectorId, string messageId, string attachmentId, CancellationToken cancellationToken = default)
    {
        var (adapter, accessToken, _) = await PrepareOpsAsync(connectorId, files: false, gmail: true, quickBooks: false, cancellationToken);
        return await adapter.DownloadGmailAttachmentAsync(accessToken, messageId, attachmentId, cancellationToken);
    }

    public async Task<ConnectorQuickBooksMasterListResponse> ListQuickBooksMastersAsync(
        Guid connectorId, string masterType, int maxResults, CancellationToken cancellationToken = default)
    {
        var (adapter, accessToken, configJson, realmId) = await PrepareQuickBooksAsync(connectorId, cancellationToken);
        var items = await adapter.ListQuickBooksMastersAsync(accessToken, realmId, masterType, maxResults, configJson, cancellationToken);
        return new ConnectorQuickBooksMasterListResponse(
            masterType,
            items.Select(i => new ConnectorQuickBooksMasterDto(i.Id, i.Type, i.DisplayName, i.Email, i.Active, i.RawJson)).ToList());
    }

    public async Task<ConnectorQuickBooksDocumentListResponse> ListQuickBooksDocumentsAsync(
        Guid connectorId, string documentType, int maxResults, CancellationToken cancellationToken = default)
    {
        var (adapter, accessToken, configJson, realmId) = await PrepareQuickBooksAsync(connectorId, cancellationToken);
        var items = await adapter.ListQuickBooksDocumentsAsync(accessToken, realmId, documentType, maxResults, configJson, cancellationToken);
        return new ConnectorQuickBooksDocumentListResponse(
            documentType,
            items.Select(i => new ConnectorQuickBooksDocumentDto(
                i.Id, i.Type, i.DocNumber, i.TxnDate, i.TotalAmount, i.PartyName, i.Status, i.RawJson)).ToList());
    }

    public async Task<(Stream Content, string ContentType, string FileName)> DownloadQuickBooksDocumentPdfAsync(
        Guid connectorId, string documentType, string documentId, CancellationToken cancellationToken = default)
    {
        var (adapter, accessToken, configJson, realmId) = await PrepareQuickBooksAsync(connectorId, cancellationToken);
        return await adapter.DownloadQuickBooksDocumentPdfAsync(accessToken, realmId, documentType, documentId, configJson, cancellationToken);
    }

    public async Task<ConnectorQuickBooksPoLookupResponse> LookupQuickBooksPurchaseOrderAsync(
        Guid connectorId, string poNumber, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(poNumber))
            throw new InvalidOperationException("poNumber is required.");

        var normalized = poNumber.Trim();
        var (adapter, accessToken, configJson, realmId) = await PrepareQuickBooksAsync(connectorId, cancellationToken);
        var raw = await adapter.GetQuickBooksPurchaseOrderRawByDocNumberAsync(
            accessToken, realmId, normalized, configJson, cancellationToken);

        if (string.IsNullOrWhiteSpace(raw))
            return new ConnectorQuickBooksPoLookupResponse(false, normalized, null);

        return new ConnectorQuickBooksPoLookupResponse(
            true,
            normalized,
            QuickBooksPurchaseOrderMapper.FromRawJson(raw));
    }

    private async Task<(IConnectorProviderAdapter Adapter, string AccessToken, string? ConfigJson)> PrepareOpsAsync(
        Guid connectorId, bool files, bool gmail, bool quickBooks, CancellationToken cancellationToken)
    {
        await EnsureValidAccessTokenAsync(connectorId, cancellationToken);
        var row = await LoadTokenRowAsync(connectorId, cancellationToken)
            ?? throw new InvalidOperationException("Connector not found.");
        var adapter = RequireAdapter(row.ProviderCode ?? string.Empty);
        if (files && !adapter.SupportsFiles)
            throw new InvalidOperationException($"{adapter.ProviderCode} does not support file operations.");
        if (gmail && !adapter.SupportsGmail)
            throw new InvalidOperationException($"{adapter.ProviderCode} does not support mail operations.");
        if (quickBooks && !adapter.SupportsQuickBooks)
            throw new InvalidOperationException($"{adapter.ProviderCode} does not support QuickBooks operations.");
        return (adapter, row.AccessToken!, row.ConfigJson);
    }

    private async Task<(IConnectorProviderAdapter Adapter, string AccessToken, string? ConfigJson, string RealmId)> PrepareQuickBooksAsync(
        Guid connectorId, CancellationToken cancellationToken)
    {
        var (adapter, accessToken, configJson) = await PrepareOpsAsync(connectorId, files: false, gmail: false, quickBooks: true, cancellationToken);
        var row = await LoadTokenRowAsync(connectorId, cancellationToken)
            ?? throw new InvalidOperationException("Connector not found.");
        if (string.IsNullOrWhiteSpace(row.ExternalAccountId))
            throw new InvalidOperationException("QuickBooks realmId is missing. Disconnect and re-authorize the connector.");
        return (adapter, accessToken, configJson, row.ExternalAccountId!);
    }

    private async Task SaveTokensAsync(
        Guid connectorId,
        ConnectorOAuthTokenResult token,
        string status,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenTenantConnectionAsync(cancellationToken);
        await EnsureOAuthColumnsAsync(connection, cancellationToken);

        const string sql = """
            UPDATE dbo."connector"
            SET "AccessToken" = @AccessToken,
                "RefreshToken" = COALESCE(@RefreshToken, "RefreshToken"),
                "TokenExpiresAtUtc" = @Expires,
                "ExternalAccountEmail" = COALESCE(@Email, "ExternalAccountEmail"),
                "ExternalAccountId" = COALESCE(@ExternalId, "ExternalAccountId"),
                "OAuthStatus" = @Status,
                "ModifiedAtUtc" = @Now
            WHERE "Id" = @Id AND "IsDeleted" = false;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", connectorId);
        cmd.Parameters.AddWithValue("@AccessToken", token.AccessToken);
        cmd.Parameters.AddWithValue("@RefreshToken", (object?)token.RefreshToken ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Expires", (object?)token.ExpiresAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Email", (object?)token.ExternalAccountEmail ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ExternalId", (object?)token.ExternalAccountId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Status", status);
        cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow);
        var n = await cmd.ExecuteNonQueryAsync(cancellationToken);
        if (n == 0)
            throw new InvalidOperationException("Connector not found while saving OAuth tokens.");
    }

    private async Task UpdateOAuthStatusAsync(Guid connectorId, string status, CancellationToken cancellationToken)
    {
        await using var connection = await OpenTenantConnectionAsync(cancellationToken);
        await EnsureOAuthColumnsAsync(connection, cancellationToken);
        const string sql = """UPDATE dbo."connector" SET "OAuthStatus" = @Status, "ModifiedAtUtc" = @Now WHERE "Id" = @Id AND "IsDeleted" = false;""";
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", connectorId);
        cmd.Parameters.AddWithValue("@Status", status);
        cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<TokenRow?> LoadTokenRowAsync(Guid connectorId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenTenantConnectionAsync(cancellationToken);
        await EnsureOAuthColumnsAsync(connection, cancellationToken);
        const string sql = """
            SELECT "ProviderCode", "ConfigJson", "AccessToken", "RefreshToken", "TokenExpiresAtUtc",
                   "ExternalAccountEmail", "ExternalAccountId", "OAuthStatus"
            FROM dbo."connector"
            WHERE "Id" = @Id AND "IsDeleted" = false;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", connectorId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new TokenRow(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetDateTime(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? "Pending" : reader.GetString(7));
    }

    private async Task EnsureOAuthColumnsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, cancellationToken))
        {
            throw new InvalidOperationException(
                "dbo.\"connector\" does not exist. Run scripts/postgres/Create-Connector-Table.sql on the tenant database.");
        }

        // Modern schema check
        const string check = """
            SELECT COUNT(1) FROM information_schema.columns
            WHERE table_schema = 'dbo' AND table_name = 'connector' AND column_name = 'ProviderCode';
            """;
        await using var cmd = new NpgsqlCommand(check, connection);
        var ok = Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken)) > 0;
        if (!ok)
        {
            throw new InvalidOperationException(
                "dbo.\"connector\" is on a legacy schema. Run scripts/postgres/Create-Connector-Table.sql to migrate.");
        }
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(1) FROM information_schema.tables
            WHERE table_schema = 'dbo' AND table_name = 'connector';
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private async Task<NpgsqlConnection> OpenTenantConnectionAsync(CancellationToken cancellationToken)
    {
        var cs = _tenantContext.ConnectionString
            ?? _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");
        var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private async Task<ConnectorProvider> RequireProviderAsync(string code, CancellationToken cancellationToken)
    {
        var provider = await _providerCatalog.GetByCodeAsync(code, cancellationToken);
        if (provider == null)
            throw new InvalidOperationException($"Unknown or inactive provider '{code}'.");
        return provider;
    }

    private IConnectorProviderAdapter RequireAdapter(string code)
    {
        var adapter = _adapters.FirstOrDefault(a =>
            string.Equals(a.ProviderCode, code, StringComparison.OrdinalIgnoreCase));
        if (adapter == null)
            throw new InvalidOperationException($"No adapter registered for provider '{code}'.");
        return adapter;
    }

    private static void ValidateProviderConfigured(ConnectorProvider provider)
    {
        if (string.IsNullOrWhiteSpace(provider.ClientId) ||
            string.IsNullOrWhiteSpace(provider.ClientSecret) ||
            string.IsNullOrWhiteSpace(provider.RedirectUri))
        {
            throw new InvalidOperationException(
                $"Provider '{provider.ProviderCode}' is not configured. Set ClientId, ClientSecret, and RedirectUri in catalog.ConnectorProviders.");
        }
    }

    private static ConnectorProviderConfig ToConfig(ConnectorProvider p) =>
        new(p.ProviderCode, p.ClientId, p.ClientSecret, p.AuthUrl, p.TokenUrl, p.Scopes, p.RedirectUri, p.ExtraConfigJson);

    private Guid RequireTenantId() =>
        _tenantContext.TenantId ?? throw new InvalidOperationException("Tenant context is required.");

    private string ResolveSigningKey()
    {
        var key = _options.StateSigningKey;
        if (string.IsNullOrWhiteSpace(key))
            key = _configuration["EzofisAuth:SigningKey"];
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Configure ConnectorOAuth:StateSigningKey or EzofisAuth:SigningKey.");
        return key;
    }

    private static string AppendQuery(string baseUrl, IReadOnlyDictionary<string, string> query)
    {
        var qs = string.Join("&", query.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        if (string.IsNullOrWhiteSpace(baseUrl))
            return "?" + qs;
        return baseUrl.Contains('?', StringComparison.Ordinal) ? $"{baseUrl}&{qs}" : $"{baseUrl}?{qs}";
    }

    private sealed record TokenRow(
        string? ProviderCode,
        string? ConfigJson,
        string? AccessToken,
        string? RefreshToken,
        DateTime? TokenExpiresAtUtc,
        string? ExternalAccountEmail,
        string? ExternalAccountId,
        string? OAuthStatus);
}
