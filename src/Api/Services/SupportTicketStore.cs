using Npgsql;
using SaaSApp.MultiTenancy;

namespace SaaSApp.Api.Services;

public sealed class SupportTicketInsertRequest
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid? UserId { get; init; }
    public string? CallerEmail { get; init; }
    public string? SupportCategory { get; init; }
    public string? Priorty { get; init; }
    public string? PreferredContact { get; init; }
    public string? PhoneNO { get; init; }
    public string? RequestDescription { get; init; }
    public bool IsEmailSend { get; init; }
    public string? JiraIssueId { get; init; }
    public string? JiraIssueKey { get; init; }
    public string? JiraIssueUrl { get; init; }
    public string? JiraRawResponse { get; init; }
    public bool JiraSuccess { get; init; }
    public DateTime CreatedAtUtc { get; init; }
}

/// <summary>
/// Persists support tickets in the tenant DB. Creates support."SupportTickets" on first use.
/// </summary>
public sealed class SupportTicketStore
{
    private readonly ITenantConnectionProvider _connectionProvider;

    public SupportTicketStore(ITenantConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider;
    }

    public async Task InsertAsync(SupportTicketInsertRequest entry, CancellationToken cancellationToken)
    {
        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string is not available.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await EnsureTableAsync(connection, cancellationToken);

        const string sql = """
            INSERT INTO support."SupportTickets" (
                "Id", "TenantId", "UserId", "CallerEmail",
                "SupportCategory", "Priorty", "PreferredContact", "PhoneNO", "RequestDescription", "IsEmailSend",
                "JiraIssueId", "JiraIssueKey", "JiraIssueUrl", "JiraRawResponse", "JiraSuccess",
                "CreatedAtUtc")
            VALUES (
                @Id, @TenantId, @UserId, @CallerEmail,
                @SupportCategory, @Priorty, @PreferredContact, @PhoneNO, @RequestDescription, @IsEmailSend,
                @JiraIssueId, @JiraIssueKey, @JiraIssueUrl, @JiraRawResponse, @JiraSuccess,
                @CreatedAtUtc)
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Id", entry.Id);
        command.Parameters.AddWithValue("@TenantId", entry.TenantId);
        command.Parameters.AddWithValue("@UserId", (object?)entry.UserId ?? DBNull.Value);
        command.Parameters.AddWithValue("@CallerEmail", (object?)entry.CallerEmail ?? DBNull.Value);
        command.Parameters.AddWithValue("@SupportCategory", (object?)entry.SupportCategory ?? DBNull.Value);
        command.Parameters.AddWithValue("@Priorty", (object?)entry.Priorty ?? DBNull.Value);
        command.Parameters.AddWithValue("@PreferredContact", (object?)entry.PreferredContact ?? DBNull.Value);
        command.Parameters.AddWithValue("@PhoneNO", (object?)entry.PhoneNO ?? DBNull.Value);
        command.Parameters.AddWithValue("@RequestDescription", (object?)Truncate(entry.RequestDescription, 1000) ?? DBNull.Value);
        command.Parameters.AddWithValue("@IsEmailSend", entry.IsEmailSend);
        command.Parameters.AddWithValue("@JiraIssueId", (object?)entry.JiraIssueId ?? DBNull.Value);
        command.Parameters.AddWithValue("@JiraIssueKey", (object?)entry.JiraIssueKey ?? DBNull.Value);
        command.Parameters.AddWithValue("@JiraIssueUrl", (object?)entry.JiraIssueUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("@JiraRawResponse", (object?)entry.JiraRawResponse ?? DBNull.Value);
        command.Parameters.AddWithValue("@JiraSuccess", entry.JiraSuccess);
        command.Parameters.AddWithValue("@CreatedAtUtc", entry.CreatedAtUtc);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// PHASE 4: support."SupportTickets" is always created fresh on Postgres tenant DBs with
    /// "SupportCategory" already the column name (see CREATE TABLE below) -- the SQL Server
    /// version's "rename legacy NeedHelp column" branch has nothing to detect here (no Postgres
    /// tenant DB ever had a NeedHelp column), so it was dropped rather than translated. Same
    /// precedent as FormService.cs / other legacy-drift-detection removals in this migration.
    /// </summary>
    private static async Task EnsureTableAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE SCHEMA IF NOT EXISTS support;

            CREATE TABLE IF NOT EXISTS support."SupportTickets" (
                "Id"                  uuid NOT NULL CONSTRAINT "PK_SupportTickets" PRIMARY KEY,
                "TenantId"            uuid NOT NULL,
                "UserId"              uuid NULL,
                "CallerEmail"         varchar(256) NULL,
                "SupportCategory"     varchar(256) NULL,
                "Priorty"             varchar(64) NULL,
                "PreferredContact"    varchar(64) NULL,
                "PhoneNO"             varchar(64) NULL,
                "RequestDescription"  varchar(1000) NULL,
                "IsEmailSend"         boolean NOT NULL DEFAULT false,
                "JiraIssueId"         varchar(64) NULL,
                "JiraIssueKey"        varchar(64) NULL,
                "JiraIssueUrl"        varchar(512) NULL,
                "JiraRawResponse"     text NULL,
                "JiraSuccess"         boolean NOT NULL DEFAULT false,
                "CreatedAtUtc"        timestamptz NOT NULL
            );

            CREATE INDEX IF NOT EXISTS "IX_SupportTickets_TenantId_CreatedAtUtc"
                ON support."SupportTickets" ("TenantId", "CreatedAtUtc" DESC);
            """;

        try
        {
            await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 60 };
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState is
            PostgresErrorCodes.DuplicateTable or
            PostgresErrorCodes.DuplicateObject or
            PostgresErrorCodes.DuplicateSchema or
            PostgresErrorCodes.UniqueViolation)
        {
            // Race: another request created the table/index — safe to continue.
        }
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;
        return value[..maxLength];
    }
}
