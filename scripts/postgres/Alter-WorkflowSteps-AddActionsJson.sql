-- Add ActionsJson to workflow.WorkflowSteps (outgoing Rules per activity from designer JSON) -- Postgres
-- Ported from scripts/Alter-WorkflowSteps-AddActionsJson.sql -- Phase 3.
-- Already added by CreateWorkflowSchemaComplete.sql (postgres/) -- redundant-but-idempotent
-- standalone-apply variant.

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'workflow' AND table_name = 'WorkflowSteps') THEN
        ALTER TABLE workflow."WorkflowSteps" ADD COLUMN IF NOT EXISTS "ActionsJson" text NULL;
    END IF;
END $$;

-- Example ActionsJson for AP AGENT block DR97uPaylMtwahvi3XYr_:
-- [{"Id":"5EOS4AO4HIdvn1aCipx79","ProceedAction":"APPROVED","ToBlockId":"so19PaUUTXJsN9kBXb3N6"},
--  {"Id":"Ky1L1OSEi6bfegdh3xYNA","ProceedAction":"REJECTED","ToBlockId":"tGLZHXsPrkiaMWWWm4hhQ"},
--  {"Id":"tjwZoRiRJIt4132e_a61u","ProceedAction":"PARTIALLY APPROVED","ToBlockId":"zigR-RzJQPjLv3ckgndxU"}]
--
-- Re-sync steps from designer JSON via PUT /api/workflows/{id} or POST sync-steps to populate.
