-- =============================================
-- CATALOG DATABASE - CREATE TABLES (Postgres)
-- Ported from scripts/01b_CreateCatalogTables.sql (SQL Server) -- Phase 3.
-- Run against ezofis_catalog_new with psql.
-- =============================================

-- =============================================
-- catalog.Tenants / catalog.UserTenants / dbo.__EFMigrationsHistory
-- =============================================
-- INTENTIONALLY NOT PORTED. On SQL Server this section created Tenants/UserTenants
-- directly and then inserted a fake dbo.__EFMigrationsHistory row for the old
-- '20260226000000_InitialCatalog' migration ID so EF's Database.Migrate() would skip
-- re-creating them. That trick does not carry over to Postgres: Phase 2 regenerated
-- CatalogDbContext's migration history as ONE consolidated 'InitialPostgres' migration
-- (not 21 incremental ones), so there is no per-table migration ID left to fake/skip.
-- On Postgres, `dotnet ef database update` against CatalogDbContext (Phase 2, already
-- verified working) is the ONLY thing that creates catalog.Tenants, catalog.UserTenants,
-- dbo.mailsettings, and dbo."OTPVerification" -- run that instead of this script for
-- those four tables.
--
-- Also dropped: the old script's defensive `ALTER TABLE catalog.UserTenants ADD
-- IsSuperuser` backfill. Confirmed via full-repo grep that no C# code (entity class,
-- Fluent API config, or raw ADO.NET) references IsSuperuser anywhere -- it is a dead
-- column that was never wired to any actual feature, so it was not carried into the
-- Postgres schema. Flag for a human: if IsSuperuser is meant to come back, it needs to
-- be added to catalog/Entities/UserTenant.cs and CatalogDbContext.cs first, then picked
-- up by a real EF migration -- not re-introduced as an orphaned SQL-only column.

-- =============================================
-- catalog.ConnectorProviders (global OAuth apps -- also created by 01a)
-- =============================================
-- Redundant-but-harmless idempotent guard, matching the original SQL Server script's
-- own redundancy with 01a (both scripts create/seed this table defensively). Table
-- name/case kept EXACTLY as "ConnectorProviders" -- see 01a's header comment for why.
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
    ("Id", "ProviderCode", "DisplayName", "ClientId", "ClientSecret", "AuthUrl", "TokenUrl", "Scopes", "RedirectUri", "IsActive", "CreatedAtUtc")
VALUES
    (gen_random_uuid(), 'GCP', 'Google Cloud Storage', '', '',
     'https://accounts.google.com/o/oauth2/v2/auth', 'https://oauth2.googleapis.com/token',
     'https://www.googleapis.com/auth/devstorage.read_write https://www.googleapis.com/auth/userinfo.email openid', '', true, now()),
    (gen_random_uuid(), 'GMAIL', 'Gmail', '', '',
     'https://accounts.google.com/o/oauth2/v2/auth', 'https://oauth2.googleapis.com/token',
     'https://www.googleapis.com/auth/gmail.modify https://www.googleapis.com/auth/userinfo.email openid', '', true, now()),
    (gen_random_uuid(), 'ONEDRIVE', 'Microsoft OneDrive', '', '',
     'https://login.microsoftonline.com/common/oauth2/v2.0/authorize', 'https://login.microsoftonline.com/common/oauth2/v2.0/token',
     'offline_access openid profile email Files.ReadWrite.All User.Read', '', true, now()),
    (gen_random_uuid(), 'TEAMS', 'Microsoft Teams', '', '',
     'https://login.microsoftonline.com/common/oauth2/v2.0/authorize', 'https://login.microsoftonline.com/common/oauth2/v2.0/token',
     'offline_access openid profile email Files.ReadWrite.All Sites.ReadWrite.All User.Read', '', true, now()),
    (gen_random_uuid(), 'DROPBOX', 'Dropbox', '', '',
     'https://www.dropbox.com/oauth2/authorize', 'https://api.dropboxapi.com/oauth2/token', '', '', true, now()),
    (gen_random_uuid(), 'OUTLOOK', 'Office 365 Outlook', '', '',
     'https://login.microsoftonline.com/common/oauth2/v2.0/authorize', 'https://login.microsoftonline.com/common/oauth2/v2.0/token',
     'offline_access openid profile email Mail.ReadWrite User.Read', '', true, now()),
    (gen_random_uuid(), 'QUICKBOOKS', 'QuickBooks', '', '',
     'https://appcenter.intuit.com/connect/oauth2', 'https://oauth.platform.intuit.com/oauth2/v1/tokens/bearer',
     'com.intuit.quickbooks.accounting openid profile email', '', true, now())
ON CONFLICT ("ProviderCode") DO NOTHING;

-- =============================================
-- Verification
-- =============================================
SELECT table_name AS "TableName"
FROM information_schema.tables
WHERE table_schema IN ('catalog', 'dbo') AND table_type = 'BASE TABLE'
ORDER BY table_name;
