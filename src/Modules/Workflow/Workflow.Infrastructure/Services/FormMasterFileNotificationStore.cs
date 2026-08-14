using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>v5 dbo.notification table — required by Python master file import.</summary>
internal static class FormMasterFileNotificationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly (string Name, string Definition)[] ExtraColumns =
    [
        ("Message", "NVARCHAR(1000) NULL"),
        ("Data", "NVARCHAR(MAX) NULL"),
        ("Severity", "NVARCHAR(32) NULL"),
        ("CreatedAtUtc", "datetime2 NULL"),
        ("ModifiedAtUtc", "datetime2 NULL"),
        ("CreatedByGuid", "uniqueidentifier NULL")
    ];

    public static async Task EnsureTableAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "notification", "dbo", cancellationToken))
        {
            const string sql = """
                CREATE TABLE dbo.notification (
                    id INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    title NVARCHAR(500) NULL,
                    status NVARCHAR(100) NULL,
                    remarks NVARCHAR(MAX) NULL,
                    inputJson NVARCHAR(MAX) NULL,
                    category NVARCHAR(100) NULL,
                    createdAt NVARCHAR(50) NULL,
                    modifiedAt NVARCHAR(50) NULL,
                    createdBy INT NOT NULL DEFAULT(0),
                    modifiedBy INT NOT NULL DEFAULT(0),
                    isDeleted BIT NOT NULL DEFAULT(0),
                    lastActionBy INT NULL,
                    readStatus INT NOT NULL DEFAULT(0),
                    Message NVARCHAR(1000) NULL,
                    Data NVARCHAR(MAX) NULL,
                    Severity NVARCHAR(32) NULL,
                    CreatedAtUtc datetime2 NULL,
                    ModifiedAtUtc datetime2 NULL,
                    CreatedByGuid uniqueidentifier NULL
                );
                CREATE INDEX IX_notification_category_createdAt ON dbo.notification(category, createdAt DESC);
                """;

            await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 120 };
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        await EnsureExtraColumnsAsync(connection, cancellationToken);
    }

    private static async Task EnsureExtraColumnsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        foreach (var (name, definition) in ExtraColumns)
        {
            if (await ColumnExistsAsync(connection, "notification", name, "dbo", cancellationToken))
                continue;

            var sql = $"ALTER TABLE dbo.notification ADD [{name}] {definition};";
            await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 60 };
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public static Task<int> InsertAsync(
        SqlConnection connection,
        string title,
        string? remarks,
        object inputJson,
        string category,
        int createdByLegacyId,
        CancellationToken cancellationToken)
        => InsertAsync(connection, title, status: null, remarks, inputJson, category, createdByLegacyId, cancellationToken);

    public static async Task<int> InsertAsync(
        SqlConnection connection,
        string title,
        string? status,
        string? remarks,
        object inputJson,
        string category,
        int createdByLegacyId,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var inputJsonText = JsonSerializer.Serialize(inputJson, JsonOptions);

        const string sql = """
            INSERT INTO dbo.notification (
                title, status, remarks, inputJson, category,
                createdAt, modifiedAt, createdBy, modifiedBy, isDeleted, lastActionBy, readStatus)
            OUTPUT INSERTED.id
            VALUES (
                @Title, @Status, @Remarks, @InputJson, @Category,
                @CreatedAt, NULL, @CreatedBy, 0, 0, NULL, 0);
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Title", (object?)title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Status", (object?)status ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Remarks", (object?)remarks ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@InputJson", inputJsonText);
        cmd.Parameters.AddWithValue("@Category", category);
        cmd.Parameters.AddWithValue("@CreatedAt", now);
        cmd.Parameters.AddWithValue("@CreatedBy", createdByLegacyId);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public static async Task<int> InsertMoveNotificationAsync(
        SqlConnection connection,
        string title,
        string? status,
        string? message,
        object data,
        string severity,
        DateTime? createdAtUtc,
        Guid? createdByGuid,
        int createdByLegacyId,
        string category,
        CancellationToken cancellationToken)
    {
        var atUtc = createdAtUtc ?? DateTime.UtcNow;
        if (atUtc.Kind == DateTimeKind.Unspecified)
            atUtc = DateTime.SpecifyKind(atUtc, DateTimeKind.Utc);

        var nowStamp = atUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var dataJson = JsonSerializer.Serialize(data, JsonOptions);

        const string sql = """
            INSERT INTO dbo.notification (
                title, status, remarks, inputJson, category,
                createdAt, modifiedAt, createdBy, modifiedBy, isDeleted, lastActionBy, readStatus,
                Message, Data, Severity, CreatedAtUtc, ModifiedAtUtc, CreatedByGuid)
            OUTPUT INSERTED.id
            VALUES (
                @Title, @Status, @Message, @Data, @Category,
                @CreatedAt, NULL, @CreatedBy, 0, 0, NULL, 0,
                @Message, @Data, @Severity, @CreatedAtUtc, NULL, @CreatedByGuid);
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@Title", (object?)title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Status", (object?)status ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Message", (object?)message ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Data", dataJson);
        cmd.Parameters.AddWithValue("@Category", category);
        cmd.Parameters.AddWithValue("@CreatedAt", nowStamp);
        cmd.Parameters.AddWithValue("@CreatedBy", createdByLegacyId);
        cmd.Parameters.AddWithValue("@Severity", severity);
        cmd.Parameters.AddWithValue("@CreatedAtUtc", atUtc);
        cmd.Parameters.AddWithValue("@CreatedByGuid", createdByGuid.HasValue && createdByGuid.Value != Guid.Empty
            ? createdByGuid.Value
            : DBNull.Value);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public static async Task<int> TryResolveLegacyUserIdAsync(
        SqlConnection connection,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (await TableExistsAsync(connection, "user", "dbo", cancellationToken))
        {
            if (await ColumnExistsAsync(connection, "user", "userGuid", "dbo", cancellationToken))
            {
                const string byGuid = """
                    SELECT TOP 1 id FROM dbo.[user]
                    WHERE isDeleted = 0 AND userGuid = @UserGuid;
                    """;
                await using var cmd = new SqlCommand(byGuid, connection);
                cmd.Parameters.AddWithValue("@UserGuid", userId.ToString("D"));
                var scalar = await cmd.ExecuteScalarAsync(cancellationToken);
                if (scalar is int i) return i;
                if (scalar is not null && scalar != DBNull.Value && int.TryParse(scalar.ToString(), out var parsed))
                    return parsed;
            }
        }

        if (await TableExistsAsync(connection, "Users", "users", cancellationToken))
        {
            const string usersSql = """
                SELECT TOP 1 Id FROM users.Users WHERE Id = @UserId AND IsDeleted = 0;
                """;
            await using var cmd = new SqlCommand(usersSql, connection);
            cmd.Parameters.AddWithValue("@UserId", userId);
            var scalar = await cmd.ExecuteScalarAsync(cancellationToken);
            if (scalar is Guid)
                return 0;
        }

        return 0;
    }

    private static async Task<bool> TableExistsAsync(
        SqlConnection connection,
        string tableName,
        string schema,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT 1 FROM sys.tables t
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE t.name = @TableName AND s.name = @Schema;
            """;
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TableName", tableName);
        cmd.Parameters.AddWithValue("@Schema", schema);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result != null && result != DBNull.Value;
    }

    private static async Task<bool> ColumnExistsAsync(
        SqlConnection connection,
        string tableName,
        string columnName,
        string schema,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT 1 FROM sys.columns c
            INNER JOIN sys.tables t ON c.object_id = t.object_id
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE t.name = @TableName AND s.name = @Schema AND c.name = @ColumnName;
            """;
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TableName", tableName);
        cmd.Parameters.AddWithValue("@Schema", schema);
        cmd.Parameters.AddWithValue("@ColumnName", columnName);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result != null && result != DBNull.Value;
    }
}
