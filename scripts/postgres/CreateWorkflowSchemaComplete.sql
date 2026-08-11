-- =============================================
-- Workflow Module - Complete Schema (Postgres)
-- Ported from scripts/CreateWorkflowSchemaComplete.sql (SQL Server) -- Phase 3.
-- Database: Tenant-specific database. This is the schema-complete script the app
-- actually runs at tenant-signup/schema-ensure time (TenantSignupService.
-- ApplyWorkflowSchemaAsync) -- embedded into the API build, unlike 02_CreateTenantDatabase.sql.
-- =============================================

CREATE SCHEMA IF NOT EXISTS workflow;

-- =============================================
-- Core Workflow Tables
-- =============================================

CREATE TABLE IF NOT EXISTS workflow."Workflows" (
    "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantId" uuid NOT NULL,
    "Name" varchar(256) NOT NULL,
    "Description" varchar(2000) NULL,
    "Status" integer NOT NULL, -- 0=Draft, 1=Published, 2=Archived
    "TriggerType" integer NOT NULL, -- 0=Manual, 1=Scheduled, 2=Event
    "TriggerConfig" varchar(4000) NULL,
    "Version" integer NOT NULL DEFAULT 1,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "ModifiedAtUtc" timestamptz NULL,
    "CreatedBy" uuid NOT NULL,
    "ModifiedBy" uuid NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false,
    "RepositoryId" varchar(64) NULL,
    "FormId" varchar(64) NULL
);
CREATE INDEX IF NOT EXISTS "IX_Workflows_TenantId_IsDeleted" ON workflow."Workflows" ("TenantId", "IsDeleted");
ALTER TABLE workflow."Workflows" ADD COLUMN IF NOT EXISTS "RepositoryId" varchar(64) NULL;
ALTER TABLE workflow."Workflows" ADD COLUMN IF NOT EXISTS "FormId" varchar(64) NULL;

