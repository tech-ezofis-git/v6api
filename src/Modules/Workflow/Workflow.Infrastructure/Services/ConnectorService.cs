using Npgsql;
using Microsoft.Extensions.Logging;
using SaaSApp.Workflow.Application.Connectors;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>Connector CRUD against modern dbo."connector" on tenant database.</summary>
public sealed class ConnectorService : IConnectorService
{
    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUserProvider _currentUserProvider;
    private readonly ILogger<ConnectorService> _logger;

    public ConnectorService(
        ITenantContext tenantContext,
        ICurrentUserProvider currentUserProvider,
        ILogger<ConnectorService> logger)
    {
        _tenantContext = tenantContext;
        _currentUserProvider = currentUserProvider;
        _logger = logger;
    }

    public async Task<ConnectorDto> CreateAsync(ConnectorUpsertRequest request, CancellationToken cancellationToken = default)
    {
        var connectionString = RequireConnectionString();
        var userId = RequireUserGuid();
        var tenantId = RequireTenantId();
        var connectorId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var name = string.IsNullOrWhiteSpace(request.Name) ? "Connector" : request.Name.Trim();
        var providerCode = string.IsNullOrWhiteSpace(request.ProviderCode)
            ? throw new InvalidOperationException("ProviderCode is required.")
            : request.ProviderCode.Trim().ToUpperInvariant();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureConnectorSchemaAsync(connection, cancellationToken);

        const string sql = """
            INSERT INTO dbo."connector"
                ("Id", "Name", "ProviderCode", "ConfigJson", "OAuthStatus", "IsDefault", "CreatedAtUtc", "CreatedBy", "IsDeleted")
            VALUES
                (@Id, @Name, @ProviderCode, @ConfigJson, 'Pending', @IsDefault, @Now, @UserId, false);
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", connectorId);
        cmd.Parameters.AddWithValue("@Name", name);
        cmd.Parameters.AddWithValue("@ProviderCode", providerCode);
        cmd.Parameters.AddWithValue("@ConfigJson", (object?)request.ConfigJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@IsDefault", request.IsDefault ?? false);
        cmd.Parameters.AddWithValue("@Now", now);
        cmd.Parameters.AddWithValue("@UserId", userId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogInformation("Created connector {ConnectorId} provider {Provider} tenant {TenantId}",
            connectorId, providerCode, tenantId);
        return (await GetByIdCoreAsync(connection, tenantId, connectorId, cancellationToken))!;
    }

    public async Task<ConnectorDto?> UpdateAsync(
        Guid id,
        ConnectorUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
            return null;

        var connectionString = RequireConnectionString();
        var userId = RequireUserGuid();
        var tenantGuid = RequireTenantId();
        var now = DateTime.UtcNow;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureConnectorSchemaAsync(connection, cancellationToken);

        if (!await ConnectorExistsAsync(connection, id, cancellationToken))
            return null;

        var sets = new List<string> { "\"ModifiedAtUtc\" = @Now", "\"ModifiedBy\" = @UserId" };
        await using var cmd = new NpgsqlCommand { Connection = connection };
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@Now", now);
        cmd.Parameters.AddWithValue("@UserId", userId);

        if (request.Name != null)
        {
            sets.Add("\"Name\" = @Name");
            cmd.Parameters.AddWithValue("@Name", request.Name.Trim());
        }
        if (request.ProviderCode != null)
        {
            sets.Add("\"ProviderCode\" = @ProviderCode");
            cmd.Parameters.AddWithValue("@ProviderCode", request.ProviderCode.Trim().ToUpperInvariant());
        }
        if (request.ConfigJson != null)
        {
            sets.Add("\"ConfigJson\" = @ConfigJson");
            cmd.Parameters.AddWithValue("@ConfigJson", request.ConfigJson);
        }
        if (request.IsDefault.HasValue)
        {
            sets.Add("\"IsDefault\" = @IsDefault");
            cmd.Parameters.AddWithValue("@IsDefault", request.IsDefault.Value);
        }

        cmd.CommandText = $"""UPDATE dbo."connector" SET {string.Join(", ", sets)} WHERE "Id" = @Id AND "IsDeleted" = false;""";
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogInformation("Updated connector {ConnectorId}", id);
        return await GetByIdCoreAsync(connection, tenantGuid, id, cancellationToken);
    }

    public async Task<IReadOnlyList<ConnectorDto>> ListAsync(
        ConnectorListRequest request,
        CancellationToken cancellationToken = default)
    {
        var connectionString = RequireConnectionString();
        var tenantGuid = RequireTenantId();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureConnectorSchemaAsync(connection, cancellationToken);

        var showDeletedOnly = !string.Equals(request.Mode, "browse", StringComparison.OrdinalIgnoreCase);
        var whereParts = new List<string> { showDeletedOnly ? "c.\"IsDeleted\" = true" : "c.\"IsDeleted\" = false" };
        var parameters = new List<NpgsqlParameter>();

        foreach (var group in request.FilterBy ?? new List<ConnectorFilterGroup>())
        {
            if (TryBuildFilterGroup(group, out var groupSql, out var groupParams))
            {
                whereParts.Add($"({groupSql})");
                parameters.AddRange(groupParams);
            }
        }

        var whereSql = string.Join(" AND ", whereParts);
        var sql = $"""
            SELECT
                c."Id", c."Name", c."ProviderCode", c."ConfigJson", c."OAuthStatus",
                c."ExternalAccountEmail", c."TokenExpiresAtUtc", c."IsDefault",
                c."CreatedAtUtc", c."ModifiedAtUtc", c."CreatedBy", c."ModifiedBy", c."IsDeleted",
                cb."Email" AS "CreatedByEmail",
                COALESCE(mb."Email", cb."Email") AS "ModifiedByEmail"
            FROM dbo."connector" c
            LEFT JOIN users."Users" cb ON cb."Id" = c."CreatedBy" AND cb."IsDeleted" = false
            LEFT JOIN users."Users" mb ON mb."Id" = c."ModifiedBy" AND mb."IsDeleted" = false
            WHERE {whereSql}
            ORDER BY c."CreatedAtUtc" DESC, c."Name";
            """;

        var items = new List<ConnectorDto>();
        await using var cmd = new NpgsqlCommand(sql, connection);
        foreach (var p in parameters)
            cmd.Parameters.Add(p);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(MapReader(reader, tenantGuid));

        return items;
    }

    public async Task<ConnectorDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
            return null;

        var connectionString = RequireConnectionString();
        var tenantGuid = RequireTenantId();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        if (!await TableExistsAsync(connection, "connector", cancellationToken))
            return null;

        await EnsureConnectorSchemaAsync(connection, cancellationToken);
        return await GetByIdCoreAsync(connection, tenantGuid, id, cancellationToken);
    }

    private async Task<ConnectorDto?> GetByIdCoreAsync(
        NpgsqlConnection connection,
        Guid tenantId,
        Guid id,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                c."Id", c."Name", c."ProviderCode", c."ConfigJson", c."OAuthStatus",
                c."ExternalAccountEmail", c."TokenExpiresAtUtc", c."IsDefault",
                c."CreatedAtUtc", c."ModifiedAtUtc", c."CreatedBy", c."ModifiedBy", c."IsDeleted",
                cb."Email" AS "CreatedByEmail",
                COALESCE(mb."Email", cb."Email") AS "ModifiedByEmail"
            FROM dbo."connector" c
            LEFT JOIN users."Users" cb ON cb."Id" = c."CreatedBy" AND cb."IsDeleted" = false
            LEFT JOIN users."Users" mb ON mb."Id" = c."ModifiedBy" AND mb."IsDeleted" = false
            WHERE c."IsDeleted" = false AND c."Id" = @Id;
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapReader(reader, tenantId) : null;
    }

    private static ConnectorDto MapReader(NpgsqlDataReader reader, Guid tenantId) =>
        new(
            Id: reader.GetGuid(0),
            TenantId: tenantId,
            Name: reader.GetString(1),
            ProviderCode: reader.GetString(2),
            ConfigJson: reader.IsDBNull(3) ? null : reader.GetString(3),
            OAuthStatus: reader.IsDBNull(4) ? "Pending" : reader.GetString(4),
            ExternalAccountEmail: reader.IsDBNull(5) ? null : reader.GetString(5),
            TokenExpiresAtUtc: reader.IsDBNull(6) ? null : reader.GetDateTime(6),
            IsDefault: !reader.IsDBNull(7) && reader.GetBoolean(7),
            CreatedAtUtc: reader.GetDateTime(8),
            ModifiedAtUtc: reader.IsDBNull(9) ? null : reader.GetDateTime(9),
            CreatedBy: reader.GetGuid(10),
            ModifiedBy: reader.IsDBNull(11) ? null : reader.GetGuid(11),
            IsDeleted: !reader.IsDBNull(12) && reader.GetBoolean(12),
            CreatedByEmail: reader.IsDBNull(13) ? null : reader.GetString(13),
            ModifiedByEmail: reader.IsDBNull(14) ? null : reader.GetString(14));

    private static bool TryBuildFilterGroup(
        ConnectorFilterGroup group,
        out string sql,
        out List<NpgsqlParameter> parameters)
    {
        sql = string.Empty;
        parameters = new List<NpgsqlParameter>();
        var parts = new List<string>();
        var joiner = string.Equals(group.GroupCondition, "OR", StringComparison.OrdinalIgnoreCase) ? " OR " : " AND ";
        var i = 0;

        foreach (var filter in group.Filters)
        {
            var column = MapFilterColumn(filter.Criteria);
            if (column == null)
                continue;

            var pName = $"@f{parameters.Count}_{i++}";
            var condition = (filter.Condition ?? "contains").Trim().ToLowerInvariant();
            switch (condition)
            {
                case "contains" or "like":
                    parts.Add($"{column} LIKE {pName}");
                    parameters.Add(new NpgsqlParameter(pName, $"%{filter.Value}%"));
                    break;
                case "eq" or "=" or "equal":
                    parts.Add($"{column} = {pName}");
                    parameters.Add(new NpgsqlParameter(pName, filter.Value));
                    break;
                case "neq" or "!=" or "notequal":
                    parts.Add($"{column} <> {pName}");
                    parameters.Add(new NpgsqlParameter(pName, filter.Value));
                    break;
            }
        }

        if (parts.Count == 0)
            return false;

        sql = string.Join(joiner, parts);
        return true;
    }

    private static string? MapFilterColumn(string criteria) =>
        (criteria ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "name" => "c.\"Name\"",
            "providercode" or "provider" or "connectortype" or "connector_type" => "c.\"ProviderCode\"",
            "oauthstatus" or "status" => "c.\"OAuthStatus\"",
            "createdby" => "c.\"CreatedBy\"::text",
            "modifiedby" => "c.\"ModifiedBy\"::text",
            _ => null
        };

    private static async Task<bool> ConnectorExistsAsync(NpgsqlConnection connection, Guid id, CancellationToken cancellationToken)
    {
        const string sql = """SELECT COUNT(1) FROM dbo."connector" WHERE "Id" = @Id AND "IsDeleted" = false;""";
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Id", id);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task EnsureConnectorSchemaAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "connector", cancellationToken))
        {
            const string create = """
                CREATE SCHEMA IF NOT EXISTS dbo;
                CREATE TABLE dbo."connector" (
                    "Id" uuid NOT NULL PRIMARY KEY,
                    "Name" varchar(256) NOT NULL,
                    "ProviderCode" varchar(64) NOT NULL,
                    "ConfigJson" text NULL,
                    "AccessToken" text NULL,
                    "RefreshToken" text NULL,
                    "TokenExpiresAtUtc" timestamptz NULL,
                    "ExternalAccountEmail" varchar(320) NULL,
                    "ExternalAccountId" varchar(256) NULL,
                    "OAuthStatus" varchar(32) NOT NULL DEFAULT 'Pending',
                    "IsDefault" boolean NOT NULL DEFAULT false,
                    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
                    "ModifiedAtUtc" timestamptz NULL,
                    "CreatedBy" uuid NOT NULL,
                    "ModifiedBy" uuid NULL,
                    "IsDeleted" boolean NOT NULL DEFAULT false
                );
                CREATE INDEX IF NOT EXISTS "IX_connector_IsDeleted" ON dbo."connector" ("IsDeleted");
                CREATE INDEX IF NOT EXISTS "IX_connector_ProviderCode" ON dbo."connector" ("ProviderCode") WHERE "IsDeleted" = false;
                """;
            await using var cmd = new NpgsqlCommand(create, connection) { CommandTimeout = 120 };
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        if (await HasColumnAsync(connection, "connector", "ProviderCode", cancellationToken))
            return;

        throw new InvalidOperationException(
            "dbo.\"connector\" is on a legacy schema. Run scripts/postgres/Create-Connector-Table.sql on the tenant database to migrate.");
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, string tableName, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(1)
            FROM information_schema.tables
            WHERE table_schema = 'dbo' AND table_name = @TableName;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TableName", tableName);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task<bool> HasColumnAsync(
        NpgsqlConnection connection,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(1)
            FROM information_schema.columns
            WHERE table_schema = 'dbo' AND table_name = @TableName AND column_name = @ColumnName;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TableName", tableName);
        cmd.Parameters.AddWithValue("@ColumnName", columnName);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private string RequireConnectionString() =>
        _tenantContext.ConnectionString
        ?? throw new InvalidOperationException("Tenant connection string not resolved.");

    private Guid RequireTenantId() =>
        _tenantContext.TenantId
        ?? throw new InvalidOperationException("Tenant context is required.");

    private Guid RequireUserGuid() =>
        _currentUserProvider.GetUserId()
        ?? throw new InvalidOperationException("User context is required.");
}
