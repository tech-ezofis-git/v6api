-- Add FileVersion to all per-repository items tables (repository.Items_xxxxxxxx) -- Postgres
-- Ported from scripts/Alter-Repository-Items-FileVersion.sql -- Phase 3.
-- First upload = 1; same FolderId + FileName = 2, 3, ...

DO $$
DECLARE
    tbl record;
BEGIN
    FOR tbl IN
        SELECT table_name FROM information_schema.tables
        WHERE table_schema = 'repository'
          AND table_name LIKE 'Items\_%' ESCAPE '\'
          AND table_name NOT LIKE '%History'
          AND table_name NOT LIKE '%Stage'
    LOOP
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'repository' AND table_name = tbl.table_name AND column_name = 'FileVersion') THEN
            EXECUTE format('ALTER TABLE repository.%I ADD COLUMN "FileVersion" integer NOT NULL DEFAULT 1', tbl.table_name);
            RAISE NOTICE 'Added FileVersion to repository.%', tbl.table_name;
        END IF;
    END LOOP;
END $$;

-- 'Done.'
