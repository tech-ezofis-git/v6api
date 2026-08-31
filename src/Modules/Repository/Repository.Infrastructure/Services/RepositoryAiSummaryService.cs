using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SaaSApp.MultiTenancy;
using SaaSApp.Repository.Application.Contracts;
using SaaSApp.Repository.Infrastructure.Options;
using SaaSApp.SharedKernel.Options;

namespace SaaSApp.Repository.Infrastructure.Services;

public sealed class RepositoryAiSummaryService : IRepositoryAiSummaryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly AgentsChatOptions _agentsChat;
    private readonly ITenantConnectionProvider _connectionProvider;
    private readonly IStaticRepositoryProvisioner _provisioner;
    private readonly ILogger<RepositoryAiSummaryService> _logger;

    public RepositoryAiSummaryService(
        HttpClient httpClient,
        IOptions<AgentsChatOptions> agentsChat,
        ITenantConnectionProvider connectionProvider,
        IStaticRepositoryProvisioner provisioner,
        ILogger<RepositoryAiSummaryService> logger)
    {
        _httpClient = httpClient;
        _agentsChat = agentsChat.Value;
        _connectionProvider = connectionProvider;
        _provisioner = provisioner;
        _logger = logger;
        _httpClient.Timeout = TimeSpan.FromMinutes(RepositoryAiSummaryDefaults.TimeoutMinutes);
    }

    public async Task<AiSummaryResult> GetOrGenerateAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        var repo = await _provisioner.GetRepositoryAsync(repositoryId, tenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Repository not found.");

        if (!RepositorySqlHelper.IsValidItemsTableName(repo.ItemsTableName))
            throw new InvalidOperationException("Invalid items table.");

        var table = RepositorySqlHelper.QualifiedItemsTable(repo.ItemsTableName);
        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        string? filePath;
        string? ocrText;
        string? summaryJson;
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            var selectSql = $"""
                SELECT file_path, ocr_text, summary_json
                FROM {table}
                WHERE id = @ItemId AND repository_id = @RepositoryId AND is_deleted = false;
                """;

            await using var command = new NpgsqlCommand(selectSql, connection);
            command.Parameters.AddWithValue("@ItemId", itemId);
            command.Parameters.AddWithValue("@RepositoryId", repositoryId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new KeyNotFoundException("Repository item not found.");

            filePath = reader.IsDBNull(0) ? null : reader.GetString(0);
            ocrText = reader.IsDBNull(1) ? null : reader.GetString(1);
            summaryJson = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        if (!string.IsNullOrWhiteSpace(summaryJson))
            return new AiSummaryResult(summaryJson, WasGenerated: false);

        var hasOcrText = !string.IsNullOrWhiteSpace(ocrText);
        if (!hasOcrText && string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("Repository item does not have OCR text or a file path.");

        var apiUrl = _agentsChat.ResolveChatUrl();
        if (string.IsNullOrWhiteSpace(apiUrl))
            throw new HttpRequestException("Agents:ChatUrl is not configured.");

        var requestJson = BuildChatRequestJson(tenantId, itemId, filePath, hasOcrText ? ocrText : null);
        var source = hasOcrText ? "ocr_text" : "filepath";

        _logger.LogInformation(
            "Calling AI summary /chat {Url} using {Source} for tenant {TenantId}, repository {RepositoryId}, item {ItemId}",
            apiUrl,
            source,
            tenantId,
            repositoryId,
            itemId);

        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _httpClient.PostAsync(apiUrl, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "AI summary /chat returned {StatusCode} for item {ItemId}: {Body}",
                (int)response.StatusCode,
                itemId,
                Truncate(responseBody, 500));
            throw new HttpRequestException(
                $"AI summary API failed ({(int)response.StatusCode}): {Truncate(responseBody, 500)}",
                null,
                response.StatusCode);
        }

        if (string.IsNullOrWhiteSpace(responseBody))
            throw new HttpRequestException("AI summary API returned an empty response.");

        var output = ExtractSummaryOutput(responseBody);

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            var updateSql = $"""
                UPDATE {table}
                SET summary_json = @SummaryJson, modified_at_utc = now()
                WHERE id = @ItemId AND repository_id = @RepositoryId AND is_deleted = false;
                """;

            await using var command = new NpgsqlCommand(updateSql, connection);
            command.Parameters.AddWithValue("@SummaryJson", output);
            command.Parameters.AddWithValue("@ItemId", itemId);
            command.Parameters.AddWithValue("@RepositoryId", repositoryId);
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
                throw new KeyNotFoundException("Repository item not found.");
        }

        return new AiSummaryResult(output, WasGenerated: true);
    }

    private string BuildChatRequestJson(Guid tenantId, Guid itemId, string? filePath, string? ocrText)
    {
        var payload = new Dictionary<string, object?>
        {
            ["tenant_id"] = tenantId.ToString("D"),
            ["key_facts_count"] = RepositoryAiSummaryDefaults.KeyFactsCount
        };

        if (!string.IsNullOrWhiteSpace(ocrText))
        {
            payload["ocr_text"] = ocrText;
        }
        else
        {
            payload["filepath"] = filePath;
            payload["pageno"] = RepositoryAiSummaryDefaults.PageNo;
        }

        var request = new
        {
            session_id = $"repo-summary-{itemId:N}",
            intent = "summary",
            payload
        };

        return JsonSerializer.Serialize(request, JsonOptions);
    }

    private static string ExtractSummaryOutput(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            if (root.TryGetProperty("summary_result", out var summaryResult)
                && summaryResult.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                return summaryResult.GetRawText();
            }

            if (root.TryGetProperty("reply", out var reply)
                && reply.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(reply.GetString()))
            {
                return reply.GetString()!;
            }
        }
        catch (JsonException)
        {
            // Keep the raw body when the agent returns non-JSON.
        }

        return responseBody;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";
}
