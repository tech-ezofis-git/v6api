-- AP Agent job progress (Python PATCH + UI polling) -- Postgres.
-- Ported from scripts/Create_ApAgentJobProgress.sql -- Phase 3.
-- Standalone script for applying this one table to existing tenants without
-- re-running the whole CreateWorkflowSchemaComplete.sql (which also creates it,
-- redundant-but-idempotent, same relationship the SQL Server originals had).
-- Run on tenant database.

CREATE SCHEMA IF NOT EXISTS workflow;

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
