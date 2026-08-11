-- =============================================
-- TENANT DATABASE - COMPLETE SETUP (Postgres)
-- Ported from scripts/02_CreateTenantDatabase.sql (SQL Server) -- Phase 3.
-- Run this for EACH new tenant database, against that tenant's own Postgres DB.
--
-- IMPORTANT PRE-REQUISITE ORDER, different from the SQL Server version:
--   1. Create the physical database (TenantDatabaseCreator.cs, Phase 1).
--   2. Run `dotnet ef database update` for UsersDbContext against it (Phase 2) --
--      this creates ALL 9 users.* tables (Users, Roles, UserRoles, RolePermissions,
--      Groups, UserGroups, Menus, RoleMenus, PermissionCategories). Do NOT run this
--      script before that -- Part 1 below only seeds DATA into tables EF must have
--      already created.
--   3. THEN run this script.
--
-- On SQL Server, the old script created the users.* tables directly with raw DDL and
-- inserted fake users.__EFMigrationsHistory rows (one per historical migration ID:
-- InitialUsers, AddCustomRoles, AddPermissionCategories, AddMenus) so that a later
-- `context.Database.Migrate()` call would skip re-creating them. That trick does not
-- carry over to Postgres: Phase 2 regenerated UsersDbContext's migration history as
-- ONE consolidated 'InitialPostgres' migration, not 15 incremental ones -- there is no
-- per-table migration ID left to fake/skip anymore. So the users.* CREATE TABLE
-- statements and the migration-history-faking inserts are gone from this port
-- entirely; only the two seed-data sets (Menus, PermissionCategories) remain, applied
-- as idempotent inserts against tables EF already created.
-- =============================================

-- =============================================
-- PART 1: USERS SEED DATA -- DROPPED, NOT PORTED
-- =============================================
-- Discovered while testing this port against the scratch instance (not visible from
-- reading the SQL alone): UsersDbContext.cs's OnModelCreating already seeds BOTH Menu
-- and PermissionCategory via EF's HasData(), driven by the MenuDefaults.All /
-- PermissionCategoryDefaults.All C# constants (src/Modules/Users/Users.Domain/). That
-- means Phase 2's `dotnet ef database update` already inserts this seed data as part
-- of applying the InitialPostgres migration -- confirmed by running it against a
-- scratch tenant DB and finding Menus/PermissionCategories already populated before
-- this script ever ran.
--
-- More importantly: the CURRENT MenuDefaults/PermissionCategoryDefaults seed values
-- have DRIFTED from this old SQL script's hardcoded seed data -- e.g. the C# defaults
-- use Keys like "workflow"/"task"/"folder"/"workspace"/"settings", not this script's
-- "dashboard"/"inbox"/"ocr-review"/"processed-invoices"/"approval-queue"/"vendors".
-- Re-inserting this script's stale rows would either silently duplicate categories
-- under different Ids (if Keys differ, which they do) or hard-error on a real Id
-- collision (which is what actually happened in testing -- PK_PermissionCategories
-- violated on the 2nd row, because EF's seed had already claimed those exact Ids from
-- a much older prior version of this same script). The C# constants are the current
-- source of truth; this script's copy of the seed data is not ported.

