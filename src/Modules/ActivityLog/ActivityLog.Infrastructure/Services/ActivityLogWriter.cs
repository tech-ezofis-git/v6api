using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SaaSApp.ActivityLog.Application.Contracts;

namespace SaaSApp.ActivityLog.Infrastructure.Services;

public sealed class ActivityLogWriter : IActivityLogWriter
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ActivityLogWriter> _logger;

    public ActivityLogWriter(IServiceScopeFactory scopeFactory, ILogger<ActivityLogWriter> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public void Enqueue(ActivityLogEntry entry, string connectionString)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var inserter = scope.ServiceProvider.GetRequiredService<ActivityLogInsertService>();
                await inserter.InsertAsync(entry, connectionString, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write activity log for {Path}", entry.Path);
            }
        });
    }
}

public sealed class ActivityLogInsertService
{
    public async Task InsertAsync(ActivityLogEntry entry, string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            INSERT INTO activitylog."ApiAccessLogs" (
                "Id", "TenantId", "UserId", "UserEmail", "HttpMethod", "Path", "QueryString",
                "StatusCode", "DurationMs", "CorrelationId", "ClientIp", "UserAgent", "CreatedAtUtc")
            VALUES (
                @Id, @TenantId, @UserId, @UserEmail, @HttpMethod, @Path, @QueryString,
                @StatusCode, @DurationMs, @CorrelationId, @ClientIp, @UserAgent, @CreatedAtUtc)
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Id", entry.Id);
        command.Parameters.AddWithValue("@TenantId", entry.TenantId);
        command.Parameters.AddWithValue("@UserId", (object?)entry.UserId ?? DBNull.Value);
        command.Parameters.AddWithValue("@UserEmail", (object?)entry.UserEmail ?? DBNull.Value);
        command.Parameters.AddWithValue("@HttpMethod", entry.HttpMethod);
        command.Parameters.AddWithValue("@Path", entry.Path);
        command.Parameters.AddWithValue("@QueryString", (object?)entry.QueryString ?? DBNull.Value);
        command.Parameters.AddWithValue("@StatusCode", entry.StatusCode);
        command.Parameters.AddWithValue("@DurationMs", entry.DurationMs);
        command.Parameters.AddWithValue("@CorrelationId", (object?)entry.CorrelationId ?? DBNull.Value);
        command.Parameters.AddWithValue("@ClientIp", (object?)entry.ClientIp ?? DBNull.Value);
        command.Parameters.AddWithValue("@UserAgent", (object?)entry.UserAgent ?? DBNull.Value);
        command.Parameters.AddWithValue("@CreatedAtUtc", entry.CreatedAtUtc);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
