using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    private readonly ApDashboardInsightsOptions _options;
    private readonly ILogger<ApDashboardInsightsClient> _logger;

    public ApDashboardInsightsClient(
        IHttpClientFactory httpClientFactory,
        IOptions<ApDashboardInsightsOptions> options,
        ILogger<ApDashboardInsightsClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> GetInsightsAsync(
        ApDashboardResult dashboard,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return Array.Empty<string>();

        var apiUrl = _options.ApiUrl?.Trim();
        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            _logger.LogWarning("ApDashboard:Insights:ApiUrl is not configured; skipping insights.");
            return Array.Empty<string>();
        }

        try
        {
            var client = _httpClientFactory.CreateClient(nameof(ApDashboardInsightsClient));
            using var response = await client.PostAsJsonAsync(apiUrl, dashboard, JsonOptions, cancellationToken);
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
            if (!doc.RootElement.TryGetProperty("insights", out var insightsEl)
                || insightsEl.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("AP dashboard insights API response missing insights array.");
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

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";
}
