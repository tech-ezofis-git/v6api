using System.Net.Http.Headers;
using System.Text.Json;
using Hangfire;
using Hangfire.Server;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SaaSApp.MultiTenancy;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Application.Workflows.Commands.StartWorkflow;
using SaaSApp.Workflow.Infrastructure.Options;
using SaaSApp.Workflow.Infrastructure.Services;

namespace SaaSApp.Workflow.Infrastructure.Jobs;

public sealed class RunWatchFoxEnquiryJob
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ITenantConnectionStringResolver _connectionStrings;
    private readonly IHttpClientFactory _http;
    private readonly IOptions<WatchFoxOptions> _options;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RunWatchFoxEnquiryJob> _logger;

    public RunWatchFoxEnquiryJob(
        IServiceScopeFactory scopes,
        ITenantConnectionStringResolver connectionStrings,
        IHttpClientFactory http,
        IOptions<WatchFoxOptions> options,
        IConfiguration configuration,
        ILogger<RunWatchFoxEnquiryJob> logger)
    {
        _scopes = scopes;
        _connectionStrings = connectionStrings;
        _http = http;
        _options = options;
        _configuration = configuration;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 2)]
    [JobDisplayName("WatchFox enquiry")]
    public async Task Execute(WatchFoxEnquiryJobArgs args, PerformContext? context)
    {
        _ = context;
        if (!await TryClaimAsync(args.EventKey))
        {
            _logger.LogInformation("WatchFox enquiry {EventKey} already started a ticket.", args.EventKey);
            return;
        }

        try
        {
            var bytes = args.Content is { Length: > 0 }
                ? args.Content
                : await DownloadAsync(args.DownloadUrl);
            if (bytes.Length == 0)
                throw new InvalidOperationException("WatchFox enquiry .eml was empty.");

            var connectionString = await _connectionStrings.GetConnectionStringAsync(args.TenantId)
                ?? throw new InvalidOperationException($"Tenant connection string not found for {args.TenantId:D}.");

            using var scope = _scopes.CreateScope();
            var services = scope.ServiceProvider;
            services.GetRequiredService<ITenantConnectionProvider>().SetConnectionString(connectionString);
            var actorUserId = await ResolveActorUserIdAsync(connectionString, args.TenantId);
            var jobContext = services.GetRequiredService<JobExecutionContext>();
            jobContext.Set(args.TenantId, actorUserId);
            try
            {
                var fileName = string.IsNullOrWhiteSpace(args.FileName) ? "message.eml" : args.FileName;
                if (!fileName.EndsWith(".eml", StringComparison.OrdinalIgnoreCase))
                    fileName += ".eml";

                var envType = _configuration["WorkflowStart:EnvType"] ?? "trial";
                var mailContext = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["source"] = "watchfox",
                    ["from"] = args.FromEmail,
                    ["subject"] = args.Subject,
                    ["messageId"] = args.MessageId
                });

                var started = await services.GetRequiredService<IMediator>().Send(
                    new StartWorkflowCommand(
                        args.WorkflowId,
                        Context: mailContext,
                        EnvType: envType,
                        Attachment: new StartWorkflowAttachmentPayload(
                            bytes,
                            fileName,
                            string.IsNullOrWhiteSpace(args.ContentType) ? "message/rfc822" : args.ContentType),
                        TriggerApAgentPythonJob: false));

                _logger.LogInformation(
                    "WatchFox enquiry {EventKey} started workflow {WorkflowId} instance {InstanceId} with {FileName}.",
                    args.EventKey,
                    args.WorkflowId,
                    started.InstanceId,
                    fileName);
            }
            finally
            {
                services.GetRequiredService<JobExecutionContext>().Clear();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WatchFox enquiry job failed for {EventKey}.", args.EventKey);
            await ReleaseClaimAsync(args.EventKey);
            throw;
        }
    }

    private async Task<Guid> ResolveActorUserIdAsync(string connectionString, Guid tenantId)
    {
        var email = _configuration["TenantPilotUser:Email"]?.Trim();
        if (string.IsNullOrWhiteSpace(email))
            email = "pilot@ezofis.com";

        const string sql = """
            SELECT "Id"
            FROM users."Users"
            WHERE "TenantId" = @TenantId
              AND lower(btrim("Email")) = lower(btrim(@Email))
              AND "IsDeleted" = false
            LIMIT 1
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        cmd.Parameters.AddWithValue("@Email", email);
        var id = await cmd.ExecuteScalarAsync();
        if (id is not Guid userId || userId == Guid.Empty)
            throw new InvalidOperationException($"WatchFox enquiry user {email} was not found in tenant {tenantId:D}.");

        return userId;
    }

    private async Task<byte[]> DownloadAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("WatchFox enquiry has no .eml download URL.");

        var apiKey = _options.Value.ApiKey?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("WatchFox:ApiKey is not configured.");

        var client = _http.CreateClient(nameof(WatchFoxWebhookService));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"WatchFox .eml download returned {(int)response.StatusCode}: {Truncate(body, 300)}");
        }

        return await response.Content.ReadAsByteArrayAsync();
    }

    private async Task<bool> TryClaimAsync(string eventKey)
    {
        await using var connection = await OpenCatalogAsync();
        await EnsureTableAsync(connection);
        const string sql = """
            INSERT INTO catalog."WatchFoxEvent" ("EventKey", "ReceivedAtUtc")
            VALUES (@EventKey, now())
            ON CONFLICT ("EventKey") DO NOTHING;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@EventKey", eventKey);
        var inserted = await cmd.ExecuteNonQueryAsync();
        return inserted == 1;
    }

    private async Task ReleaseClaimAsync(string eventKey)
    {
        try
        {
            await using var connection = await OpenCatalogAsync();
            await using var cmd = new NpgsqlCommand(
                """DELETE FROM catalog."WatchFoxEvent" WHERE "EventKey" = @EventKey""",
                connection);
            cmd.Parameters.AddWithValue("@EventKey", eventKey);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not release WatchFox enquiry claim {EventKey}.", eventKey);
        }
    }

    private static async Task EnsureTableAsync(NpgsqlConnection connection)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS catalog."WatchFoxEvent" (
                "EventKey" varchar(160) NOT NULL CONSTRAINT "PK_WatchFoxEvent" PRIMARY KEY,
                "ReceivedAtUtc" timestamptz NOT NULL
            );
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<NpgsqlConnection> OpenCatalogAsync()
    {
        var raw = _configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Catalog connection string is not configured.");
        var connection = new NpgsqlConnection(raw);
        await connection.OpenAsync();
        return connection;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
