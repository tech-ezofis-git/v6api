using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SaaSApp.MultiTenancy;
using SaaSApp.SharedKernel.Options;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Infrastructure.Options;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>POST AP Agent job to agents <c>/chat</c> (<c>intent=ap</c>). Move-next is handled inside the agents service.</summary>
public sealed class ApAgentPythonPipelineService : IApAgentPythonPipelineService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ITenantConnectionStringResolver _connectionStringResolver;
    private readonly ITenantConnectionProvider _connectionProvider;
    private readonly JobExecutionContext _jobContext;
    private readonly IApAgentPilotAuthProvider _pilotAuthProvider;
    private readonly IOptions<ApAgentOptions> _options;
    private readonly AgentsChatOptions _agentsChat;
    private readonly ILogger<ApAgentPythonPipelineService> _logger;

    public ApAgentPythonPipelineService(
        IHttpClientFactory httpClientFactory,
        ITenantConnectionStringResolver connectionStringResolver,
        ITenantConnectionProvider connectionProvider,
        JobExecutionContext jobContext,
        IApAgentPilotAuthProvider pilotAuthProvider,
        IOptions<ApAgentOptions> options,
        IOptions<AgentsChatOptions> agentsChat,
        ILogger<ApAgentPythonPipelineService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _connectionStringResolver = connectionStringResolver;
        _connectionProvider = connectionProvider;
        _jobContext = jobContext;
        _pilotAuthProvider = pilotAuthProvider;
        _options = options;
        _agentsChat = agentsChat.Value;
        _logger = logger;
    }

    public async Task ExecuteAsync(
        ApAgentPythonJobArgs args,
        string? hangfireJobId = null,
        CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            _logger.LogInformation(
                "AP Agent Python pipeline disabled; skipping instance {InstanceId}.",
                args.InstanceId);
            return;
        }

        var chatUrl = _agentsChat.ResolveChatUrl();
        if (string.IsNullOrWhiteSpace(chatUrl))
        {
            throw new InvalidOperationException(
                "Agents:ChatUrl is not configured. Add it to appsettings.json " +
                "(e.g. \"Agents\": { \"ChatUrl\": \"https://cloud.ezofis.com/chat\" }).");
        }

        var connectionString = await _connectionStringResolver.GetConnectionStringAsync(args.TenantId, cancellationToken);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"Tenant connection string not found for {args.TenantId:D}.");

        _connectionProvider.SetConnectionString(connectionString);
        _jobContext.Set(args.TenantId, args.UserId);

        try
        {
            var requestBody = await BuildChatRequestJsonAsync(args, hangfireJobId, cancellationToken);
            await PostToPythonAsync(options, chatUrl, requestBody, cancellationToken);

            _logger.LogInformation(
                "AP Agent /chat call finished for instance {InstanceId}.",
                args.InstanceId);
        }
        finally
        {
            _jobContext.Clear();
        }
    }

    /// <inheritdoc />
    public string BuildChatRequestJson(ApAgentPythonJobArgs args, string? hangfireJobId = null) =>
        BuildChatRequestJsonAsync(args, hangfireJobId, CancellationToken.None).GetAwaiter().GetResult();

    private async Task<string> BuildChatRequestJsonAsync(
        ApAgentPythonJobArgs args,
        string? hangfireJobId,
        CancellationToken cancellationToken)
    {
        var options = _options.Value;
        if (string.IsNullOrWhiteSpace(args.StartPayloadJson))
            throw new InvalidOperationException("Start payload JSON is empty.");

        var inner = ApAgentStartPayloadJson.UnwrapInner(args.StartPayloadJson);
        var sessionId = !string.IsNullOrWhiteSpace(hangfireJobId)
            ? hangfireJobId
            : $"ap-{args.InstanceId:N}";

        if (!string.IsNullOrWhiteSpace(hangfireJobId))
        {
            inner = ApAgentStartPayloadJson.EnrichWithJobTracking(
                inner,
                args.WorkflowId,
                args.InstanceId,
                hangfireJobId,
                options.ApiBaseUrl);
        }

        if (options.IncludePilotAuthInPayload)
        {
            try
            {
                var pilotAuth = await _pilotAuthProvider.GetAuthForTenantAsync(args.TenantId, cancellationToken);
                if (pilotAuth != null)
                    inner = ApAgentStartPayloadJson.EnrichWithPilotAuth(inner, pilotAuth);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not enrich AP Agent payload with pilot auth for tenant {TenantId}.",
                    args.TenantId);
            }
        }

        var skills = ApAgentStartPayloadJson.NormalizeSkills(args.Skills);
        return ApAgentStartPayloadJson.BuildChatApRequestJson(inner, sessionId, skills);
    }

    private async Task PostToPythonAsync(
        ApAgentOptions options,
        string chatUrl,
        string requestBody,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(ApAgentPythonPipelineService));
        using var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, options.TimeoutMinutes)));

        _logger.LogInformation("Posting AP Agent /chat request to {Url}", chatUrl);

        using var response = await client.PostAsync(chatUrl, content, timeoutCts.Token);
        var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"AP Agent /chat returned {(int)response.StatusCode}: {Truncate(body, 500)}");
        }

        if (string.IsNullOrWhiteSpace(body))
            throw new InvalidOperationException("AP Agent /chat returned an empty response body.");
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";
}
