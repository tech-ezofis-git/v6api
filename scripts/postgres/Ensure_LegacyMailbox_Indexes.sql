-- Add list-performance indexes on all workflow.Inbox_*/Sent_*/Completed_* tables -- Postgres
-- Ported from scripts/Ensure_LegacyMailbox_Indexes.sql -- Phase 3.
-- Run on TENANT database (safe to re-run). Within-one-database dynamic loop, no
-- cross-database concern. INCLUDE columns ported directly (native Postgres 11+ feature).

DO $$
DECLARE
    tbl record;
    idx_instance text;
    idx_user text;
BEGIN
    FOR tbl IN
        SELECT table_name FROM information_schema.tables
        WHERE table_schema = 'workflow'
          AND (table_name LIKE 'Inbox\_%' ESCAPE '\' OR table_name LIKE 'Sent\_%' ESCAPE '\' OR table_name LIKE 'Completed\_%' ESCAPE '\')
    LOOP
        idx_instance := 'IX_' || tbl.table_name || '_Instance_Created';
        idx_user := 'IX_' || tbl.table_name || '_User_Created';

        IF NOT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'workflow' AND indexname = idx_instance) THEN
            EXECUTE format(
                'CREATE INDEX %I ON workflow.%I ("workflowInstanceId", "transaction_createdAt" DESC, "id" DESC) INCLUDE ("userId", "transaction_createdBy", "transactionId", "name", "referenceNumber", "stage", "review")',
                idx_instance, tbl.table_name);
        END IF;

        IF NOT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'workflow' AND indexname = idx_user) THEN
            EXECUTE format(
                'CREATE INDEX %I ON workflow.%I ("userId", "transaction_createdAt" DESC, "id" DESC) INCLUDE ("transaction_createdBy", "workflowInstanceId", "transactionId", "name", "referenceNumber")',
                idx_user, tbl.table_name);
        END IF;
    END LOOP;
END $$;

-- 'Index ensure complete.'
