using Microsoft.EntityFrameworkCore;
using Npgsql;
using SaaSApp.Catalog.Entities;
using SaaSApp.Catalog.Persistence;

namespace SaaSApp.Catalog;

public sealed class ConnectorProviderCatalog : IConnectorProviderCatalog
{
    private readonly IDbContextFactory<CatalogDbContext> _catalogFactory;

    public ConnectorProviderCatalog(IDbContextFactory<CatalogDbContext> catalogFactory)
    {
        _catalogFactory = catalogFactory;
    }

    /// <summary>
    /// PHASE 4: catalog."ConnectorProviders" is created AND seeded by
    /// scripts/postgres/01a_CreateCatalogDatabase.sql (Phase 1) with the same 7 OAuth providers
    /// this method seeds — that script's INSERT ... ON CONFLICT ("ProviderCode") DO NOTHING is the
    /// primary seed path. This method's CREATE TABLE IF NOT EXISTS / INSERT ... ON CONFLICT DO
    /// UPDATE is kept as a defensive idempotent no-op safety net (mirrors the SQL Server MERGE
    /// semantics: only GMAIL/OUTLOOK scopes get refreshed on conflict) for a catalog DB where the
    /// Phase 1 script was skipped, plus the ongoing legacy QUICKBOOKS_EMAIL → QUICKBOOKS migration.
    /// </summary>
    public async Task EnsureSchemaAndSeedAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _catalogFactory.CreateDbContextAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE SCHEMA IF NOT EXISTS catalog;
                CREATE TABLE IF NOT EXISTS catalog."ConnectorProviders" (
                    "Id" uuid NOT NULL DEFAULT gen_random_uuid() CONSTRAINT "PK_ConnectorProviders" PRIMARY KEY,
                    "ProviderCode" varchar(64) NOT NULL,
                    "DisplayName" varchar(128) NOT NULL,
                    "ClientId" varchar(512) NOT NULL DEFAULT '',
                    "ClientSecret" varchar(1024) NOT NULL DEFAULT '',
                    "AuthUrl" varchar(1024) NOT NULL,
                    "TokenUrl" varchar(1024) NOT NULL,
                    "Scopes" varchar(2000) NOT NULL DEFAULT '',
                    "RedirectUri" varchar(1024) NOT NULL DEFAULT '',
                    "ExtraConfigJson" text NULL,
                    "IsActive" boolean NOT NULL DEFAULT true,
                    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
                    "ModifiedAtUtc" timestamptz NULL,
                    CONSTRAINT "UQ_ConnectorProviders_ProviderCode" UNIQUE ("ProviderCode")
                );

