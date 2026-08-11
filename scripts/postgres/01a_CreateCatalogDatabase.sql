-- =============================================
-- CATALOG DATABASE - CREATE DATABASE + CONNECTOR PROVIDERS (Postgres)
-- Ported from scripts/01a_CreateCatalogDatabase.sql (SQL Server) -- Phase 3.
-- Run against the `postgres` maintenance database with psql (CREATE DATABASE
-- cannot run inside a transaction block, so run this file's first statement
-- with autocommit / outside any BEGIN).
-- Idempotent: safe to re-run.
--
-- NOTE: the app itself creates tenant/catalog databases programmatically via
-- TenantDatabaseCreator.cs (Phase 1) -- this script exists for manual/dev
-- bootstrap, same role its SQL Server predecessor had.
-- =============================================

SELECT 'CREATE DATABASE ezofis_catalog_new'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'ezofis_catalog_new')\gexec

\c ezofis_catalog_new

-- =============================================
-- Ensuring catalog.connector_providers (OAuth apps)
-- =============================================
CREATE SCHEMA IF NOT EXISTS catalog;

-- Table name/case kept EXACTLY as "ConnectorProviders" (not snake_case) to match
-- CatalogDbContext.cs's ToTable("ConnectorProviders", t => t.ExcludeFromMigrations())
-- (Phase 2) -- this script is the only thing that creates/owns this table now that
-- it's excluded from EF Migrations. See Phase 3 forward-flag: do not rename this on
-- the SQL side without updating that ToTable() call in the same change.
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

-- Seed Phase-1 providers (empty ClientId/Secret -- fill after create; do not commit secrets)
-- MERGE ... WHEN NOT MATCHED THEN INSERT -> INSERT ... ON CONFLICT DO NOTHING
INSERT INTO catalog."ConnectorProviders"
    ("Id", "ProviderCode", "DisplayName", "ClientId", "ClientSecret", "AuthUrl", "TokenUrl", "Scopes", "RedirectUri", "IsActive", "CreatedAtUtc")
VALUES
    (gen_random_uuid(), 'GCP', 'Google Cloud Storage',
     '', '',
     'https://accounts.google.com/o/oauth2/v2/auth',
     'https://oauth2.googleapis.com/token',
     'https://www.googleapis.com/auth/devstorage.read_write https://www.googleapis.com/auth/userinfo.email openid',
     '', true, now()),
    (gen_random_uuid(), 'GMAIL', 'Gmail',
     '', '',
     'https://accounts.google.com/o/oauth2/v2/auth',
     'https://oauth2.googleapis.com/token',
     'https://www.googleapis.com/auth/gmail.modify https://www.googleapis.com/auth/userinfo.email openid',
     '', true, now()),
    (gen_random_uuid(), 'ONEDRIVE', 'Microsoft OneDrive',
     '', '',
     'https://login.microsoftonline.com/common/oauth2/v2.0/authorize',
     'https://login.microsoftonline.com/common/oauth2/v2.0/token',
     'offline_access openid profile email Files.ReadWrite.All User.Read',
     '', true, now()),
    (gen_random_uuid(), 'TEAMS', 'Microsoft Teams',
     '', '',
     'https://login.microsoftonline.com/common/oauth2/v2.0/authorize',
     'https://login.microsoftonline.com/common/oauth2/v2.0/token',
     'offline_access openid profile email Files.ReadWrite.All Sites.ReadWrite.All User.Read',
     '', true, now()),
    (gen_random_uuid(), 'DROPBOX', 'Dropbox',
     '', '',
     'https://www.dropbox.com/oauth2/authorize',
     'https://api.dropboxapi.com/oauth2/token',
     '',
     '', true, now()),
    (gen_random_uuid(), 'OUTLOOK', 'Office 365 Outlook',
     '', '',
     'https://login.microsoftonline.com/common/oauth2/v2.0/authorize',
     'https://login.microsoftonline.com/common/oauth2/v2.0/token',
     'offline_access openid profile email Mail.ReadWrite User.Read',
     '', true, now()),
    (gen_random_uuid(), 'QUICKBOOKS', 'QuickBooks',
     '', '',
     'https://appcenter.intuit.com/connect/oauth2',
     'https://oauth.platform.intuit.com/oauth2/v1/tokens/bearer',
     'com.intuit.quickbooks.accounting openid profile email',
     '', true, now())
ON CONFLICT ("ProviderCode") DO NOTHING;

-- '✓ ConnectorProviders seed ensured (GCP, GMAIL, OUTLOOK, ONEDRIVE, TEAMS, DROPBOX, QUICKBOOKS)'
-- Set ClientId / ClientSecret / RedirectUri, e.g.:
-- UPDATE catalog."ConnectorProviders" SET "ClientId"='...', "ClientSecret"='...', "RedirectUri"='https://host/V6API/api/connector/oauth/callback' WHERE "ProviderCode"='GCP';
--
-- Next: run 01b_CreateCatalogTables.sql for remaining script-owned tables (Tenants/
-- UserTenants themselves are now provisioned by `dotnet ef database update` against
-- CatalogDbContext -- Phase 2 -- not by this script).
