-- Optional: self-referencing FK for repository.Folders.ParentId -- Postgres
-- Ported from scripts/Alter-Repository-Folders-ParentFk.sql -- Phase 3.
-- Run on tenant database if repository.Folders already exists without FK.

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'repository' AND table_name = 'Folders')
       AND NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_Folders_Parent')
    THEN
        ALTER TABLE repository."Folders"
            ADD CONSTRAINT "FK_Folders_Parent" FOREIGN KEY ("ParentId") REFERENCES repository."Folders" ("Id");
        RAISE NOTICE 'FK_Folders_Parent added.';
    END IF;
END $$;
