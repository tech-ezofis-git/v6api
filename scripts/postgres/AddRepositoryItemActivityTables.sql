-- Optional repository item timeline/comments (run on tenant DB if tables are missing) -- Postgres
-- Ported from scripts/AddRepositoryItemActivityTables.sql -- Phase 3.
-- Same tables as CreateRepositorySchema.sql (postgres/) -- redundant-but-idempotent
-- standalone-apply variant, matching the SQL Server originals' own relationship.
-- Upload/create repository do not require these tables.

CREATE SCHEMA IF NOT EXISTS repository;

CREATE TABLE IF NOT EXISTS repository."ItemTimelineEvents" (
    "Id" uuid NOT NULL DEFAULT gen_random_uuid() CONSTRAINT "PK_ItemTimelineEvents" PRIMARY KEY,
    "TenantId" uuid NOT NULL,
    "RepositoryId" uuid NOT NULL,
    "ItemId" uuid NOT NULL,
    "EventType" varchar(64) NOT NULL,
    "Title" varchar(500) NOT NULL,
    "Description" text NULL,
    "ActorType" varchar(64) NULL,
    "ActorName" varchar(256) NULL,
    "ActorUserId" uuid NULL,
    "CreatedBy" uuid NULL,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "IsDeleted" boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS "IX_ItemTimelineEvents_Item" ON repository."ItemTimelineEvents" ("TenantId", "RepositoryId", "ItemId", "IsDeleted", "CreatedAtUtc");

CREATE TABLE IF NOT EXISTS repository."ItemComments" (
    "Id" uuid NOT NULL DEFAULT gen_random_uuid() CONSTRAINT "PK_ItemComments" PRIMARY KEY,
    "TenantId" uuid NOT NULL,
    "RepositoryId" uuid NOT NULL,
    "ItemId" uuid NOT NULL,
    "Body" text NOT NULL,
    "CreatedBy" uuid NOT NULL,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "ModifiedAtUtc" timestamptz NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS "IX_ItemComments_Item" ON repository."ItemComments" ("TenantId", "RepositoryId", "ItemId", "IsDeleted", "CreatedAtUtc");
