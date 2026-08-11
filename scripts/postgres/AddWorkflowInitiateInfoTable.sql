-- Run on each tenant database if workflow create fails with:
-- "relation workflow.WorkflowInitiateInfo does not exist" -- Postgres
-- Ported from scripts/AddWorkflowInitiateInfoTable.sql -- Phase 3.
-- Same table as CreateWorkflowSchemaComplete.sql (postgres/) -- redundant-but-idempotent
-- standalone-apply variant.

CREATE SCHEMA IF NOT EXISTS workflow;

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
