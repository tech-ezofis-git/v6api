using Npgsql;
using SaaSApp.ActivityLog.Application.Contracts;
using SaaSApp.MultiTenancy;

namespace SaaSApp.ActivityLog.Infrastructure.Services;

public sealed class EventLogQueryService : IEventLogQueryService
{
    private readonly ITenantConnectionProvider _connectionProvider;

    public EventLogQueryService(ITenantConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider;
    }

    public async Task<PagedResult<EventLogEntryDto>> ListAsync(
        Guid tenantId,
        ListEventLogsQuery query,
        CancellationToken cancellationToken = default)
    {
        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var offset = (page - 1) * pageSize;

        var where = new List<string>
        {
            "\"TenantId\" = @TenantId",
            "(\"HttpMethod\" IS NULL OR UPPER(\"HttpMethod\") NOT IN ('GET', 'HEAD'))",
            "\"EventType\" NOT LIKE '%Viewed'",
            "\"EventType\" NOT LIKE '%Listed'",
            "(\"Path\" IS NULL OR \"Path\" NOT LIKE '%/search%')"
        };
        var parameters = new List<NpgsqlParameter> { new("@TenantId", tenantId) };

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            where.Add("\"Category\" = @Category");
            parameters.Add(new NpgsqlParameter("@Category", query.Category.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(query.Severity))
        {
            where.Add("\"Severity\" = @Severity");
            parameters.Add(new NpgsqlParameter("@Severity", query.Severity.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(query.UserEmail))
        {
            where.Add("\"UserEmail\" LIKE @UserEmail");
            parameters.Add(new NpgsqlParameter("@UserEmail", $"%{query.UserEmail.Trim()}%"));
        }

        if (query.DateFrom.HasValue)
        {
            where.Add("\"CreatedAtUtc\" >= @DateFrom");
            parameters.Add(new NpgsqlParameter("@DateFrom", query.DateFrom.Value));
        }

        if (query.DateTo.HasValue)
        {
            where.Add("\"CreatedAtUtc\" <= @DateTo");
            parameters.Add(new NpgsqlParameter("@DateTo", query.DateTo.Value));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            where.Add("\"EventTitle\" LIKE @Search");
            parameters.Add(new NpgsqlParameter("@Search", $"%{query.Search.Trim()}%"));
        }

        var whereClause = string.Join(" AND ", where);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var countSql = $"""SELECT COUNT(1) FROM activitylog."EventLogs" WHERE {whereClause}""";
        await using var countCmd = new NpgsqlCommand(countSql, connection);
        AddParameters(countCmd, parameters);
        var total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(cancellationToken) ?? 0);

        var dataSql = $"""
            SELECT "Id", "EventTitle", "EventType", "UserDisplayName", "UserEmail",
                   "Category", "Severity", "IpAddress", "CreatedAtUtc"
            FROM activitylog."EventLogs"
            WHERE {whereClause}
            ORDER BY "CreatedAtUtc" DESC
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
            """;

        await using var dataCmd = new NpgsqlCommand(dataSql, connection);
        AddParameters(dataCmd, parameters);
        dataCmd.Parameters.AddWithValue("@Offset", offset);
        dataCmd.Parameters.AddWithValue("@PageSize", pageSize);

        var list = new List<EventLogEntryDto>();
        await using var reader = await dataCmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new EventLogEntryDto(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetDateTime(8)));
        }

        return new PagedResult<EventLogEntryDto>(list, page, pageSize, total);
    }

    private static void AddParameters(NpgsqlCommand command, IReadOnlyList<NpgsqlParameter> parameters)
    {
        foreach (var p in parameters)
            command.Parameters.Add(new NpgsqlParameter(p.ParameterName, p.Value));
    }
}