                INSERT INTO catalog."ConnectorProviders"
                    ("Id", "ProviderCode", "DisplayName", "AuthUrl", "TokenUrl", "Scopes", "ClientId", "ClientSecret", "RedirectUri", "IsActive", "CreatedAtUtc")
                VALUES
                    (gen_random_uuid(), 'GCP', 'Google Cloud Storage',
                     'https://accounts.google.com/o/oauth2/v2/auth',
                     'https://oauth2.googleapis.com/token',
                     'https://www.googleapis.com/auth/devstorage.read_write https://www.googleapis.com/auth/userinfo.email openid',
                     '', '', '', true, now()),
                    (gen_random_uuid(), 'GMAIL', 'Gmail',
                     'https://accounts.google.com/o/oauth2/v2/auth',
                     'https://oauth2.googleapis.com/token',
                     'https://www.googleapis.com/auth/gmail.modify https://www.googleapis.com/auth/userinfo.email openid',
                     '', '', '', true, now()),
                    (gen_random_uuid(), 'OUTLOOK', 'Office 365 Outlook',
                     'https://login.microsoftonline.com/common/oauth2/v2.0/authorize',
                     'https://login.microsoftonline.com/common/oauth2/v2.0/token',
                     'offline_access openid profile email Mail.ReadWrite User.Read',
                     '', '', '', true, now()),
                    (gen_random_uuid(), 'ONEDRIVE', 'Microsoft OneDrive',
                     'https://login.microsoftonline.com/common/oauth2/v2.0/authorize',
                     'https://login.microsoftonline.com/common/oauth2/v2.0/token',
                     'offline_access openid profile email Files.ReadWrite.All User.Read',
                     '', '', '', true, now()),
                    (gen_random_uuid(), 'TEAMS', 'Microsoft Teams',
                     'https://login.microsoftonline.com/common/oauth2/v2.0/authorize',
                     'https://login.microsoftonline.com/common/oauth2/v2.0/token',
                     'offline_access openid profile email Files.ReadWrite.All Sites.ReadWrite.All User.Read',
                     '', '', '', true, now()),
                    (gen_random_uuid(), 'DROPBOX', 'Dropbox',
                     'https://www.dropbox.com/oauth2/authorize',
                     'https://api.dropboxapi.com/oauth2/token',
                     '',
                     '', '', '', true, now()),
                    (gen_random_uuid(), 'QUICKBOOKS', 'QuickBooks',
                     'https://appcenter.intuit.com/connect/oauth2',
                     'https://oauth.platform.intuit.com/oauth2/v1/tokens/bearer',
                     'com.intuit.quickbooks.accounting openid profile email',
                     '', '', '', true, now())
                ON CONFLICT ("ProviderCode") DO UPDATE SET
                    "Scopes" = CASE WHEN EXCLUDED."ProviderCode" IN ('GMAIL', 'OUTLOOK')
                                    THEN EXCLUDED."Scopes"
                                    ELSE catalog."ConnectorProviders"."Scopes" END,
                    "ModifiedAtUtc" = CASE WHEN EXCLUDED."ProviderCode" IN ('GMAIL', 'OUTLOOK')
                                           THEN now()
                                           ELSE catalog."ConnectorProviders"."ModifiedAtUtc" END;
                """;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Migrate legacy QUICKBOOKS_EMAIL → QUICKBOOKS (or deactivate if both exist)
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                DO $do$
                BEGIN
                    IF EXISTS (SELECT 1 FROM catalog."ConnectorProviders" WHERE "ProviderCode" = 'QUICKBOOKS_EMAIL')
                       AND NOT EXISTS (SELECT 1 FROM catalog."ConnectorProviders" WHERE "ProviderCode" = 'QUICKBOOKS')
                    THEN
                        UPDATE catalog."ConnectorProviders"
                        SET "ProviderCode" = 'QUICKBOOKS',
                            "DisplayName" = 'QuickBooks',
                            "Scopes" = 'com.intuit.quickbooks.accounting openid profile email',
                            "ModifiedAtUtc" = now()
                        WHERE "ProviderCode" = 'QUICKBOOKS_EMAIL';
                    ELSIF EXISTS (SELECT 1 FROM catalog."ConnectorProviders" WHERE "ProviderCode" = 'QUICKBOOKS_EMAIL')
                    THEN
                        UPDATE catalog."ConnectorProviders"
                        SET "IsActive" = false, "ModifiedAtUtc" = now()
                        WHERE "ProviderCode" = 'QUICKBOOKS_EMAIL';
                    END IF;
                END
                $do$;
                """;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<ConnectorProvider>> ListActiveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureSchemaAndSeedAsync(cancellationToken);
            await using var context = await _catalogFactory.CreateDbContextAsync(cancellationToken);
            return await context.ConnectorProviders
                .AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.DisplayName)
                .ToListAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return Array.Empty<ConnectorProvider>();
        }
    }

    public async Task<ConnectorProvider?> GetByCodeAsync(string providerCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerCode))
            return null;

        try
        {
            await EnsureSchemaAndSeedAsync(cancellationToken);
            await using var context = await _catalogFactory.CreateDbContextAsync(cancellationToken);
            var code = providerCode.Trim().ToUpperInvariant();
            return await context.ConnectorProviders
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.ProviderCode == code && p.IsActive, cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return null;
        }
    }

    public async Task UpsertCredentialsAsync(
        string providerCode,
        string clientId,
        string clientSecret,
        string redirectUri,
        string? scopes = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAndSeedAsync(cancellationToken);
        await using var context = await _catalogFactory.CreateDbContextAsync(cancellationToken);
        var code = providerCode.Trim().ToUpperInvariant();
        var row = await context.ConnectorProviders.FirstOrDefaultAsync(p => p.ProviderCode == code, cancellationToken);
        if (row == null)
            throw new InvalidOperationException($"Unknown provider code '{providerCode}'.");

        row.ClientId = clientId?.Trim() ?? string.Empty;
        row.ClientSecret = clientSecret?.Trim() ?? string.Empty;
        row.RedirectUri = redirectUri?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(scopes))
            row.Scopes = scopes.Trim();
        row.ModifiedAtUtc = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
    }
}
