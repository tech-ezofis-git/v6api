-- Add ActivityId / StageType to all per-workflow WorkflowStepInstances_* tables (idempotent) -- Postgres
-- Ported from scripts/AddWorkflowStepInstancesActivityIdStageType.sql -- Phase 3.
-- Same pattern as AddTransactionGuidColumn.sql (postgres/) -- dynamic per-tenant-database
-- loop over matching tables, no cross-database concern.

DO $$
DECLARE
    tbl record;
BEGIN
    FOR tbl IN
        SELECT table_name FROM information_schema.tables
        WHERE table_schema = 'workflow' AND table_name LIKE 'WorkflowStepInstances\_%' ESCAPE '\'
    LOOP
        IF NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema = 'workflow' AND table_name = tbl.table_name AND column_name = 'ActivityId')
        THEN
            EXECUTE format('ALTER TABLE workflow.%I ADD COLUMN "ActivityId" varchar(128) NULL', tbl.table_name);
        END IF;
        IF NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema = 'workflow' AND table_name = tbl.table_name AND column_name = 'StageType')
        THEN
            EXECUTE format('ALTER TABLE workflow.%I ADD COLUMN "StageType" varchar(64) NULL', tbl.table_name);
        END IF;
    END LOOP;
END $$;

-- 'WorkflowStepInstances_* ActivityId/StageType columns ensured.'
