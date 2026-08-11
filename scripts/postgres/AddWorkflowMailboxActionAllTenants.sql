-- =============================================
-- Add "action" to workflow mailbox tables -- Postgres
-- Ported from scripts/AddWorkflowMailboxActionAllTenants.sql -- Phase 3.
--
-- The SQL Server original has TWO nested loops: an OUTER one over catalog.Tenants
-- (cross-database, connects to each tenant DB in turn) and an INNER one, run WITHIN
-- each tenant database, over workflow.Workflows (find each workflow's dynamically
-- created Inbox_{suffix}/Sent_{suffix}/Completed_{suffix} tables and add an "action"
-- column). Only the outer loop is the cross-database orchestration concern covered by
-- AddRepositoryFolderDocumentSecurityAllTenants.sql's header comment (Phase 6's job,
-- no cross-database USE on Postgres). The INNER loop runs entirely within one tenant
-- database, so it's ported here as real, runnable content -- run this file directly
-- against ONE tenant database; Phase 6's loop should invoke it once per tenant instead
-- of trying to replicate the whole nested structure in one script.
--
-- action: 1 = show verify/approve (default), 0 = hide action buttons
-- =============================================

DO $$
DECLARE
    wf record;
    suffix text;
    tbl_name text;
    tables_updated integer := 0;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'workflow' AND table_name = 'Workflows') THEN
        RAISE NOTICE 'workflow.Workflows does not exist in this database -- nothing to do.';
        RETURN;
    END IF;

    FOR wf IN
        SELECT "Id" FROM workflow."Workflows"
        WHERE "Id" IS NOT NULL AND COALESCE("IsDeleted", false) = false
    LOOP
        -- Same as C#: workflowId.ToString("N")[..8]
        suffix := left(replace(lower(wf."Id"::text), '-', ''), 8);

        FOREACH tbl_name IN ARRAY ARRAY['Inbox_' || suffix, 'Sent_' || suffix, 'Completed_' || suffix]
        LOOP
            IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'workflow' AND table_name = tbl_name)
               AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'workflow' AND table_name = tbl_name AND column_name = 'action')
            THEN
                EXECUTE format('ALTER TABLE workflow.%I ADD COLUMN "action" integer NOT NULL DEFAULT 1', tbl_name);
                tables_updated := tables_updated + 1;
            END IF;
        END LOOP;
    END LOOP;

    RAISE NOTICE 'workflow.Workflows -> mailbox "action", tables updated: %', tables_updated;
END $$;
