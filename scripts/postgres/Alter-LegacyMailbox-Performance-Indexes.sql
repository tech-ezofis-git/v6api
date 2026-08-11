-- Mailbox / transaction indexes for inbox-sent-completed list performance -- Postgres
-- Ported from scripts/Alter-LegacyMailbox-Performance-Indexes.sql -- Phase 3.
-- Run on tenant DB. Postgres supports CREATE INDEX ... INCLUDE (...) natively (PG 11+),
-- direct translation of SQL Server's INCLUDE clause.

DO $$
DECLARE
    tbl record;
    idx_name text;
BEGIN
    FOR tbl IN
        SELECT table_name FROM information_schema.tables
        WHERE table_schema = 'workflow' AND table_name LIKE 'transaction\_%' ESCAPE '\'
    LOOP
        idx_name := 'IX_' || tbl.table_name || '_Instance_Status';
        IF NOT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'workflow' AND indexname = idx_name) THEN
            EXECUTE format(
                'CREATE INDEX %I ON workflow.%I ("WorkflowInstanceId", "IsDeleted", "ActionStatus") INCLUDE ("ActivityUserId", "CreatedBy", "StageType", "ActivityGroupId")',
                idx_name, tbl.table_name);
        END IF;
        RAISE NOTICE 'Index ensured: workflow.%', tbl.table_name;
    END LOOP;
END $$;