-- =============================================
-- PART 2: WORKFLOW SCHEMA -- LEGACY SHARED-INSTANCE TABLES
-- =============================================
-- FORWARD FLAG: confirmed via grep + scripts/MigrateToPerWorkflowInstances.sql that
-- this shared workflow.WorkflowInstances/WorkflowStepInstances/WorkflowInstanceSlas
-- design is SUPERSEDED for new tenants. Current tenant signup only creates
-- workflow.WorkflowInstanceLookup (from CreateWorkflowSchemaComplete.sql); real
-- instance data lives in PER-WORKFLOW dynamic tables (WorkflowInstances_{suffix})
-- created by WorkflowTableCreator.cs at publish time (Phase 4's #1 priority file) --
-- which has NO temporal/history tracking at all, confirmed by grep (zero matches for
-- SYSTEM_VERSIONING/PERIOD FOR SYSTEM_TIME/History in that file). So the temporal-table
-- replacement below (Part 3) only matters for tenants that pre-date the per-workflow
-- migration and still have data in this shared table. Ported faithfully rather than
-- dropped, since MigrateToPerWorkflowInstances.sql's own comments imply such tenants
-- may still exist. Phase 4 should NOT try to add temporal/history tracking to the new
-- per-workflow dynamic tables -- that feature never existed there on SQL Server either,
-- so there is nothing to migrate for the current architecture.

CREATE SCHEMA IF NOT EXISTS workflow;

CREATE TABLE IF NOT EXISTS workflow."Workflows" (
    "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantId" uuid NOT NULL,
    "Name" varchar(256) NOT NULL,
    "Description" varchar(2000) NULL,
    "Status" integer NOT NULL DEFAULT 0,
    "TriggerType" integer NOT NULL DEFAULT 0,
    "TriggerConfig" varchar(4000) NULL,
    "Version" integer NOT NULL DEFAULT 1,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "ModifiedAtUtc" timestamptz NULL,
    "CreatedBy" uuid NOT NULL,
    "ModifiedBy" uuid NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS "IX_Workflows_TenantId_IsDeleted" ON workflow."Workflows" ("TenantId", "IsDeleted");

CREATE TABLE IF NOT EXISTS workflow."WorkflowSteps" (
    "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "WorkflowId" uuid NOT NULL REFERENCES workflow."Workflows" ("Id") ON DELETE CASCADE,
    "Name" varchar(256) NOT NULL,
    "Description" varchar(2000) NULL,
    "StepType" integer NOT NULL DEFAULT 0,
    "Order" integer NOT NULL,
    "Config" varchar(4000) NULL,
    "IsRequired" boolean NOT NULL DEFAULT true,
    "AssignedToUserId" uuid NULL,
    "AssignedToRole" varchar(64) NULL,
    "ActivityId" varchar(128) NULL,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_WorkflowSteps_WorkflowId_Order" ON workflow."WorkflowSteps" ("WorkflowId", "Order");
-- SQL Server version had an ALTER-if-missing for ActivityId on pre-existing tables;
-- IF NOT EXISTS above already covers fresh creation, and ADD COLUMN IF NOT EXISTS
-- covers a table ported from an older revision of this same script:
ALTER TABLE workflow."WorkflowSteps" ADD COLUMN IF NOT EXISTS "ActivityId" varchar(128) NULL;

CREATE TABLE IF NOT EXISTS workflow."WorkflowInstances" (
    "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantId" uuid NOT NULL,
    "WorkflowId" uuid NOT NULL,
    "WorkflowName" varchar(256) NOT NULL,
    "WorkflowVersion" integer NOT NULL,
    "Status" integer NOT NULL DEFAULT 0,
    "CurrentStepInstanceId" uuid NULL,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "StartedAtUtc" timestamptz NULL,
    "CompletedAtUtc" timestamptz NULL,
    "StartedBy" uuid NOT NULL,
    "Context" varchar(4000) NULL,
    "ErrorMessage" varchar(2000) NULL,
    -- Extended fields
    "ReferenceNumber" varchar(128) NULL,
    "CustomerName" varchar(256) NULL,
    "CustomerEmail" varchar(256) NULL,
    "CustomerPhone" varchar(64) NULL,
    "Department" varchar(128) NULL,
    "Category" varchar(128) NULL,
    "Priority" integer NOT NULL DEFAULT 1,
    "Tags" varchar(1000) NULL,
    "CustomFieldsJson" varchar(4000) NULL,
    "AssignedToUserId" uuid NULL,
    "AssignedToGroupId" uuid NULL,
    "LastActivityAtUtc" timestamptz NULL,
    "ViewCount" integer NOT NULL DEFAULT 0,
    "IsArchived" boolean NOT NULL DEFAULT false,
    "ArchivedAtUtc" timestamptz NULL,
    "SourceType" varchar(64) NULL,
    "SourceId" varchar(256) NULL
);
CREATE INDEX IF NOT EXISTS "IX_WorkflowInstances_TenantId_WorkflowId" ON workflow."WorkflowInstances" ("TenantId", "WorkflowId");
CREATE INDEX IF NOT EXISTS "IX_WorkflowInstances_TenantId_Status_IsArchived" ON workflow."WorkflowInstances" ("TenantId", "Status", "IsArchived");
CREATE INDEX IF NOT EXISTS "IX_WorkflowInstances_ReferenceNumber" ON workflow."WorkflowInstances" ("ReferenceNumber");
CREATE INDEX IF NOT EXISTS "IX_WorkflowInstances_CustomerEmail" ON workflow."WorkflowInstances" ("CustomerEmail");

CREATE TABLE IF NOT EXISTS workflow."WorkflowStepInstances" (
    "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "WorkflowInstanceId" uuid NOT NULL REFERENCES workflow."WorkflowInstances" ("Id") ON DELETE CASCADE,
    "WorkflowStepId" uuid NOT NULL,
    "StepName" varchar(256) NOT NULL,
    "StepType" integer NOT NULL,
    "Order" integer NOT NULL,
    "Status" integer NOT NULL DEFAULT 0,
    "AssignedToUserId" uuid NULL,
    "AssignedToRole" varchar(64) NULL,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "StartedAtUtc" timestamptz NULL,
    "CompletedAtUtc" timestamptz NULL,
    "CompletedBy" uuid NULL,
    "Result" varchar(4000) NULL,
    "ErrorMessage" varchar(2000) NULL
);
CREATE INDEX IF NOT EXISTS "IX_WorkflowStepInstances_WorkflowInstanceId_Order" ON workflow."WorkflowStepInstances" ("WorkflowInstanceId", "Order");

CREATE TABLE IF NOT EXISTS workflow."WorkflowApprovals" (
    "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantId" uuid NOT NULL,
    "WorkflowInstanceId" uuid NOT NULL,
    "StepInstanceId" uuid NOT NULL,
    "RequestedBy" uuid NOT NULL,
    "AssignedToUserId" uuid NULL,
    "AssignedToRole" varchar(64) NULL,
    "Status" integer NOT NULL DEFAULT 0,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "RespondedAtUtc" timestamptz NULL,
    "RespondedBy" uuid NULL,
    "Comments" varchar(2000) NULL
);
CREATE INDEX IF NOT EXISTS "IX_WorkflowApprovals_TenantId_AssignedToUserId_Status" ON workflow."WorkflowApprovals" ("TenantId", "AssignedToUserId", "Status");

CREATE TABLE IF NOT EXISTS workflow."WorkflowSlas" (
    "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantId" uuid NOT NULL,
    "WorkflowId" uuid NOT NULL REFERENCES workflow."Workflows" ("Id") ON DELETE CASCADE,
    "Priority" integer NOT NULL DEFAULT 1,
    "ResponseTimeMinutes" integer NOT NULL,
    "ResolutionTimeMinutes" integer NOT NULL,
    "EscalationTimeMinutes" integer NULL,
    "EscalateToUserId" uuid NULL,
    "EscalateToRole" varchar(64) NULL,
    "SendNotificationOnBreach" boolean NOT NULL DEFAULT true,
    "NotificationEmails" varchar(1000) NULL,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "ModifiedAtUtc" timestamptz NULL,
    CONSTRAINT "UQ_WorkflowSlas_WorkflowId" UNIQUE ("WorkflowId")
);

CREATE TABLE IF NOT EXISTS workflow."WorkflowInstanceSlas" (
    "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "WorkflowInstanceId" uuid NOT NULL REFERENCES workflow."WorkflowInstances" ("Id") ON DELETE CASCADE,
    "Priority" integer NOT NULL,
    "ResponseDeadline" timestamptz NOT NULL,
    "ResolutionDeadline" timestamptz NOT NULL,
    "EscalationDeadline" timestamptz NULL,
    "ResponseAchievedAt" timestamptz NULL,
    "ResolutionAchievedAt" timestamptz NULL,
    "ResponseStatus" integer NOT NULL DEFAULT 0,
    "ResolutionStatus" integer NOT NULL DEFAULT 0,
    "IsEscalated" boolean NOT NULL DEFAULT false,
    "EscalatedAt" timestamptz NULL,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "UQ_WorkflowInstanceSlas_WorkflowInstanceId" UNIQUE ("WorkflowInstanceId")
);
CREATE INDEX IF NOT EXISTS "IX_WorkflowInstanceSlas_ResponseStatus_ResolutionStatus" ON workflow."WorkflowInstanceSlas" ("ResponseStatus", "ResolutionStatus");

-- =============================================
-- PART 3: TEMPORAL TABLE REPLACEMENT (Decision 2 -- trigger-based history table)
-- =============================================
-- SQL Server used SYSTEM_VERSIONING (PERIOD FOR SYSTEM_TIME) on WorkflowInstances,
-- generating workflow.WorkflowInstancesHistory automatically. Postgres has no native
-- equivalent. Replacement: an AFTER UPDATE/DELETE trigger copies the prior row image
-- into a hand-maintained history table, stamped with when the change was captured.
-- WorkflowInstances' own shape is untouched (no hidden generated period columns added,
-- unlike SQL Server) -- history_recorded_at on the history table records "when" instead.

CREATE TABLE IF NOT EXISTS workflow."WorkflowInstancesHistory" (
    LIKE workflow."WorkflowInstances" INCLUDING DEFAULTS,
    "history_id" bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "history_operation" varchar(1) NOT NULL,  -- 'U' = update, 'D' = delete
    "history_recorded_at" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_WorkflowInstancesHistory_Id" ON workflow."WorkflowInstancesHistory" ("Id");

-- Explicit column list (excluding the history table's own GENERATED ALWAYS AS IDENTITY
-- history_id column -- `DEFAULT` is not valid inside a SELECT list, only VALUES(...),
-- and a bare `INSERT ... SELECT OLD.*` would try to supply a value for history_id
-- positionally, which GENERATED ALWAYS rejects. Found by testing: this exact form
-- failed with "ERROR: DEFAULT is not allowed in this context".
CREATE OR REPLACE FUNCTION workflow.fn_workflow_instances_history() RETURNS trigger AS $$
BEGIN
    IF (TG_OP = 'UPDATE') THEN
        INSERT INTO workflow."WorkflowInstancesHistory" (
            "Id", "TenantId", "WorkflowId", "WorkflowName", "WorkflowVersion", "Status",
            "CurrentStepInstanceId", "CreatedAtUtc", "StartedAtUtc", "CompletedAtUtc", "StartedBy",
            "Context", "ErrorMessage", "ReferenceNumber", "CustomerName", "CustomerEmail",
            "CustomerPhone", "Department", "Category", "Priority", "Tags", "CustomFieldsJson",
            "AssignedToUserId", "AssignedToGroupId", "LastActivityAtUtc", "ViewCount", "IsArchived",
            "ArchivedAtUtc", "SourceType", "SourceId", history_operation, history_recorded_at
        )
        SELECT OLD."Id", OLD."TenantId", OLD."WorkflowId", OLD."WorkflowName", OLD."WorkflowVersion", OLD."Status",
               OLD."CurrentStepInstanceId", OLD."CreatedAtUtc", OLD."StartedAtUtc", OLD."CompletedAtUtc", OLD."StartedBy",
               OLD."Context", OLD."ErrorMessage", OLD."ReferenceNumber", OLD."CustomerName", OLD."CustomerEmail",
               OLD."CustomerPhone", OLD."Department", OLD."Category", OLD."Priority", OLD."Tags", OLD."CustomFieldsJson",
               OLD."AssignedToUserId", OLD."AssignedToGroupId", OLD."LastActivityAtUtc", OLD."ViewCount", OLD."IsArchived",
               OLD."ArchivedAtUtc", OLD."SourceType", OLD."SourceId", 'U', now();
        RETURN NEW;
    ELSIF (TG_OP = 'DELETE') THEN
        INSERT INTO workflow."WorkflowInstancesHistory" (
            "Id", "TenantId", "WorkflowId", "WorkflowName", "WorkflowVersion", "Status",
            "CurrentStepInstanceId", "CreatedAtUtc", "StartedAtUtc", "CompletedAtUtc", "StartedBy",
            "Context", "ErrorMessage", "ReferenceNumber", "CustomerName", "CustomerEmail",
            "CustomerPhone", "Department", "Category", "Priority", "Tags", "CustomFieldsJson",
            "AssignedToUserId", "AssignedToGroupId", "LastActivityAtUtc", "ViewCount", "IsArchived",
            "ArchivedAtUtc", "SourceType", "SourceId", history_operation, history_recorded_at
        )
        SELECT OLD."Id", OLD."TenantId", OLD."WorkflowId", OLD."WorkflowName", OLD."WorkflowVersion", OLD."Status",
               OLD."CurrentStepInstanceId", OLD."CreatedAtUtc", OLD."StartedAtUtc", OLD."CompletedAtUtc", OLD."StartedBy",
               OLD."Context", OLD."ErrorMessage", OLD."ReferenceNumber", OLD."CustomerName", OLD."CustomerEmail",
               OLD."CustomerPhone", OLD."Department", OLD."Category", OLD."Priority", OLD."Tags", OLD."CustomFieldsJson",
               OLD."AssignedToUserId", OLD."AssignedToGroupId", OLD."LastActivityAtUtc", OLD."ViewCount", OLD."IsArchived",
               OLD."ArchivedAtUtc", OLD."SourceType", OLD."SourceId", 'D', now();
        RETURN OLD;
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_workflow_instances_history ON workflow."WorkflowInstances";
CREATE TRIGGER trg_workflow_instances_history
    AFTER UPDATE OR DELETE ON workflow."WorkflowInstances"
    FOR EACH ROW EXECUTE FUNCTION workflow.fn_workflow_instances_history();

-- =============================================
-- PART 4: DMS SCHEMA (Document Management - Folder Structure)
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
    CONSTRAINT "PK_RepositoryFolderConfig" PRIMARY KEY ("RepositoryId", "LevelOrder")
);

CREATE TABLE IF NOT EXISTS dms."DocumentWorkflowLink" (
    "DocumentId" uuid NOT NULL,
    "RepositoryId" uuid NOT NULL,
    "WorkflowInstanceId" uuid NOT NULL,
    "WorkflowId" uuid NOT NULL,
    "TenantId" uuid NOT NULL,
    "LinkedAtUtc" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PK_DocumentWorkflowLink" PRIMARY KEY ("DocumentId", "RepositoryId")
);
CREATE INDEX IF NOT EXISTS "IX_DocumentWorkflowLink_WorkflowInstance" ON dms."DocumentWorkflowLink" ("WorkflowInstanceId");
CREATE INDEX IF NOT EXISTS "IX_DocumentWorkflowLink_Tenant_Workflow" ON dms."DocumentWorkflowLink" ("TenantId", "WorkflowId");

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
-- SQL Server's INCLUDE-columns covering index has no direct Postgres equivalent;
-- a plain composite index gets the same query-planning benefit for the common filter
-- shape (INCLUDE columns just avoid heap lookups, which Postgres's index-only scans
-- can already do here once autovacuum keeps the visibility map current).
CREATE INDEX IF NOT EXISTS "IX_sample_items_Folder" ON dms."sample_items" ("RepositoryId", "IsDeleted", "Year", "InvoiceType", "VendorName");
CREATE INDEX IF NOT EXISTS "IX_sample_items_Workflow" ON dms."sample_items" ("WorkflowInstanceId") WHERE "WorkflowInstanceId" IS NOT NULL;

-- =============================================
-- PART 5: CONNECTOR (modern OAuth -- tenant DB, per-tenant connected accounts)
-- =============================================
-- Distinct from catalog."ConnectorProviders" (01a/01b) -- this is the tenant-level
-- table of a tenant's actual configured connector instances, not the global list of
-- OAuth provider definitions. No EF ownership either way (Workflow.Infrastructure's
-- ConnectorService.cs uses raw ADO.NET) -- straightforward script-owned table, no
-- exact-match concern like the Catalog three.
--
-- Unlike Postgres's `public` schema, `dbo` is not created automatically -- it exists
-- in the Catalog database only because EF's migration includes an EnsureSchema call
-- for it (Phase 2). Tenant databases never get a Catalog-context migration applied, so
-- it needs an explicit CREATE SCHEMA here (found by testing: the first run of this
-- script against a fresh scratch tenant DB failed with "schema dbo does not exist").

CREATE SCHEMA IF NOT EXISTS dbo;

CREATE TABLE IF NOT EXISTS dbo."connector" (
    "Id" uuid NOT NULL PRIMARY KEY,
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

-- =============================================
-- PART 6: WORKFLOW EF MIGRATIONS HISTORY -- DROPPED, NOT PORTED
-- =============================================
-- The SQL Server script created workflow.__EFMigrationsHistory and inserted a fake row
-- for '20260226000001_WorkflowModuleComplete'. Confirmed WorkflowDbContext has never
-- had an actual Migrations/ folder or any real EF migration (script-first per the
-- resolved decision, engineering plan Sec 6) -- so that migration ID was never real to
-- begin with; this table+row was vestigial even on SQL Server. Not ported.

-- =============================================
-- VERIFICATION
-- =============================================
SELECT table_schema AS "Schema", table_name AS "TableName"
FROM information_schema.tables
WHERE table_schema IN ('users', 'workflow', 'dms')
  AND table_type = 'BASE TABLE'
ORDER BY table_schema, table_name;
