-- Add TransactionGuid to all workflow.transaction_* tables (idempotent) -- Postgres
-- Ported from scripts/AddTransactionGuidColumn.sql -- Phase 3.
-- Unlike the ...AllTenants.sql scripts, this loop is WITHIN one database (find every
-- dynamically-created workflow.transaction_{suffix} table for this tenant), so it
-- translates directly -- no cross-database USE involved, just a Postgres catalog query
-- + dynamic ALTER per matching table.

DO $$
DECLARE
    tbl record;
BEGIN
    FOR tbl IN
        SELECT table_name FROM information_schema.tables
        WHERE table_schema = 'workflow' AND table_name LIKE 'transaction\_%' ESCAPE '\'
    LOOP
        IF NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema = 'workflow' AND table_name = tbl.table_name AND column_name = 'TransactionGuid')
        THEN
            EXECUTE format('ALTER TABLE workflow.%I ADD COLUMN "TransactionGuid" uuid NULL', tbl.table_name);
        END IF;
    END LOOP;
END $$;

-- 'transaction_* TransactionGuid column ensured.'
