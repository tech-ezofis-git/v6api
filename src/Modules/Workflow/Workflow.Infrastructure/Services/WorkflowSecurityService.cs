using Npgsql;
using Microsoft.Extensions.Logging;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Application.Workflows.Commands.CreateWorkflow;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>Manages workflow security and user assignments.</summary>
public sealed class WorkflowSecurityService : IWorkflowSecurityService
{
    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUserProvider _currentUserProvider;
    private readonly ILogger<WorkflowSecurityService> _logger;

    public WorkflowSecurityService(
        ITenantContext tenantContext,
        ICurrentUserProvider currentUserProvider,
        ILogger<WorkflowSecurityService> logger)
    {
        _tenantContext = tenantContext;
        _currentUserProvider = currentUserProvider;
        _logger = logger;
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
}
