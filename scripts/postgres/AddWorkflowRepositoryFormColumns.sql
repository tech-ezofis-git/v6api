-- Add RepositoryId and FormId to workflow.Workflows (run on tenant DB) -- Postgres
-- Ported from scripts/AddWorkflowRepositoryFormColumns.sql -- Phase 3.
-- Same columns already added by CreateWorkflowSchemaComplete.sql (postgres/) via
-- ADD COLUMN IF NOT EXISTS -- redundant-but-idempotent standalone-apply variant.
-- Note: SQL Server original also handled a legacy case where RepositoryId was
-- UNIQUEIDENTIFIER and needed ALTER COLUMN to NVARCHAR(64) -- no Postgres tenant DB
-- will ever have had that legacy uuid-typed column (this migration creates the varchar
-- version from the start), so that branch isn't applicable here.

CREATE SCHEMA IF NOT EXISTS workflow;

-- Guarded by table existence (DO block): unlike SQL Server's COL_LENGTH (which
-- returns NULL, not an error, against a missing table), Postgres's ALTER TABLE
-- errors outright if the table doesn't exist yet.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'workflow' AND table_name = 'Workflows') THEN
        ALTER TABLE workflow."Workflows" ADD COLUMN IF NOT EXISTS "RepositoryId" varchar(64) NULL;
        ALTER TABLE workflow."Workflows" ADD COLUMN IF NOT EXISTS "FormId" varchar(64) NULL;
    END IF;
END $$;
