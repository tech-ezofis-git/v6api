using System.Collections.Concurrent;
using Npgsql;
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

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // EXACT-MATCH REQUIREMENT: table/column names must stay catalog."RepositoryItemShares"
        // (PascalCase quoted) to match CatalogDbContext.cs's ToTable("RepositoryItemShares", t
        // => t.ExcludeFromMigrations()) (Phase 2) -- that table is script-owned, not EF-owned,
        // so this is the only thing (along with scripts/postgres/Create_RepositoryItemShares.sql,
        // kept in sync with the same 3 extra columns below) that creates/evolves it on Postgres.
        const string ensureSchemaSql = "CREATE SCHEMA IF NOT EXISTS catalog;";

        const string createTableSql = """
            CREATE TABLE IF NOT EXISTS catalog."RepositoryItemShares" (
                "Id"                 uuid NOT NULL DEFAULT gen_random_uuid() CONSTRAINT "PK_RepositoryItemShares" PRIMARY KEY,
                "ShareToken"         varchar(128)  NOT NULL,
                "SourceTenantId"     uuid          NOT NULL,
                "SourceRepositoryId" uuid          NOT NULL,
                "SourceItemId"       uuid          NOT NULL,
                "SharedByUserId"     uuid          NOT NULL,
                "RecipientEmail"     varchar(256)  NOT NULL,
                "Message"            varchar(2000) NULL,
                "Status"             varchar(32)   NOT NULL CONSTRAINT "DF_RepositoryItemShares_Status" DEFAULT 'Active',
                "ExpiresAtUtc"       timestamptz   NOT NULL,
                "CreatedAtUtc"       timestamptz   NOT NULL CONSTRAINT "DF_RepositoryItemShares_CreatedAt" DEFAULT now(),
                "LastAccessedAtUtc"  timestamptz   NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS "IX_RepositoryItemShares_ShareToken"
                ON catalog."RepositoryItemShares" ("ShareToken");

            CREATE INDEX IF NOT EXISTS "IX_RepositoryItemShares_Recipient_Status"
                ON catalog."RepositoryItemShares" ("RecipientEmail", "Status");

            CREATE INDEX IF NOT EXISTS "IX_RepositoryItemShares_Source"
                ON catalog."RepositoryItemShares" ("SourceTenantId", "SourceRepositoryId", "SourceItemId");

            ALTER TABLE catalog."RepositoryItemShares"
                ADD COLUMN IF NOT EXISTS "AutoProvisionGuest" boolean NOT NULL DEFAULT false;

            ALTER TABLE catalog."RepositoryItemShares"
                ADD COLUMN IF NOT EXISTS "WorkflowInstanceId" uuid NULL;

            ALTER TABLE catalog."RepositoryItemShares"
                ADD COLUMN IF NOT EXISTS "Action" integer NOT NULL DEFAULT 0;
            """;

        foreach (var sql in new[] { ensureSchemaSql, createTableSql })
        {
            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        TableEnsured.TryAdd(connectionString, 0);
    }
}
