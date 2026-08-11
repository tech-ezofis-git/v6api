-- Add dms.StagingItems for existing tenant DBs (temp indexing before export) -- Postgres
-- Ported from scripts/AddDmsStagingItems.sql -- Phase 3.
-- Run on tenant database. New signups get this via CreateDmsSchema.sql (postgres/).

CREATE SCHEMA IF NOT EXISTS dms;

CREATE TABLE IF NOT EXISTS dms."StagingItems" (
    "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantId" uuid NOT NULL,
    "RepositoryId" uuid NOT NULL,
    "Year" smallint NOT NULL,
    "InvoiceType" varchar(64) NOT NULL,
    "VendorName" varchar(256) NOT NULL,
    "FileName" varchar(512) NOT NULL,
    "FilePath" varchar(1024) NULL,
    "StoragePath" varchar(1024) NULL,
    "Status" smallint NOT NULL DEFAULT 0,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "CreatedBy" uuid NOT NULL,
    "UpdatedAt" timestamptz NULL,
    "UpdatedBy" uuid NULL,
    "ExportedAt" timestamptz NULL,
    "ExportedToItemId" uuid NULL
);
CREATE INDEX IF NOT EXISTS "IX_StagingItems_Repository_Status" ON dms."StagingItems" ("RepositoryId", "Status");
CREATE INDEX IF NOT EXISTS "IX_StagingItems_CreatedBy" ON dms."StagingItems" ("CreatedBy");
CREATE INDEX IF NOT EXISTS "IX_StagingItems_CreatedAt" ON dms."StagingItems" ("CreatedAt" DESC);
