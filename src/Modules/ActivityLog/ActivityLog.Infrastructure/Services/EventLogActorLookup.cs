using Microsoft.Data.SqlClient;

namespace SaaSApp.ActivityLog.Infrastructure.Services;

/// <summary>Best-effort tenant lookups for Event Log title enrichment. Never throws.</summary>
public static class EventLogActorLookup
{
    public static async Task<(string? DisplayName, string? Email, string? LoginName)> TryGetUserAsync(
        string? connectionString,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString) || userId == Guid.Empty)
            return (null, null, null);

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            const string sql = """
                SELECT TOP 1 DisplayName, Email, LoginName
                FROM users.Users
                WHERE Id = @Id
                """;

            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 5 };
            command.Parameters.AddWithValue("@Id", userId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return (null, null, null);

            var displayName = ReadTrimmed(reader, 0);
            var email = ReadTrimmed(reader, 1);
            var loginName = ReadTrimmed(reader, 2);
            return (displayName, email, loginName);
        }
        catch
        {
            return (null, null, null);
        }
    }

    public static Task<string?> TryGetRoleNameAsync(
        string? connectionString,
        Guid roleId,
        CancellationToken cancellationToken = default) =>
        TryGetNameByGuidAsync(
            connectionString,
            """
            SELECT TOP 1 Name
            FROM users.Roles
            WHERE Id = @Id AND IsDeleted = 0
            """,
            roleId,
            cancellationToken);

    public static Task<string?> TryGetGroupNameAsync(
        string? connectionString,
        Guid groupId,
        CancellationToken cancellationToken = default) =>
        TryGetNameByGuidAsync(
            connectionString,
            """
            SELECT TOP 1 Name
            FROM users.Groups
            WHERE Id = @Id AND IsDeleted = 0
            """,
            groupId,
            cancellationToken);

    public static Task<string?> TryGetWorkflowNameAsync(
        string? connectionString,
        Guid workflowId,
        CancellationToken cancellationToken = default) =>
        TryGetNameByGuidAsync(
            connectionString,
            """
            SELECT TOP 1 Name
            FROM workflow.Workflows
            WHERE Id = @Id
            """,
            workflowId,
            cancellationToken);

    public static Task<string?> TryGetWorkflowNameByInstanceIdAsync(
        string? connectionString,
        Guid instanceId,
        CancellationToken cancellationToken = default) =>
        TryGetNameByGuidAsync(
            connectionString,
            """
            SELECT TOP 1 w.Name
            FROM workflow.Workflows w
            INNER JOIN workflow.WorkflowInstanceLookup l ON l.WorkflowId = w.Id
            WHERE l.InstanceId = @Id
            """,
            instanceId,
            cancellationToken);

    public static Task<string?> TryGetRepositoryNameAsync(
        string? connectionString,
        Guid repositoryId,
        CancellationToken cancellationToken = default) =>
        TryGetNameByGuidAsync(
            connectionString,
            """
            SELECT TOP 1 Name
            FROM repository.Repositories
            WHERE Id = @Id AND IsDeleted = 0
            """,
            repositoryId,
            cancellationToken);

    public static async Task<string?> TryGetFormNameAsync(
        string? connectionString,
        string? formId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(formId))
            return null;

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            const string sql = """
                SELECT TOP 1 name
                FROM dbo.wForm
                WHERE id = @Id
                """;

            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 5 };
            command.Parameters.AddWithValue("@Id", formId.Trim());

            var result = await command.ExecuteScalarAsync(cancellationToken);
            var name = result as string;
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> TryGetNameByGuidAsync(
        string? connectionString,
        string sql,
        Guid id,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString) || id == Guid.Empty)
            return null;

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 5 };
            command.Parameters.AddWithValue("@Id", id);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            var name = result as string;
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadTrimmed(SqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        var value = reader.GetString(ordinal);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
