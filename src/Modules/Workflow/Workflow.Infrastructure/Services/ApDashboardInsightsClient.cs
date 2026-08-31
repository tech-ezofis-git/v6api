using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SaaSApp.SharedKernel.Options;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Infrastructure.Options;

namespace SaaSApp.Workflow.Infrastructure.Services;

public sealed class ApDashboardInsightsClient : IApDashboardInsightsClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AgentsChatOptions _agentsChat;
    private readonly ILogger<ApDashboardInsightsClient> _logger;

    public ApDashboardInsightsClient(
        IHttpClientFactory httpClientFactory,
        IOptions<AgentsChatOptions> agentsChat,
        ILogger<ApDashboardInsightsClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _agentsChat = agentsChat.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> GetInsightsAsync(
        ApDashboardResult dashboard,
        CancellationToken cancellationToken = default)
    {
        if (!ApDashboardInsightsDefaults.Enabled)
            return Array.Empty<string>();

        var apiUrl = _agentsChat.ResolveChatUrl();
        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            _logger.LogWarning("Agents:ChatUrl is not configured; skipping insights.");
            return Array.Empty<string>();
        }

        try
        {
            var client = _httpClientFactory.CreateClient(nameof(ApDashboardInsightsClient));
            var request = BuildChatRequest(dashboard);
            var requestJson = JsonSerializer.Serialize(request, JsonOptions);
            using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(apiUrl, content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "AP dashboard insights API returned {StatusCode}: {Body}",
                    (int)response.StatusCode,
                    Truncate(body, 500));
                return Array.Empty<string>();
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                _logger.LogWarning("AP dashboard insights API returned an empty body.");
                return Array.Empty<string>();
            }

            using var doc = JsonDocument.Parse(body);
            if (!TryGetInsightsArray(doc.RootElement, out var insightsEl))
            {
                _logger.LogWarning("AP dashboard /chat response missing insight_result.insights array.");
                return Array.Empty<string>();
            }

            var list = new List<string>();
            foreach (var item in insightsEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var text = item.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        list.Add(text);
                }
            }

            return list;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AP dashboard insights API call failed; returning dashboard without insights.");
            return Array.Empty<string>();
        }
    }

    private static object BuildChatRequest(ApDashboardResult dashboard)
    {
        var payload = new Dictionary<string, object?>
        {
            ["insights_count"] = ApDashboardInsightsDefaults.InsightsCount,
            ["insight_area"] = ApDashboardInsightsDefaults.InsightArea,
            ["insight_json"] = dashboard
        };

        return new
        {
            session_id = $"ap-dashboard-{Guid.NewGuid():N}",
            intent = "insight",
            payload
        };
    }

    private static bool TryGetInsightsArray(JsonElement root, out JsonElement insights)
    {
        // Current agents /chat contract.
        if (root.TryGetProperty("insight_result", out var result)
            && result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("insights", out insights)
            && insights.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        // Keep compatibility with the retired /api/v1/insights response.
        if (root.TryGetProperty("insights", out insights)
            && insights.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        insights = default;
        return false;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";
}
