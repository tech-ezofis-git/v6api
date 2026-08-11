-- =============================================
-- Tenant: dbo.connector (modern OAuth schema) -- Postgres
-- Ported from scripts/Create-Connector-Table.sql -- Phase 3.
-- Same table as 02_CreateTenantDatabase.sql Part 5 -- redundant-but-idempotent.
--
-- NOT PORTED: the SQL Server original's legacy-schema-migration branch (COL_LENGTH
-- check for a pre-existing lowercase-column v5-era dbo.connector, TRY_CONVERT-based
-- dynamic SQL copy into the modern schema, connector_legacy_backup table). That branch
-- exists to upgrade an EXISTING SQL Server tenant database in place, before its data
-- is ever copied to Postgres. No Postgres tenant database will ever have that legacy
-- shape -- every Postgres tenant DB is created fresh by this migration program, either
-- via this script (new signups) or via Phase 7's data-copy tooling (existing tenants,
-- which copies FROM an already-modern-schema SQL Server source, since that upgrade is
-- expected to have already run there). If a real tenant's SQL Server dbo.connector
-- somehow hasn't been upgraded to the modern schema by the time Phase 7 copies its
-- data, run the ORIGINAL SQL Server script against it first -- this Postgres port is
-- not the place to reimplement that one-time legacy upgrade.
-- =============================================

CREATE SCHEMA IF NOT EXISTS dbo;

CREATE TABLE IF NOT EXISTS dbo."connector" (
    "Id" uuid NOT NULL CONSTRAINT "PK_connector" PRIMARY KEY,
    "Name" varchar(256) NOT NULL,
    "ProviderCode" varchar(64) NOT NULL,
    "ConfigJson" text NULL,
    "AccessToken" text NULL,
    "RefreshToken" text NULL,
    "TokenExpiresAtUtc" timestamptz NULL,
    "ExternalAccountEmail" varchar(320) NULL,
    "ExternalAccountId" varchar(256) NULL,
    "OAuthStatus" varchar(32) NOT NULL DEFAULT 'Pending',
    "IsDefault" boolean NOT NULL DEFAULT false,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "ModifiedAtUtc" timestamptz NULL,
    "CreatedBy" uuid NOT NULL,
    "ModifiedBy" uuid NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS "IX_connector_IsDeleted" ON dbo."connector" ("IsDeleted");
CREATE INDEX IF NOT EXISTS "IX_connector_ProviderCode" ON dbo."connector" ("ProviderCode") WHERE "IsDeleted" = false;

-- 'dbo.connector created (modern schema).'
