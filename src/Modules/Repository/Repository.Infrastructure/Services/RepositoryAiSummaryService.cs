using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
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
    private readonly IRepositoryItemQueryService _items;
    private readonly ILogger<RepositoryAiSummaryService> _logger;

    public RepositoryAiSummaryService(
        HttpClient httpClient,
        IOptions<AgentsChatOptions> agentsChat,
        ITenantConnectionProvider connectionProvider,
        IStaticRepositoryProvisioner provisioner,
        IRepositoryItemQueryService items,
        ILogger<RepositoryAiSummaryService> logger)
    {
        _httpClient = httpClient;
        _agentsChat = agentsChat.Value;
        _connectionProvider = connectionProvider;
        _provisioner = provisioner;
        _items = items;
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

        HttpResponseMessage response;
        string responseBody;
        string source;

        if (hasOcrText)
        {
            source = "ocr_text";
            (response, responseBody) = await PostChatAsync(
                apiUrl,
                BuildChatRequestJson(tenantId, itemId, filePath: null, ocrText: ocrText),
                cancellationToken);
        }
        else
        {
            var normalizedFilePath = NormalizeSummaryFilePath(filePath!);
            var useBase64Payload = ContainsNonAsciiPath(filePath!);

            if (useBase64Payload)
            {
                source = "file_bytes";
                _logger.LogInformation(
                    "AI summary item {ItemId} has non-ASCII blob path; sending file bytes to Python instead of blob path.",
                    itemId);
                (response, responseBody) = await PostChatWithFileBytesAsync(
                    apiUrl,
                    tenantId,
                    repositoryId,
                    itemId,
                    cancellationToken);
            }
            else
            {
                source = "filepath";
                (response, responseBody) = await PostChatAsync(
                    apiUrl,
                    BuildChatRequestJson(tenantId, itemId, filePath, ocrText: null),
                    cancellationToken);

                if (response.StatusCode == HttpStatusCode.NotFound
                    && !string.Equals(normalizedFilePath, filePath, StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        "AI summary API returned 404 for item {ItemId} with raw filepath; retrying with normalized filepath. Raw={RawFilePath} Normalized={NormalizedFilePath}",
                        itemId,
                        filePath,
                        normalizedFilePath);

                    response.Dispose();
                    (response, responseBody) = await PostChatAsync(
                        apiUrl,
                        BuildChatRequestJson(tenantId, itemId, normalizedFilePath, ocrText: null),
                        cancellationToken);
                }

                if (!response.IsSuccessStatusCode && IsBlobNotFoundResponse(response, responseBody))
                {
                    _logger.LogWarning(
                        "AI summary API could not read blob for item {ItemId}; retrying with file bytes. FilePath={FilePath}",
                        itemId,
                        filePath);

                    response.Dispose();
                    source = "file_bytes";
                    (response, responseBody) = await PostChatWithFileBytesAsync(
                        apiUrl,
                        tenantId,
                        repositoryId,
                        itemId,
                        cancellationToken);
                }
            }
        }

        using (response)
        {
            _logger.LogInformation(
                "Calling AI summary /chat {Url} using {Source} for tenant {TenantId}, repository {RepositoryId}, item {ItemId}",
                apiUrl,
                source,
                tenantId,
                repositoryId,
                itemId);

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
    }

    private async Task<(HttpResponseMessage Response, string Body)> PostChatAsync(
        string apiUrl,
        string requestJson,
        CancellationToken cancellationToken)
    {
        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var response = await _httpClient.PostAsync(apiUrl, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return (response, body);
    }

    private async Task<(HttpResponseMessage Response, string Body)> PostChatWithFileBytesAsync(
        string apiUrl,
        Guid tenantId,
        Guid repositoryId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var fileContent = await _items.OpenItemFileAsync(repositoryId, tenantId, itemId, cancellationToken)
            ?? throw new FileNotFoundException("Repository file not found in storage for AI summary.");

        await using (fileContent.Stream)
        {
            using var buffer = new MemoryStream();
            await fileContent.Stream.CopyToAsync(buffer, cancellationToken);
            if (buffer.Length == 0)
                throw new InvalidOperationException("Repository file is empty.");

            var base64 = Convert.ToBase64String(buffer.ToArray());
            var requestJson = BuildChatRequestJson(
                tenantId,
                itemId,
                filePath: base64,
                ocrText: null,
                fileName: fileContent.FileName,
                includeFileAlias: true);
            return await PostChatAsync(apiUrl, requestJson, cancellationToken);
        }
    }

    private string BuildChatRequestJson(
        Guid tenantId,
        Guid itemId,
        string? filePath,
        string? ocrText,
        string? fileName = null,
        bool includeFileAlias = false)
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
            if (!string.IsNullOrWhiteSpace(fileName))
                payload["filename"] = fileName;
            if (includeFileAlias && !string.IsNullOrWhiteSpace(filePath))
                payload["file"] = filePath;
        }

        var request = new
        {
            session_id = $"repo-summary-{itemId:N}",
            intent = "summary",
            payload
        };

        return JsonSerializer.Serialize(request, JsonOptions);
    }

    private static bool ContainsNonAsciiPath(string filePath) =>
        filePath.Any(static c => c > 127);

    private static bool IsBlobNotFoundResponse(HttpResponseMessage response, string body) =>
        response.StatusCode == HttpStatusCode.NotFound
        && body.Contains("Blob not found", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeSummaryFilePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return string.Empty;

        var normalized = filePath
            .Replace('\\', '/')
            .TrimStart('/')
            .Normalize(NormalizationForm.FormC);

        return string.Join(
            '/',
            normalized
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(segment => segment.Normalize(NormalizationForm.FormC)));
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
