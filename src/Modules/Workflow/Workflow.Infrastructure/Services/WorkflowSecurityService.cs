using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Application.Workflows.Commands.CreateWorkflow;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>Manages workflow security and user assignments.</summary>
public sealed class WorkflowSecurityService : IWorkflowSecurityService
{
    private const string DefaultPilotEmail = "pilot@ezofis.com";

    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUserProvider _currentUserProvider;
    private readonly ILogger<WorkflowSecurityService> _logger;
    private readonly string _pilotEmail;
    private Guid _unrestrictedCacheUserId;
    private bool? _unrestrictedCache;

    public WorkflowSecurityService(
        ITenantContext tenantContext,
        ICurrentUserProvider currentUserProvider,
        ILogger<WorkflowSecurityService> logger,
        IConfiguration? configuration = null)
    {
        _tenantContext = tenantContext;
        _currentUserProvider = currentUserProvider;
        _logger = logger;
        var configured = configuration?["TenantPilotUser:Email"]?.Trim();
        _pilotEmail = string.IsNullOrWhiteSpace(configured) ? DefaultPilotEmail : configured;
    }

    public async Task EnsureDefaultWorkflowSecurityAsync(
        Guid workflowId,
        CancellationToken cancellationToken = default)
    {
        var connectionString = _tenantContext.ConnectionString;
        var tenantId = _tenantContext.TenantId;
        var userId = _currentUserProvider.GetUserId();
        if (string.IsNullOrEmpty(connectionString) || tenantId == null || userId == null)
            return;

        var now = DateTime.UtcNow;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // Default row in WorkflowUsers (old API parity).
        const string usersSql = """
            INSERT INTO workflow."WorkflowUsers"
                ("TenantId", "WorkflowId", "UserId", "GroupId", "UserCategory", "CreatedAtUtc", "ModifiedAtUtc", "CreatedBy", "ModifiedBy", "IsDeleted")
            SELECT @TenantId, @WorkflowId, @UserId, NULL, NULL, @CreatedAt, NULL, @CreatedBy, NULL, false
            WHERE NOT EXISTS (
                SELECT 1 FROM workflow."WorkflowUsers"
                WHERE "WorkflowId" = @WorkflowId AND "UserId" = @UserId AND "IsDeleted" = false
            );
            """;

        await using (var command = new NpgsqlCommand(usersSql, connection))
        {
            command.Parameters.AddWithValue("@TenantId", tenantId.Value);
            command.Parameters.AddWithValue("@WorkflowId", workflowId);
            command.Parameters.AddWithValue("@UserId", userId.Value);
            command.Parameters.AddWithValue("@CreatedAt", now);
            command.Parameters.AddWithValue("@CreatedBy", userId.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // Default row in WorkflowSecurity (old API parity).
        const string securitySql = """
            INSERT INTO workflow."WorkflowSecurity"
                ("TenantId", "WorkflowId", "UserId", "UserCategory", "CreatedAtUtc", "ModifiedAtUtc", "CreatedBy", "ModifiedBy", "IsDeleted")
            SELECT @TenantId, @WorkflowId, @UserId, NULL, @CreatedAt, NULL, @CreatedBy, NULL, false
            WHERE NOT EXISTS (
                SELECT 1 FROM workflow."WorkflowSecurity"
                WHERE "WorkflowId" = @WorkflowId AND "UserId" = @UserId AND "IsDeleted" = false
            );
            """;

        await using (var command = new NpgsqlCommand(securitySql, connection))
        {
            command.Parameters.AddWithValue("@TenantId", tenantId.Value);
            command.Parameters.AddWithValue("@WorkflowId", workflowId);
            command.Parameters.AddWithValue("@UserId", userId.Value);
            command.Parameters.AddWithValue("@CreatedAt", now);
            command.Parameters.AddWithValue("@CreatedBy", userId.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task SetWorkflowSecurityAsync(
        Guid workflowId,
        string[]? coordinators,
        string[]? superusers,
        List<WorkflowBlockDto> blocks,
        CancellationToken cancellationToken = default)
    {
        var connectionString = _tenantContext.ConnectionString;
        var tenantId = _tenantContext.TenantId;
        if (string.IsNullOrEmpty(connectionString))
            return;
        if (tenantId == null)
            return;

        var userId = _currentUserProvider.GetUserId();
        if (userId == null)
            return;

        var currentTime = DateTime.UtcNow;

        // Collect unique users and groups from blocks
        var userList = new HashSet<string>();
        var groupList = new HashSet<string>();

        if (blocks != null)
        {
            foreach (var block in blocks)
            {
                if (block.Settings.Users != null)
                {
                    foreach (var user in block.Settings.Users)
                    {
                        if (!string.IsNullOrEmpty(user))
                            userList.Add(user);
                    }
                }

                if (block.Settings.Groups != null)
                {
                    foreach (var group in block.Settings.Groups)
                    {
                        if (!string.IsNullOrEmpty(group))
                            groupList.Add(group);
                    }
                }
            }
        }

        // Add coordinators and superusers
        if (coordinators != null)
        {
            foreach (var coordinator in coordinators)
            {
                if (!string.IsNullOrEmpty(coordinator))
                    userList.Add(coordinator);
            }
        }

        if (superusers != null)
        {
            foreach (var superuser in superusers)
            {
                if (!string.IsNullOrEmpty(superuser))
                    userList.Add(superuser);
            }
        }

        // Insert workflow users
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        foreach (var user in userList)
        {
            if (Guid.TryParse(user, out var userIdGuid))
            {
                const string sql = """
                    INSERT INTO workflow."WorkflowUsers" ("TenantId", "WorkflowId", "UserId", "GroupId", "UserCategory", "CreatedAtUtc", "ModifiedAtUtc", "CreatedBy", "ModifiedBy", "IsDeleted")
                    SELECT @TenantId, @WorkflowId, @UserId, NULL, NULL, @CreatedAt, NULL, @CreatedBy, NULL, false
                    WHERE NOT EXISTS (
                        SELECT 1 FROM workflow."WorkflowUsers"
                        WHERE "WorkflowId" = @WorkflowId AND "UserId" = @UserId AND "IsDeleted" = false
                    );
                    """;

                await using var command = new NpgsqlCommand(sql, connection);
                command.Parameters.AddWithValue("@TenantId", tenantId.Value);
                command.Parameters.AddWithValue("@WorkflowId", workflowId);
                command.Parameters.AddWithValue("@UserId", userIdGuid);
                command.Parameters.AddWithValue("@CreatedAt", currentTime);
                command.Parameters.AddWithValue("@CreatedBy", userId.Value);
                await command.ExecuteNonQueryAsync(cancellationToken);

                const string securitySql = """
                    INSERT INTO workflow."WorkflowSecurity" ("TenantId", "WorkflowId", "UserId", "UserCategory", "CreatedAtUtc", "ModifiedAtUtc", "CreatedBy", "ModifiedBy", "IsDeleted")
                    SELECT @TenantId, @WorkflowId, @UserId, NULL, @CreatedAt, NULL, @CreatedBy, NULL, false
                    WHERE NOT EXISTS (
                        SELECT 1 FROM workflow."WorkflowSecurity"
                        WHERE "WorkflowId" = @WorkflowId AND "UserId" = @UserId AND "IsDeleted" = false
                    );
                    """;
                await using var securityCommand = new NpgsqlCommand(securitySql, connection);
                securityCommand.Parameters.AddWithValue("@TenantId", tenantId.Value);
                securityCommand.Parameters.AddWithValue("@WorkflowId", workflowId);
                securityCommand.Parameters.AddWithValue("@UserId", userIdGuid);
                securityCommand.Parameters.AddWithValue("@CreatedAt", currentTime);
                securityCommand.Parameters.AddWithValue("@CreatedBy", userId.Value);
                await securityCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        // Insert workflow groups
        foreach (var group in groupList)
        {
            if (int.TryParse(group, out var groupId))
            {
                const string sql = """
                    INSERT INTO workflow."WorkflowUsers" ("TenantId", "WorkflowId", "UserId", "GroupId", "UserCategory", "CreatedAtUtc", "ModifiedAtUtc", "CreatedBy", "ModifiedBy", "IsDeleted")
                    SELECT @TenantId, @WorkflowId, NULL, @GroupId, NULL, @CreatedAt, NULL, @CreatedBy, NULL, false
                    WHERE NOT EXISTS (
                        SELECT 1 FROM workflow."WorkflowUsers"
                        WHERE "WorkflowId" = @WorkflowId AND "GroupId" = @GroupId AND "IsDeleted" = false
                    );
                    """;

                await using var command = new NpgsqlCommand(sql, connection);
                command.Parameters.AddWithValue("@TenantId", tenantId.Value);
                command.Parameters.AddWithValue("@WorkflowId", workflowId);
                command.Parameters.AddWithValue("@GroupId", groupId);
                command.Parameters.AddWithValue("@CreatedAt", currentTime);
                command.Parameters.AddWithValue("@CreatedBy", userId.Value);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        _logger.LogInformation("Workflow security set for workflow {WorkflowId}", workflowId);
    }

    public async Task SetWorkflowUsersByDomainAsync(
        Guid workflowId,
        string[] domains,
        CancellationToken cancellationToken = default)
    {
        var connectionString = _tenantContext.ConnectionString;
        if (string.IsNullOrEmpty(connectionString) || domains == null || domains.Length == 0)
            return;

        var userId = _currentUserProvider.GetUserId();
        if (userId == null)
            return;

        var currentTime = DateTime.UtcNow;
        var tenantId = _tenantContext.TenantId;
        if (tenantId == null)
            return;

        // Build domain filter
        var domainConditions = string.Join(" OR ", domains.Select(d => $"\"Email\" LIKE '%@{d}'"));

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // Update existing deleted users
        var updateSql = $@"
            UPDATE workflow.""WorkflowUsers""
            SET ""IsDeleted"" = false
            WHERE ""WorkflowId"" = @WorkflowId
            AND ""UserId"" IN (
                SELECT ""Id"" FROM users.""Users""
                WHERE ""IsDeleted"" = false
                AND ({domainConditions})
                AND ""Id"" IN (SELECT ""UserId"" FROM workflow.""WorkflowUsers"" WHERE ""WorkflowId"" = @WorkflowId AND ""IsDeleted"" = true)
            );";

        await using var updateCommand = new NpgsqlCommand(updateSql, connection);
        updateCommand.Parameters.AddWithValue("@WorkflowId", workflowId);
        await updateCommand.ExecuteNonQueryAsync(cancellationToken);

        // Insert new users
        var insertSql = $@"
            INSERT INTO workflow.""WorkflowUsers"" (""TenantId"", ""WorkflowId"", ""UserId"", ""GroupId"", ""CreatedAtUtc"", ""CreatedBy"", ""IsDeleted"")
            SELECT @TenantId, @WorkflowId, ""Id"", 0, @CreatedAt, @CreatedBy, false
            FROM users.""Users""
            WHERE ""IsDeleted"" = false
            AND ({domainConditions})
            AND ""Id"" NOT IN (SELECT ""UserId"" FROM workflow.""WorkflowUsers"" WHERE ""WorkflowId"" = @WorkflowId AND ""IsDeleted"" = false);";

        await using var insertCommand = new NpgsqlCommand(insertSql, connection);
        insertCommand.Parameters.AddWithValue("@TenantId", tenantId.Value);
        insertCommand.Parameters.AddWithValue("@WorkflowId", workflowId);
        insertCommand.Parameters.AddWithValue("@CreatedAt", currentTime);
        insertCommand.Parameters.AddWithValue("@CreatedBy", userId.Value);
        await insertCommand.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogInformation("Workflow users set by domain for workflow {WorkflowId}", workflowId);
    }

    public Task<bool> UserSeesAllWorkflowsAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        IsUnrestrictedUserAsync(userId, cancellationToken);

    public async Task<bool> CanAccessWorkflowAsync(
        Guid workflowId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (await IsUnrestrictedUserAsync(userId, cancellationToken))
            return true;

        var connectionString = _tenantContext.ConnectionString;
        if (string.IsNullOrEmpty(connectionString) || userId == Guid.Empty || workflowId == Guid.Empty)
            return false;

        const string sql = """
            SELECT 1
            FROM workflow."Workflows" w
            WHERE w."Id" = @WorkflowId
              AND w."IsDeleted" = false
              AND (
                    w."CreatedBy" = @UserId
                    OR EXISTS (
                        SELECT 1 FROM workflow."WorkflowSecurity" s
                        WHERE s."WorkflowId" = w."Id" AND s."UserId" = @UserId AND s."IsDeleted" = false)
                    OR EXISTS (
                        SELECT 1 FROM workflow."WorkflowUsers" u
                        WHERE u."WorkflowId" = w."Id" AND u."UserId" = @UserId AND u."IsDeleted" = false)
                  )
            LIMIT 1
            """;

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("@WorkflowId", workflowId);
            command.Parameters.AddWithValue("@UserId", userId);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result != null && result != DBNull.Value;
        }
        catch (PostgresException)
        {
            return false;
        }
    }

    public async Task<IReadOnlySet<Guid>> GetAccessibleWorkflowIdsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var ids = new HashSet<Guid>();
        var connectionString = _tenantContext.ConnectionString;
        if (string.IsNullOrEmpty(connectionString) || userId == Guid.Empty)
            return ids;

        const string sql = """
            SELECT w."Id"
            FROM workflow."Workflows" w
            WHERE w."IsDeleted" = false
              AND (
                    w."CreatedBy" = @UserId
                    OR EXISTS (
                        SELECT 1 FROM workflow."WorkflowSecurity" s
                        WHERE s."WorkflowId" = w."Id" AND s."UserId" = @UserId AND s."IsDeleted" = false)
                    OR EXISTS (
                        SELECT 1 FROM workflow."WorkflowUsers" u
                        WHERE u."WorkflowId" = w."Id" AND u."UserId" = @UserId AND u."IsDeleted" = false)
                  )
            """;

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("@UserId", userId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                ids.Add(reader.GetGuid(0));
        }
        catch (PostgresException)
        {
            return ids;
        }

        return ids;
    }

    private async Task<bool> IsUnrestrictedUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty)
            return false;
        if (_unrestrictedCache is not null && _unrestrictedCacheUserId == userId)
            return _unrestrictedCache.Value;

        var unrestricted = false;
        var connectionString = _tenantContext.ConnectionString;
        var tenantId = _tenantContext.TenantId;
        if (!string.IsNullOrEmpty(connectionString) && tenantId is Guid tid)
        {
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var cmd = new NpgsqlCommand("""
                    SELECT "Role", "Email"
                    FROM users."Users"
                    WHERE "Id" = @UserId
                      AND "TenantId" = @TenantId
                      AND "IsDeleted" = false
                    LIMIT 1
                    """, connection);
                cmd.Parameters.AddWithValue("@UserId", userId);
                cmd.Parameters.AddWithValue("@TenantId", tid);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    var role = reader.IsDBNull(0) ? null : reader.GetString(0);
                    var email = reader.IsDBNull(1) ? null : reader.GetString(1);
                    unrestricted = string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(role, "Administrator", StringComparison.OrdinalIgnoreCase)
                        || (!string.IsNullOrWhiteSpace(_pilotEmail)
                            && string.Equals(email?.Trim(), _pilotEmail, StringComparison.OrdinalIgnoreCase));
                }
            }
            catch (PostgresException)
            {
                unrestricted = false;
            }
        }

        _unrestrictedCacheUserId = userId;
        _unrestrictedCache = unrestricted;
        return unrestricted;
    }
}
