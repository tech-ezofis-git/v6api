-- Adds mailbox columns for already-created workflow.Inbox_*, Sent_*, Completed_* tables -- Postgres
-- Ported from scripts/Alter-LegacyMailbox-AddRepositoryFormColumns.sql -- Phase 3.
-- Run on each tenant database. Within-one-database dynamic loop, no cross-database concern.

DO $$
DECLARE
    tbl record;
BEGIN
    FOR tbl IN
        SELECT table_name FROM information_schema.tables
        WHERE table_schema = 'workflow'
          AND (table_name LIKE 'Inbox\_%' ESCAPE '\' OR table_name LIKE 'Sent\_%' ESCAPE '\' OR table_name LIKE 'Completed\_%' ESCAPE '\')
    LOOP
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'workflow' AND table_name = tbl.table_name AND column_name = 'repositoryId') THEN
            EXECUTE format('ALTER TABLE workflow.%I ADD COLUMN "repositoryId" varchar(255) NULL', tbl.table_name);
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'workflow' AND table_name = tbl.table_name AND column_name = 'itemId') THEN
            EXECUTE format('ALTER TABLE workflow.%I ADD COLUMN "itemId" varchar(255) NULL', tbl.table_name);
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'workflow' AND table_name = tbl.table_name AND column_name = 'formId') THEN
            EXECUTE format('ALTER TABLE workflow.%I ADD COLUMN "formId" varchar(255) NULL', tbl.table_name);
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'workflow' AND table_name = tbl.table_name AND column_name = 'formEntryId') THEN
            EXECUTE format('ALTER TABLE workflow.%I ADD COLUMN "formEntryId" varchar(255) NULL', tbl.table_name);
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'workflow' AND table_name = tbl.table_name AND column_name = 'formData') THEN
            EXECUTE format('ALTER TABLE workflow.%I ADD COLUMN "formData" text NULL', tbl.table_name);
        END IF;
        RAISE NOTICE 'Updated: workflow.%', tbl.table_name;
    END LOOP;
END $$;

-- Verify columns on mailbox tables
SELECT table_schema AS "SchemaName", table_name AS "TableName", column_name AS "ColumnName", data_type AS "DataType"
FROM information_schema.columns
WHERE table_schema = 'workflow'
  AND (table_name LIKE 'Inbox\_%' ESCAPE '\' OR table_name LIKE 'Sent\_%' ESCAPE '\' OR table_name LIKE 'Completed\_%' ESCAPE '\')
  AND column_name IN ('repositoryId', 'itemId', 'formId', 'formEntryId', 'formData')
ORDER BY table_name, column_name;
