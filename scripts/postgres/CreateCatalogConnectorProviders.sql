-- =============================================
-- Catalog: ConnectorProviders (global OAuth app config) -- Postgres
-- Ported from scripts/CreateCatalogConnectorProviders.sql -- Phase 3.
-- Run against catalog database (DefaultConnection).
-- Secrets: UPDATE "ClientId"/"ClientSecret" after create -- do not commit real secrets.
--
-- NOT superseded by 01a/01b_*.sql's simpler ConnectorProviders seed -- confirmed via
-- git history that this file was added in the SAME commit as 01a's ConnectorProviders
-- work (b701d93), and it has two things 01a/01b's seed doesn't: a scope-refresh clause
-- for GMAIL/OUTLOOK on re-run (INSERT ... ON CONFLICT DO UPDATE, not just DO NOTHING),
-- and a one-time QUICKBOOKS_EMAIL -> QUICKBOOKS provider-code migration. Ported in
-- full rather than skipped.
-- =============================================

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

-- Seed providers (empty ClientId/Secret -- fill via UPDATE; do not commit secrets).
-- MERGE ... WHEN MATCHED AND ProviderCode IN (...) THEN UPDATE Scopes ... WHEN NOT
-- MATCHED THEN INSERT -> a single INSERT ... ON CONFLICT DO UPDATE can't conditionally
-- update only some rows, so this uses a CASE inside the DO UPDATE SET instead: for
-- GMAIL/OUTLOOK the Scopes/ModifiedAtUtc refresh; for everyone else it's a no-op
-- (re-assigns the existing value), which is the same practical effect as "only some
-- rows get updated on conflict."
INSERT INTO catalog."ConnectorProviders"
    ("Id", "ProviderCode", "DisplayName", "ClientId", "ClientSecret", "AuthUrl", "TokenUrl", "Scopes", "RedirectUri", "IsActive", "CreatedAtUtc")
VALUES
    (gen_random_uuid(), 'GCP', 'Google Cloud Storage', '', '',
     'https://accounts.google.com/o/oauth2/v2/auth', 'https://oauth2.googleapis.com/token',
     'https://www.googleapis.com/auth/devstorage.read_write https://www.googleapis.com/auth/userinfo.email openid', '', true, now()),
    (gen_random_uuid(), 'GMAIL', 'Gmail', '', '',
     'https://accounts.google.com/o/oauth2/v2/auth', 'https://oauth2.googleapis.com/token',
     'https://www.googleapis.com/auth/gmail.modify https://www.googleapis.com/auth/userinfo.email openid', '', true, now()),
    (gen_random_uuid(), 'OUTLOOK', 'Office 365 Outlook', '', '',
     'https://login.microsoftonline.com/common/oauth2/v2.0/authorize', 'https://login.microsoftonline.com/common/oauth2/v2.0/token',
     'offline_access openid profile email Mail.ReadWrite User.Read', '', true, now()),
    (gen_random_uuid(), 'ONEDRIVE', 'Microsoft OneDrive', '', '',
     'https://login.microsoftonline.com/common/oauth2/v2.0/authorize', 'https://login.microsoftonline.com/common/oauth2/v2.0/token',
     'offline_access openid profile email Files.ReadWrite.All User.Read', '', true, now()),
    (gen_random_uuid(), 'TEAMS', 'Microsoft Teams', '', '',
     'https://login.microsoftonline.com/common/oauth2/v2.0/authorize', 'https://login.microsoftonline.com/common/oauth2/v2.0/token',
     'offline_access openid profile email Files.ReadWrite.All Sites.ReadWrite.All User.Read', '', true, now()),
    (gen_random_uuid(), 'DROPBOX', 'Dropbox', '', '',
     'https://www.dropbox.com/oauth2/authorize', 'https://api.dropboxapi.com/oauth2/token', '', '', true, now()),
    (gen_random_uuid(), 'QUICKBOOKS', 'QuickBooks', '', '',
     'https://appcenter.intuit.com/connect/oauth2', 'https://oauth.platform.intuit.com/oauth2/v1/tokens/bearer',
     'com.intuit.quickbooks.accounting openid profile email', '', true, now())
ON CONFLICT ("ProviderCode") DO UPDATE SET
    "Scopes" = CASE WHEN EXCLUDED."ProviderCode" IN ('GMAIL', 'OUTLOOK') THEN EXCLUDED."Scopes" ELSE catalog."ConnectorProviders"."Scopes" END,
    "ModifiedAtUtc" = CASE WHEN EXCLUDED."ProviderCode" IN ('GMAIL', 'OUTLOOK') THEN now() ELSE catalog."ConnectorProviders"."ModifiedAtUtc" END;

-- Prefer renaming legacy email provider; if QUICKBOOKS already exists, deactivate the old code
DO $$
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
        SET "IsActive" = false,
            "ModifiedAtUtc" = now()
        WHERE "ProviderCode" = 'QUICKBOOKS_EMAIL';
    END IF;
END $$;

-- 'Seed complete. Set ClientId, ClientSecret, RedirectUri per provider.'
