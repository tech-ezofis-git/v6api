using System.Net.Http.Headers;
using Hangfire;
using Hangfire.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SaaSApp.MultiTenancy;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Application.Workflows;
using SaaSApp.Workflow.Infrastructure.Options;
using SaaSApp.Workflow.Infrastructure.Services;

namespace SaaSApp.Workflow.Infrastructure.Jobs;

public sealed class RunWatchFoxInvoiceJob
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ITenantConnectionStringResolver _connectionStrings;
    private readonly IHttpClientFactory _http;
    private readonly IOptions<WatchFoxOptions> _options;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RunWatchFoxInvoiceJob> _logger;

    public RunWatchFoxInvoiceJob(
        IServiceScopeFactory scopes,
        ITenantConnectionStringResolver connectionStrings,
        IHttpClientFactory http,
        IOptions<WatchFoxOptions> options,
        IConfiguration configuration,
        ILogger<RunWatchFoxInvoiceJob> logger)
    {
        _scopes = scopes;
        _connectionStrings = connectionStrings;
        _http = http;
        _options = options;
        _configuration = configuration;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 2)]
    [JobDisplayName("WatchFox invoice")]
    public async Task Execute(WatchFoxInvoiceJobArgs args, PerformContext? context)
    {
        if (!await TryClaimAsync(args.EventKey))
        {
            _logger.LogInformation("WatchFox event {EventKey} already started a ticket.", args.EventKey);
            return;
        }

        try
        {
            var bytes = await DownloadAsync(args.DownloadUrl);
            if (bytes.Length == 0)
                throw new InvalidOperationException("WatchFox attachment download was empty.");

            var connectionString = await _connectionStrings.GetConnectionStringAsync(args.TenantId)
                ?? throw new InvalidOperationException($"Tenant connection string not found for {args.TenantId:D}.");

            using var scope = _scopes.CreateScope();
            var services = scope.ServiceProvider;
            services.GetRequiredService<ITenantConnectionProvider>().SetConnectionString(connectionString);
            var jobContext = services.GetRequiredService<JobExecutionContext>();
            jobContext.Set(args.TenantId, EmailIngestActorResolver.SystemUserId);
            try
            {
                var workflow = await services.GetRequiredService<IWorkflowRepository>()
                    .GetByIdWithStepsAsync(args.WorkflowId)
                    ?? throw new InvalidOperationException($"Workflow {args.WorkflowId:D} was not found.");

                if (!MjbUsAgent.IsThisWorkflow(workflow.Id, args.TenantId))
                    throw new InvalidOperationException("WatchFox invoice start is only wired for the MJB workflow.");

                DateTime? received = args.ReceivedUnix is long unix and unix > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
                    : null;

                var started = await services.GetRequiredService<IMjbUsMailWorkflowStarter>().StartAsync(
                    workflow,
                    bytes,
                    args.FileName,
                    args.ContentType,
                    args.FromEmail,
                    args.Subject,
                    received,
                    args.MessageId);

                _logger.LogInformation(
                    "WatchFox event {EventKey} started MJB instance {InstanceId} for {FileName}.",
                    args.EventKey,
                    started.InstanceId,
                    args.FileName);
            }
            finally
            {
                services.GetRequiredService<JobExecutionContext>().Clear();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WatchFox invoice job failed for {EventKey}.", args.EventKey);
            await ReleaseClaimAsync(args.EventKey);
            throw;
        }
    }

    private async Task<byte[]> DownloadAsync(string url)
    {
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
                $"WatchFox attachment download returned {(int)response.StatusCode}: {Truncate(body, 300)}");
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
            _logger.LogWarning(ex, "Could not release WatchFox claim {EventKey}.", eventKey);
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
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
}
