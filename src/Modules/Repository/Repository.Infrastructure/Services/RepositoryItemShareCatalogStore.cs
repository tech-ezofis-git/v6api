using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SaaSApp.Catalog.Persistence;

namespace SaaSApp.Repository.Infrastructure.Services;

internal static class RepositoryItemShareCatalogStore
{
    private static readonly ConcurrentDictionary<string, byte> TableEnsured = new(StringComparer.OrdinalIgnoreCase);

    public static async Task EnsureTableAsync(
        IDbContextFactory<CatalogDbContext> catalogFactory,
        CancellationToken cancellationToken)
    {
        await using var catalog = await catalogFactory.CreateDbContextAsync(cancellationToken);
        var connectionString = catalog.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Catalog connection string is not configured.");

        if (TableEnsured.ContainsKey(connectionString))
            return;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string ensureSchemaSql = """
            IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'catalog')
                EXEC(N'CREATE SCHEMA catalog');
            """;

        const string createTableSql = """
            IF NOT EXISTS (
                SELECT 1 FROM sys.tables t
                INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE s.name = N'catalog' AND t.name = N'RepositoryItemShares')
            BEGIN
                CREATE TABLE catalog.RepositoryItemShares (
                    Id                  UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_RepositoryItemShares PRIMARY KEY DEFAULT NEWID(),
                    ShareToken          NVARCHAR(128)    NOT NULL,
                    SourceTenantId      UNIQUEIDENTIFIER NOT NULL,
                    SourceRepositoryId  UNIQUEIDENTIFIER NOT NULL,
                    SourceItemId        UNIQUEIDENTIFIER NOT NULL,
                    SharedByUserId      UNIQUEIDENTIFIER NOT NULL,
                    RecipientEmail      NVARCHAR(256)    NOT NULL,
                    Message             NVARCHAR(2000)   NULL,
                    Status              NVARCHAR(32)     NOT NULL CONSTRAINT DF_RepositoryItemShares_Status DEFAULT N'Active',
                    ExpiresAtUtc        DATETIME2        NOT NULL,
                    CreatedAtUtc        DATETIME2        NOT NULL CONSTRAINT DF_RepositoryItemShares_CreatedAt DEFAULT SYSUTCDATETIME(),
                    LastAccessedAtUtc   DATETIME2        NULL
                );

                CREATE UNIQUE INDEX IX_RepositoryItemShares_ShareToken
                    ON catalog.RepositoryItemShares (ShareToken);

                CREATE INDEX IX_RepositoryItemShares_Recipient_Status
                    ON catalog.RepositoryItemShares (RecipientEmail, Status);

                CREATE INDEX IX_RepositoryItemShares_Source
                    ON catalog.RepositoryItemShares (SourceTenantId, SourceRepositoryId, SourceItemId);
            END

            IF COL_LENGTH('catalog.RepositoryItemShares', 'AutoProvisionGuest') IS NULL
                ALTER TABLE catalog.RepositoryItemShares ADD AutoProvisionGuest BIT NOT NULL
                    CONSTRAINT DF_RepositoryItemShares_AutoProvisionGuest DEFAULT 0;

            IF COL_LENGTH('catalog.RepositoryItemShares', 'WorkflowInstanceId') IS NULL
                ALTER TABLE catalog.RepositoryItemShares ADD WorkflowInstanceId UNIQUEIDENTIFIER NULL;

            IF COL_LENGTH('catalog.RepositoryItemShares', 'Action') IS NULL
                ALTER TABLE catalog.RepositoryItemShares ADD [Action] INT NOT NULL
                    CONSTRAINT DF_RepositoryItemShares_Action DEFAULT (0);
            """;

        foreach (var sql in new[] { ensureSchemaSql, createTableSql })
        {
            await using var cmd = new SqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        TableEnsured.TryAdd(connectionString, 0);
    }
}
