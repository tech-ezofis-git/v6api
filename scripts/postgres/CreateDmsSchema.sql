-- =============================================
-- DMS (Document Management System) Schema (Postgres)
-- Ported from scripts/CreateDmsSchema.sql -- Phase 3.
-- Run on tenant database. Created automatically on tenant signup
-- (TenantSignupService.ApplyDmsSchemaAsync). Same tables as 02_CreateTenantDatabase.sql
-- Part 4 -- redundant-but-idempotent, matching the SQL Server originals' own overlap.
-- =============================================

CREATE SCHEMA IF NOT EXISTS dms;

CREATE TABLE IF NOT EXISTS dms."Repository" (
    "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantId" uuid NOT NULL,
    "Code" varchar(32) NOT NULL,
    "Name" varchar(256) NOT NULL,
    "Description" varchar(2000) NULL,
    "ItemsTableName" varchar(128) NOT NULL,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "ModifiedAtUtc" timestamptz NULL,
    CONSTRAINT "UQ_Repository_TenantId_Code" UNIQUE ("TenantId", "Code")
);
CREATE INDEX IF NOT EXISTS "IX_Repository_TenantId" ON dms."Repository" ("TenantId");

CREATE TABLE IF NOT EXISTS dms."RepositoryFolderConfig" (
    "RepositoryId" uuid NOT NULL,
    "LevelOrder" smallint NOT NULL,
    "FieldName" varchar(64) NOT NULL,
    "DisplayName" varchar(128) NOT NULL,
    PRIMARY KEY ("RepositoryId", "LevelOrder")
);

CREATE TABLE IF NOT EXISTS dms."DocumentWorkflowLink" (
    "DocumentId" uuid NOT NULL,
    "RepositoryId" uuid NOT NULL,
    "WorkflowInstanceId" uuid NOT NULL,
    "WorkflowId" uuid NOT NULL,
    "TenantId" uuid NOT NULL,
    "LinkedAtUtc" timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY ("DocumentId", "RepositoryId")
);
CREATE INDEX IF NOT EXISTS "IX_DocumentWorkflowLink_WorkflowInstance" ON dms."DocumentWorkflowLink" ("WorkflowInstanceId");
CREATE INDEX IF NOT EXISTS "IX_DocumentWorkflowLink_Tenant_Workflow" ON dms."DocumentWorkflowLink" ("TenantId", "WorkflowId");

-- StagingItems: Temp indexing (upload + manual index before Export). Status: 0=Draft, 1=Exported.
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

-- Sample repository items table (for testing). Status: 0=Draft, 1=Exported, 2=Archived.
CREATE TABLE IF NOT EXISTS dms."sample_items" (
    "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantId" uuid NOT NULL,
    "RepositoryId" uuid NOT NULL,
    "Year" smallint NOT NULL,
    "InvoiceType" varchar(64) NOT NULL,
    "VendorName" varchar(256) NOT NULL,
    "FileName" varchar(512) NOT NULL,
    "Status" smallint NOT NULL DEFAULT 0,
    "SignStatus" smallint NOT NULL DEFAULT 0,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    "CreatedBy" uuid NOT NULL,
    "UpdatedBy" uuid NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false,
    "DeletedAt" timestamptz NULL,
    "Version" integer NOT NULL DEFAULT 1,
    "WorkflowInstanceId" uuid NULL,
    "ReportNo" varchar(128) NULL,
    "ReferenceNo" varchar(64) NULL
);
-- SQL Server's INCLUDE-columns covering index has no direct Postgres equivalent; see
-- 02_CreateTenantDatabase.sql's note on this same table.
CREATE INDEX IF NOT EXISTS "IX_sample_items_Folder" ON dms."sample_items" ("RepositoryId", "IsDeleted", "Year", "InvoiceType", "VendorName");
CREATE INDEX IF NOT EXISTS "IX_sample_items_Workflow" ON dms."sample_items" ("WorkflowInstanceId") WHERE "WorkflowInstanceId" IS NOT NULL;

-- 'DMS schema setup complete.'
