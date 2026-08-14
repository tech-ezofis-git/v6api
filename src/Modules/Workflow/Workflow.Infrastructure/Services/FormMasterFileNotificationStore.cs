using System.Globalization;
using System.Text.Json;
using Npgsql;

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
        ("Message", "varchar(1000) NULL"),
        ("Data", "text NULL"),
        ("Severity", "varchar(32) NULL"),
        ("CreatedAtUtc", "timestamptz NULL"),
        ("ModifiedAtUtc", "timestamptz NULL"),
        ("CreatedByGuid", "uuid NULL")
    ];

    public static async Task EnsureTableAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "notification", "dbo", cancellationToken))
        {
            const string sql = """
                CREATE SCHEMA IF NOT EXISTS dbo;
                CREATE TABLE dbo.notification (
                    id integer GENERATED ALWAYS AS IDENTITY NOT NULL PRIMARY KEY,
                    title varchar(500) NULL,
                    status varchar(100) NULL,
                    "inputJson" text NULL,
                    remarks text NULL,
                    category varchar(100) NULL,
                    "createdAt" varchar(50) NULL,
                    "modifiedAt" varchar(50) NULL,
                    "createdBy" integer NOT NULL DEFAULT 0,
                    "modifiedBy" integer NOT NULL DEFAULT 0,
                    "isDeleted" boolean NOT NULL DEFAULT false,
                    "lastActionBy" integer NULL,
                    "readStatus" integer NOT NULL DEFAULT 0,
                    "Message" varchar(1000) NULL,
                    "Data" text NULL,
                    "Severity" varchar(32) NULL,
                    "CreatedAtUtc" timestamptz NULL,
                    "ModifiedAtUtc" timestamptz NULL,
                    "CreatedByGuid" uuid NULL
                );
                CREATE INDEX IX_notification_category_createdAt ON dbo.notification(category, "createdAt" DESC);
                """;

            await using var cmd = new NpgsqlCommand(sql, connection) { CommandTimeout = 120 };
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        await EnsureExtraColumnsAsync(connection, cancellationToken);
    }

    private static async Task EnsureExtraColumnsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        foreach (var (name, definition) in ExtraColumns)
        {
            if (await ColumnExistsAsync(connection, "notification", name, "dbo", cancellationToken))
                continue;

            var sql = $"""ALTER TABLE dbo.notification ADD "{name}" {definition};""";
            await using var cmd = new NpgsqlCommand(sql, connection) { CommandTimeout = 60 };
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public static Task<int> InsertAsync(
        NpgsqlConnection connection,
        string title,
        string? remarks,
        object inputJson,
        string category,
        int createdByLegacyId,
        CancellationToken cancellationToken)
        => InsertAsync(connection, title, status: null, remarks, inputJson, category, createdByLegacyId, cancellationToken);

    public static async Task<int> InsertAsync(
        NpgsqlConnection connection,
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
                title, status, remarks, "inputJson", category,
                "createdAt", "modifiedAt", "createdBy", "modifiedBy", "isDeleted", "lastActionBy", "readStatus")
            VALUES (
                @Title, @Status, @Remarks, @InputJson, @Category,
                @CreatedAt, NULL, @CreatedBy, 0, false, NULL, 0)
            RETURNING id;
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
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
        NpgsqlConnection connection,
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
                title, status, remarks, "inputJson", category,
                "createdAt", "modifiedAt", "createdBy", "modifiedBy", "isDeleted", "lastActionBy", "readStatus",
                "Message", "Data", "Severity", "CreatedAtUtc", "ModifiedAtUtc", "CreatedByGuid")
            VALUES (
                @Title, @Status, @Message, @Data, @Category,
                @CreatedAt, NULL, @CreatedBy, 0, false, NULL, 0,
                @Message, @Data, @Severity, @CreatedAtUtc, NULL, @CreatedByGuid)
            RETURNING id;
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
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
        NpgsqlConnection connection,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (await TableExistsAsync(connection, "user", "dbo", cancellationToken))
        {
            if (await ColumnExistsAsync(connection, "user", "userGuid", "dbo", cancellationToken))
            {
                const string byGuid = """
                    SELECT id FROM dbo."user"
                    WHERE "isDeleted" = false AND "userGuid" = @UserGuid
                    LIMIT 1;
                    """;
                await using var cmd = new NpgsqlCommand(byGuid, connection);
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
                SELECT "Id" FROM users."Users" WHERE "Id" = @UserId AND "IsDeleted" = false LIMIT 1;
                """;
            await using var cmd = new NpgsqlCommand(usersSql, connection);
            cmd.Parameters.AddWithValue("@UserId", userId);
            var scalar = await cmd.ExecuteScalarAsync(cancellationToken);
            if (scalar is Guid)
                return 0;
        }

        return 0;
    }

    private static async Task<bool> TableExistsAsync(
        NpgsqlConnection connection,
        string tableName,
        string schema,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT 1 FROM information_schema.tables
            WHERE table_name = @TableName AND table_schema = @Schema;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TableName", tableName);
        cmd.Parameters.AddWithValue("@Schema", schema);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result != null && result != DBNull.Value;
    }

    private static async Task<bool> ColumnExistsAsync(
        NpgsqlConnection connection,
        string tableName,
        string columnName,
        string schema,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT 1 FROM information_schema.columns
            WHERE table_name = @TableName AND table_schema = @Schema AND column_name = @ColumnName;
            """;
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TableName", tableName);
        cmd.Parameters.AddWithValue("@Schema", schema);
        cmd.Parameters.AddWithValue("@ColumnName", columnName);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result != null && result != DBNull.Value;
    }
}