CREATE TABLE IF NOT EXISTS workflow."WorkflowSteps" (
    "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "WorkflowId" uuid NOT NULL REFERENCES workflow."Workflows" ("Id") ON DELETE CASCADE,
    "Name" varchar(256) NOT NULL,
    "Description" varchar(2000) NULL,
    "StepType" integer NOT NULL, -- 0=Task, 1=Approval, 2=Notification, 3=Automation
    "Order" integer NOT NULL,
    "Config" varchar(4000) NULL,
    "IsRequired" boolean NOT NULL DEFAULT true,
    "AssignedToUserId" uuid NULL,
    "AssignedToRole" varchar(64) NULL,
    "ApprovedNextStepId" uuid NULL,
    "RejectedNextStepId" uuid NULL,
    "ApprovalPolicy" integer NOT NULL DEFAULT 1, -- 0=AllMustApprove, 1=AnyOneApprove
    "ApproversJson" varchar(4000) NULL,
    "ActivityId" varchar(128) NULL,
    "StageType" varchar(64) NULL,
    "ActionsJson" text NULL,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS "IX_WorkflowSteps_WorkflowId_Order" ON workflow."WorkflowSteps" ("WorkflowId", "Order");
ALTER TABLE workflow."WorkflowSteps" ADD COLUMN IF NOT EXISTS "ApprovedNextStepId" uuid NULL;
ALTER TABLE workflow."WorkflowSteps" ADD COLUMN IF NOT EXISTS "RejectedNextStepId" uuid NULL;
ALTER TABLE workflow."WorkflowSteps" ADD COLUMN IF NOT EXISTS "ApprovalPolicy" integer NOT NULL DEFAULT 1;
ALTER TABLE workflow."WorkflowSteps" ADD COLUMN IF NOT EXISTS "ApproversJson" varchar(4000) NULL;
ALTER TABLE workflow."WorkflowSteps" ADD COLUMN IF NOT EXISTS "ActivityId" varchar(128) NULL;
ALTER TABLE workflow."WorkflowSteps" ADD COLUMN IF NOT EXISTS "StageType" varchar(64) NULL;
ALTER TABLE workflow."WorkflowSteps" ADD COLUMN IF NOT EXISTS "ActionsJson" text NULL;

-- WorkflowInstanceLookup: Maps InstanceId -> WorkflowId for per-workflow tables.
-- Used for inbox/sent/completed queries. Instances live in WorkflowInstances_{suffix}
-- (created by WorkflowTableCreator.cs at publish time -- Phase 4 -- not here).
CREATE TABLE IF NOT EXISTS workflow."WorkflowInstanceLookup" (
    "InstanceId" uuid PRIMARY KEY,
    "WorkflowId" uuid NOT NULL,
    "TenantId" uuid NOT NULL,
    "WorkflowName" varchar(256) NOT NULL,
    "Status" integer NOT NULL,
    "AssignedToUserId" uuid NULL,
    "StartedBy" uuid NOT NULL,
    "CreatedAtUtc" timestamptz NOT NULL,
    "LastActivityAtUtc" timestamptz NULL,
    "CompletedAtUtc" timestamptz NULL,
    "IsArchived" boolean NOT NULL DEFAULT false,
    "Priority" integer NOT NULL DEFAULT 1,
    "CurrentStepInstanceId" uuid NULL,
    "SlaPriority" integer NULL,
    "ResponseStatus" integer NULL,
    "ResolutionStatus" integer NULL,
    "ResponseDeadline" timestamptz NULL,
    "ResolutionDeadline" timestamptz NULL,
    "IsEscalated" boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS "IX_WorkflowInstanceLookup_WorkflowId" ON workflow."WorkflowInstanceLookup" ("WorkflowId");
CREATE INDEX IF NOT EXISTS "IX_WorkflowInstanceLookup_AssignedTo_Status" ON workflow."WorkflowInstanceLookup" ("AssignedToUserId", "Status", "IsArchived");
CREATE INDEX IF NOT EXISTS "IX_WorkflowInstanceLookup_StartedBy" ON workflow."WorkflowInstanceLookup" ("StartedBy", "IsArchived");
CREATE INDEX IF NOT EXISTS "IX_WorkflowInstanceLookup_SlaBreach" ON workflow."WorkflowInstanceLookup" ("ResponseStatus", "ResolutionStatus");

-- NOTE: WorkflowInstances and WorkflowStepInstances are PER-WORKFLOW
-- (WorkflowInstances_{suffix}, WorkflowStepInstances_{suffix}). Created by
-- WorkflowTableCreator.cs (Phase 4) when a workflow is published. No temporal/history
-- tracking on those -- see Phase 3 forward-flag in 02_CreateTenantDatabase.sql.

-- =============================================
-- Approval & SLA Tables
-- =============================================

CREATE TABLE IF NOT EXISTS workflow."WorkflowApprovals" (
    "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantId" uuid NOT NULL,
    "WorkflowInstanceId" uuid NOT NULL,
    "StepInstanceId" uuid NOT NULL,
    "RequestedBy" uuid NOT NULL,
    "AssignedToUserId" uuid NULL,
    "AssignedToRole" varchar(64) NULL,
    "Status" integer NOT NULL, -- 0=Pending, 1=Approved, 2=Rejected
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
    "Priority" integer NOT NULL, -- 0=Low, 1=Normal, 2=High, 3=Critical
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

-- NOTE: WorkflowInstanceSlas is per-workflow (WorkflowInstanceSlas_{suffix}), created by WorkflowTableCreator.cs.

-- =============================================
-- Extended Feature Tables
-- =============================================

CREATE TABLE IF NOT EXISTS workflow."groupUser" (
    "Id" bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "GroupId" integer NOT NULL,
    "UserId" uuid NOT NULL,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "CreatedBy" uuid NULL,
    "ModifiedAtUtc" timestamptz NULL,
    "ModifiedBy" uuid NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS "IX_groupUser_GroupId_UserId_IsDeleted" ON workflow."groupUser" ("GroupId", "UserId", "IsDeleted");

CREATE TABLE IF NOT EXISTS workflow."jiraCreateIssue" (
    "Id" bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "WorkflowId" uuid NOT NULL,
    "ProcessId" integer NOT NULL,
    "IssueId" varchar(128) NULL,
    "Key" varchar(128) NULL,
    "Self" varchar(512) NULL,
    "Assignee" varchar(256) NULL,
    "Status" varchar(128) NULL,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "CreatedBy" uuid NULL,
    "ModifiedAtUtc" timestamptz NULL,
    "ModifiedBy" uuid NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS "IX_jiraCreateIssue_WorkflowId_ProcessId_IsDeleted" ON workflow."jiraCreateIssue" ("WorkflowId", "ProcessId", "IsDeleted");

CREATE TABLE IF NOT EXISTS workflow."WorkflowUsers" (
    "Id" bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "TenantId" uuid NULL,
    "WorkflowId" uuid NOT NULL,
    "UserId" uuid NULL,
    "GroupId" integer NULL,
    "UserCategory" varchar(128) NULL,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "ModifiedAtUtc" timestamptz NULL,
    "CreatedBy" uuid NULL,
    "ModifiedBy" uuid NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS "IX_WorkflowUsers_WorkflowId_UserId_IsDeleted" ON workflow."WorkflowUsers" ("WorkflowId", "UserId", "IsDeleted");
CREATE INDEX IF NOT EXISTS "IX_WorkflowUsers_WorkflowId_GroupId_IsDeleted" ON workflow."WorkflowUsers" ("WorkflowId", "GroupId", "IsDeleted");

CREATE TABLE IF NOT EXISTS workflow."WorkflowSecurity" (
    "Id" bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "TenantId" uuid NULL,
    "WorkflowId" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "UserCategory" varchar(128) NULL,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "ModifiedAtUtc" timestamptz NULL,
    "CreatedBy" uuid NULL,
    "ModifiedBy" uuid NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS "IX_WorkflowSecurity_WorkflowId_UserId_IsDeleted" ON workflow."WorkflowSecurity" ("WorkflowId", "UserId", "IsDeleted");

CREATE TABLE IF NOT EXISTS workflow."WorkflowDocuments" (
    "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "TenantId" uuid NOT NULL,
    "WorkflowId" uuid NOT NULL,
    "WorkflowInstanceId" uuid NULL,
    "FileName" varchar(512) NOT NULL,
    "Description" varchar(2000) NULL,
    "Type" varchar(64) NULL,
    "Status" integer NOT NULL DEFAULT 0, -- 0=Pending, 1=Uploaded, 2=Approved, 3=Rejected
    "IsMandatory" boolean NOT NULL DEFAULT false,
    "FilePath" varchar(1024) NULL,
    "UploadedAt" timestamptz NULL,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "ModifiedAtUtc" timestamptz NULL,
    "CreatedBy" uuid NOT NULL,
    "ModifiedBy" uuid NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS "IX_WorkflowDocuments_TenantId_WorkflowId_IsDeleted" ON workflow."WorkflowDocuments" ("TenantId", "WorkflowId", "IsDeleted");
CREATE INDEX IF NOT EXISTS "IX_WorkflowDocuments_TenantId_WorkflowInstanceId_IsDeleted" ON workflow."WorkflowDocuments" ("TenantId", "WorkflowInstanceId", "IsDeleted");

-- WorkflowInitiateInfo: auto-initiation config (email, document, master form) per workflow
CREATE TABLE IF NOT EXISTS workflow."WorkflowInitiateInfo" (
    "Id" bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "TenantId" uuid NOT NULL,
    "WorkflowId" uuid NOT NULL,
    "InputType" varchar(256) NOT NULL,
    "InputJson" text NULL,
    "Status" integer NOT NULL DEFAULT 0,
    "Remarks" varchar(2000) NOT NULL DEFAULT '',
    "CreatedBy" uuid NOT NULL,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "RepositoryId" integer NULL
);
CREATE INDEX IF NOT EXISTS "IX_WorkflowInitiateInfo_WorkflowId" ON workflow."WorkflowInitiateInfo" ("WorkflowId");
CREATE INDEX IF NOT EXISTS "IX_WorkflowInitiateInfo_TenantId_WorkflowId" ON workflow."WorkflowInitiateInfo" ("TenantId", "WorkflowId");

-- =============================================
-- Workflow EF Migrations History -- DROPPED, NOT PORTED
-- =============================================
-- Same reasoning as 02_CreateTenantDatabase.sql Part 6: WorkflowDbContext has never
-- had a real Migrations/ folder (script-first per the resolved decision), so this
-- fake '20260226000001_WorkflowModuleComplete' history row was vestigial even on
-- SQL Server. Not ported.

-- =============================================
-- AP Agent job progress (Hangfire + Python PATCH polling)
-- =============================================
-- Also exists as a standalone script (Create_ApAgentJobProgress.sql, ported
-- separately) for applying to existing tenants without re-running this whole file --
-- same redundant-but-idempotent relationship the SQL Server originals had.

CREATE TABLE IF NOT EXISTS workflow."ApAgentJobProgress" (
    "JobId" varchar(64) NOT NULL PRIMARY KEY,
    "TenantId" uuid NOT NULL,
    "WorkflowId" uuid NOT NULL,
    "InstanceId" uuid NOT NULL,
    "HangfireState" varchar(32) NULL,
    "Stage" varchar(64) NULL,
    "Message" varchar(2000) NULL,
    "ProgressPercent" integer NULL,
    "ErrorMessage" text NULL,
    "FormData" text NULL,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAtUtc" timestamptz NOT NULL DEFAULT now()
);
ALTER TABLE workflow."ApAgentJobProgress" ADD COLUMN IF NOT EXISTS "FormData" text NULL;
CREATE INDEX IF NOT EXISTS "IX_ApAgentJobProgress_InstanceId_Updated" ON workflow."ApAgentJobProgress" ("InstanceId", "UpdatedAtUtc" DESC);

-- 'Workflow schema created successfully.'
