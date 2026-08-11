-- Catalog DB: cross-tenant repository item share grants -- Postgres
-- Ported from scripts/Create_RepositoryItemShares.sql -- Phase 3.
-- Run against the CATALOG database (not tenant DBs).
--
-- EXACT-MATCH REQUIREMENT: table name/case/schema must be catalog."RepositoryItemShares"
-- to match CatalogDbContext.cs's ToTable("RepositoryItemShares", t =>
-- t.ExcludeFromMigrations()) (Phase 2) -- that table is script-owned, never EF-owned,
-- so this script is the only thing that creates it on Postgres. A snake_case rename
-- here without updating that ToTable() call would break CatalogDbContext with
-- "relation does not exist" the first time it queries this table.

CREATE SCHEMA IF NOT EXISTS catalog;

CREATE TABLE IF NOT EXISTS catalog."RepositoryItemShares" (
    "Id" uuid NOT NULL DEFAULT gen_random_uuid() CONSTRAINT "PK_RepositoryItemShares" PRIMARY KEY,
    "ShareToken" varchar(128) NOT NULL,
    "SourceTenantId" uuid NOT NULL,
    "SourceRepositoryId" uuid NOT NULL,
    "SourceItemId" uuid NOT NULL,
    "SharedByUserId" uuid NOT NULL,
    "RecipientEmail" varchar(256) NOT NULL,
    "Message" varchar(2000) NULL,
    "Status" varchar(32) NOT NULL DEFAULT 'Active',
    "ExpiresAtUtc" timestamptz NOT NULL,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "LastAccessedAtUtc" timestamptz NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_RepositoryItemShares_ShareToken" ON catalog."RepositoryItemShares" ("ShareToken");
CREATE INDEX IF NOT EXISTS "IX_RepositoryItemShares_Recipient_Status" ON catalog."RepositoryItemShares" ("RecipientEmail", "Status");
CREATE INDEX IF NOT EXISTS "IX_RepositoryItemShares_Source" ON catalog."RepositoryItemShares" ("SourceTenantId", "SourceRepositoryId", "SourceItemId");

-- PHASE 4 FINDING: these 3 columns were never in the original standalone SQL Server script
-- either -- they only ever existed via RepositoryItemShareCatalogStore.cs's inline COL_LENGTH-
-- guarded ALTERs at app startup (a preexisting drift between this static script and the live
-- app code, not something Phase 3 introduced). Added here too so this script alone leaves the
-- table in the same final shape as running the app once would. Kept in sync with
-- RepositoryItemShareCatalogStore.EnsureTableAsync's ALTER TABLE statements.
ALTER TABLE catalog."RepositoryItemShares" ADD COLUMN IF NOT EXISTS "AutoProvisionGuest" boolean NOT NULL DEFAULT false;
ALTER TABLE catalog."RepositoryItemShares" ADD COLUMN IF NOT EXISTS "WorkflowInstanceId" uuid NULL;
ALTER TABLE catalog."RepositoryItemShares" ADD COLUMN IF NOT EXISTS "Action" integer NOT NULL DEFAULT 0;
