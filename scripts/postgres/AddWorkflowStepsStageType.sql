-- Add StageType to workflow.WorkflowSteps (idempotent) -- Postgres
-- Ported from scripts/AddWorkflowStepsStageType.sql -- Phase 3.
-- Already added by CreateWorkflowSchemaComplete.sql (postgres/) -- redundant-but-idempotent
-- standalone-apply variant.

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'workflow' AND table_name = 'WorkflowSteps') THEN
        ALTER TABLE workflow."WorkflowSteps" ADD COLUMN IF NOT EXISTS "StageType" varchar(64) NULL;
    END IF;
END $$;
