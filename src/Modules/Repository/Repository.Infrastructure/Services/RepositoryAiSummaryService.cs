using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SaaSApp.MultiTenancy;
using SaaSApp.Repository.Application.Contracts;
using SaaSApp.Repository.Infrastructure.Options;

namespace SaaSApp.Repository.Infrastructure.Services;

public sealed class RepositoryAiSummaryService : IRepositoryAiSummaryService
{
    private readonly HttpClient _httpClient;
    private readonly RepositoryAiSummaryOptions _options;
    private readonly ITenantConnectionProvider _connectionProvider;
    private readonly IStaticRepositoryProvisioner _provisioner;
    private readonly ILogger<RepositoryAiSummaryService> _logger;

    public RepositoryAiSummaryService(
        HttpClient httpClient,
        IOptions<RepositoryAiSummaryOptions> options,
        ITenantConnectionProvider connectionProvider,
        IStaticRepositoryProvisioner provisioner,
        ILogger<RepositoryAiSummaryService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _connectionProvider = connectionProvider;
        _provisioner = provisioner;
        _logger = logger;
        _httpClient.Timeout = TimeSpan.FromMinutes(Math.Clamp(_options.TimeoutMinutes, 1, 30));
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
        string? summaryJson;
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            var selectSql = $"""
                SELECT FilePath, SummaryJson
                FROM {table}
                WHERE Id = @ItemId AND RepositoryId = @RepositoryId AND IsDeleted = 0;
                """;

            await using var command = new SqlCommand(selectSql, connection);
            command.Parameters.AddWithValue("@ItemId", itemId);
            command.Parameters.AddWithValue("@RepositoryId", repositoryId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new KeyNotFoundException("Repository item not found.");

            filePath = reader.IsDBNull(0) ? null : reader.GetString(0);
            summaryJson = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        if (!string.IsNullOrWhiteSpace(summaryJson))
            return new AiSummaryResult(summaryJson, WasGenerated: false);

        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("Repository item does not have a file path.");

        var apiUrl = _options.ApiUrl?.Trim();
        if (string.IsNullOrWhiteSpace(apiUrl))
            throw new HttpRequestException("Repository:AiSummary:ApiUrl is not configured.");

        _logger.LogInformation(
            "Calling AI summary API {Url} for tenant {TenantId}, repository {RepositoryId}, item {ItemId}",
            apiUrl,
            tenantId,
            repositoryId,
            itemId);

        using var response = await _httpClient.PostAsJsonAsync(
            apiUrl,
            new { tenantId, filepath = filePath },
            cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "AI summary API returned {StatusCode} for item {ItemId}: {Body}",
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

        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            var updateSql = $"""
                UPDATE {table}
                SET SummaryJson = @SummaryJson, ModifiedAtUtc = SYSUTCDATETIME()
                WHERE Id = @ItemId AND RepositoryId = @RepositoryId AND IsDeleted = 0;
                """;

            await using var command = new SqlCommand(updateSql, connection);
            command.Parameters.AddWithValue("@SummaryJson", responseBody);
            command.Parameters.AddWithValue("@ItemId", itemId);
            command.Parameters.AddWithValue("@RepositoryId", repositoryId);
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
                throw new KeyNotFoundException("Repository item not found.");
        }

        return new AiSummaryResult(responseBody, WasGenerated: true);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";
}
