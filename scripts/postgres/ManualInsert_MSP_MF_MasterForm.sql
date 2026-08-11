/*
  Manual insert: MSP_MF master form (legacy v5 -> SaaS GUID format) -- Postgres
  Ported from scripts/ManualInsert_MSP_MF_MasterForm.sql -- Phase 3.

  Generated form GUID : 7f8a9b0c-1d2e-4f3a-9b5c-6d7e8f9a0b1c
  ezfb items table    : dbo.ezfb_7f8a9b0c_items   (first 8 hex chars of GUID, lowercase)

  BEFORE RUNNING -- set these variables for your tenant DB (this is a TEMPLATE, not
  meant to run as-is):
    tenant_guid   -> the tenant's GUID (dbo."wForm"."tenantId" is uuid -- Phase 4's
                     FormService.cs port dropped the SQL Server original's INT
                     tenantId / dbo.TenantIdMap side-table entirely: no legacy-drift
                     tenant-id mapping can exist on a freshly-created Postgres tenant DB)
    created_by    -> admin user GUID as string
    repository_id -> legacy INT repo id if wForm.repositoryId is INT; else NULL

  Run on the TENANT database (not catalog).

  Two genuine dialect rewrites here, not just mechanical syntax:
  - SCOPE_IDENTITY() -> Postgres has no session-global "last identity" function;
    capture it directly off the INSERT via `INSERT ... RETURNING "id" INTO var` inside
    a DO block (Postgres's PL/pgSQL equivalent).
  - The temporal ezfb_7f8a9b0c_items table (PERIOD FOR SYSTEM_TIME / SYSTEM_VERSIONING)
    gets the same Decision 2 treatment as workflow.WorkflowInstances in
    02_CreateTenantDatabase.sql: an AFTER UPDATE/DELETE trigger copying the prior row
    into a hand-maintained history table, since Postgres has no native temporal-table
    feature. Its fixed/system columns (item_id, created_at, ...) are snake_case
    unquoted, matching the fix applied to FormService.cs's EnsureFormEntryTableAsync --
    an earlier draft of that method left them unquoted camelCase, which Postgres folds
    to all-lowercase; functionally equivalent as long as never quoted, but fragile, so
    both the live code and this reference script now use genuine snake_case. Per-field
    columns (the random-looking quoted identifiers) keep their original casing quoted,
    same as repository custom columns.
*/

DO $$
DECLARE
    form_id        text := '7f8a9b0c-1d2e-4f3a-9b5c-6d7e8f9a0b1c';
    tenant_guid    uuid := '00000000-0000-0000-0000-000000000000'; -- change me
    created_by     text := '00000000-0000-0000-0000-000000000001'; -- change me
    repository_id  integer := NULL;       -- legacy was 36; set if you have INT mapping
    now_str        text := to_char(now() AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US');
    po_detail_control_id bigint;
BEGIN
    -- ---------------------------------------------------------------------------
    -- 1) dbo."wForm"
    -- ---------------------------------------------------------------------------
    IF NOT EXISTS (SELECT 1 FROM dbo."wForm" WHERE "id" = form_id) THEN
        INSERT INTO dbo."wForm" (
            "id", "uid", "tenantId", "name", "description", "type", "layout", "publishOption", "error",
            "createdAt", "modifiedAt", "createdBy", "modifiedBy", "isDeleted", "qrFields", "isEdit",
            "repositoryId", "uniqueColumns", "superUser", "entryUser", "activityBy", "activityOn", "activityId"
        )
        VALUES (
            form_id,
            '91XiXcI-vytg6NaSBJQWo',          -- legacy uid (optional traceability)
            tenant_guid,
            'MSP_MF',
            '',
            'MASTER',
            'CLASSIC',
            'PUBLISHED',
            '',
            now_str,
            now_str,
            created_by,
            created_by,
            false,
            '',
            false,
            repository_id,
            'pLIax1zKPXRCdlnCOLHzy',          -- PO Number = unique column
            '',
            '',
            NULL,
            NULL,
            NULL
        );
        RAISE NOTICE 'Inserted wForm: %', form_id;
    ELSE
        RAISE NOTICE 'wForm already exists: %', form_id;
    END IF;

    -- ---------------------------------------------------------------------------
    -- 2) dbo."wFormControl"  (parentId 0 = top-level; TABLE children use po_detail_control_id)
    -- ---------------------------------------------------------------------------
    DELETE FROM dbo."wFormControl" WHERE "wFormId" = form_id;

    INSERT INTO dbo."wFormControl" ("wFormId", "jsonId", "name", "type", "isMandatory", "parentId", "createdAt", "createdBy", "isDeleted", "validationJson") VALUES
        (form_id, 'pLIax1zKPXRCdlnCOLHzy', 'PO Number', 'SHORT_TEXT', false, 0, now_str, created_by, false, '{"specific":{},"validation":{"contentRule":"TEXT","maximum":"","minimum":""}}'),
        (form_id, 'IChfsjDJrJsfQDRVM9EAD', 'Vendor', 'SHORT_TEXT', false, 0, now_str, created_by, false, '{"specific":{},"validation":{"contentRule":"TEXT","maximum":"","minimum":""}}'),
        (form_id, 'szG2cMaKkEtkY-V8dNQQU', 'Vendor Name', 'SHORT_TEXT', false, 0, now_str, created_by, false, '{"specific":{},"validation":{"contentRule":"TEXT","maximum":"","minimum":""}}'),
        (form_id, 'l7GRxwpdjcWv8LSz2BlmV', 'Bill-To Address', 'SHORT_TEXT', false, 0, now_str, created_by, false, '{"specific":{},"validation":{"contentRule":"TEXT","maximum":"","minimum":""}}'),
        (form_id, 'cpyUkkJaPgvNbUQCb_z6e', 'Ship-To Address', 'SHORT_TEXT', false, 0, now_str, created_by, false, '{"specific":{},"validation":{"contentRule":"TEXT","maximum":"","minimum":""}}'),
        (form_id, 'Cghd2DGqBZpvfmqwFmd8z', 'PO Date', 'DATE', false, 0, now_str, created_by, false, NULL),
        (form_id, 'bnWTWmxoqfafNDOrwqYVT', 'Terms Descr', 'SHORT_TEXT', false, 0, now_str, created_by, false, '{"specific":{},"validation":{"contentRule":"TEXT","maximum":"","minimum":""}}'),
        (form_id, 'Adcwz0bdXvk6dvSCeN9xB', 'Buyer Name', 'SHORT_TEXT', false, 0, now_str, created_by, false, '{"specific":{},"validation":{"contentRule":"TEXT","maximum":"","minimum":""}}'),
        (form_id, 'K7jbH86jW3DOVC9ElLn4z', 'Gross Amount', 'SHORT_TEXT', false, 0, now_str, created_by, false, '{"specific":{},"validation":{"contentRule":"TEXT","maximum":"","minimum":""}}'),
        (form_id, 'A6WOvkJ7iVzFiSp6uftqC', 'Notes', 'SHORT_TEXT', false, 0, now_str, created_by, false, '{"specific":{},"validation":{"contentRule":"TEXT","maximum":"","minimum":""}}');

    INSERT INTO dbo."wFormControl" ("wFormId", "jsonId", "name", "type", "isMandatory", "parentId", "createdAt", "createdBy", "isDeleted", "validationJson")
    VALUES (form_id, '8G9Iy7Dtj6ksL1ikYVmEp', 'PODetail', 'TABLE', false, 0, now_str, created_by, false, NULL)
    RETURNING "id" INTO po_detail_control_id;

    INSERT INTO dbo."wFormControl" ("wFormId", "jsonId", "name", "type", "isMandatory", "parentId", "createdAt", "createdBy", "isDeleted", "validationJson") VALUES
        (form_id, 'EWdBSDlm185GKpbReyvAx', 'Line', 'SHORT_TEXT', false, po_detail_control_id, now_str, created_by, false, '{"specific":{},"validation":{"contentRule":"TEXT","maximum":"","minimum":""}}'),
        (form_id, 'hhV29UwNDr85bFxTpw0pK', 'Part Number', 'SHORT_TEXT', false, po_detail_control_id, now_str, created_by, false, '{"specific":{},"validation":{"contentRule":"TEXT","maximum":"","minimum":""}}'),
        (form_id, '65FcXXq2C4JG5lFZWGwu-', 'Rev', 'SHORT_TEXT', false, po_detail_control_id, now_str, created_by, false, '{"specific":{},"validation":{"contentRule":"TEXT","maximum":"","minimum":""}}'),
        (form_id, 'Gx1FM5LN2nct-_fLeLn63', 'Part Description', 'SHORT_TEXT', false, po_detail_control_id, now_str, created_by, false, '{"specific":{},"validation":{"contentRule":"TEXT","maximum":"","minimum":""}}'),
        (form_id, 'X_K-QEAdydBLh08Pf4FZY', 'UOM', 'SHORT_TEXT', false, po_detail_control_id, now_str, created_by, false, '{"specific":{},"validation":{"contentRule":"TEXT","maximum":"","minimum":""}}'),
        (form_id, 'Fk4UvCUeIWcHJxTxGSQi9', 'Tax', 'SHORT_TEXT', false, po_detail_control_id, now_str, created_by, false, '{"specific":{},"validation":{"contentRule":"TEXT","maximum":"","minimum":""}}'),
        (form_id, 'hnzgiQph-MXpYA4bowCiI', 'Quantity', 'SHORT_TEXT', false, po_detail_control_id, now_str, created_by, false, '{"specific":{},"validation":{"contentRule":"TEXT","maximum":"","minimum":""}}'),
        (form_id, 'm0XNzT9VELdAFFKfXZAYa', 'Unit Cost', 'SHORT_TEXT', false, po_detail_control_id, now_str, created_by, false, '{"specific":{},"validation":{"contentRule":"TEXT","maximum":"","minimum":""}}'),
        (form_id, '4hhwce0cB1NA_CJJyF5Y2', 'Gross Amount1', 'SHORT_TEXT', false, po_detail_control_id, now_str, created_by, false, '{"specific":{},"validation":{"contentRule":"TEXT","maximum":"","minimum":""}}'),
        (form_id, 'y-8t3wTX5XUkrbbE3ZHpi', 'Req Date', 'DATE', false, po_detail_control_id, now_str, created_by, false, NULL),
        (form_id, 'rJemAZd-lVI-rRjTqTe05', 'Weight', 'SHORT_TEXT', false, po_detail_control_id, now_str, created_by, false, '{"specific":{},"validation":{"contentRule":"TEXT","maximum":"","minimum":""}}'),
        (form_id, 'W6qlf2mHG_WyKim70kowj', 'G/L Account', 'SHORT_TEXT', false, po_detail_control_id, now_str, created_by, false, '{"specific":{},"validation":{"contentRule":"TEXT","maximum":"","minimum":""}}');

    RAISE NOTICE 'Inserted wFormControl rows for form %', form_id;

    -- ---------------------------------------------------------------------------
    -- 3) dbo.ezfb_7f8a9b0c_items -- temporal table replacement (Decision 2)
    -- ---------------------------------------------------------------------------
    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'dbo' AND table_name = 'ezfb_7f8a9b0c_items') THEN
        CREATE TABLE dbo.ezfb_7f8a9b0c_items (
            item_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            "pLIax1zKPXRCdlnCOLHzy" text NULL,
            "IChfsjDJrJsfQDRVM9EAD" text NULL,
            "szG2cMaKkEtkY-V8dNQQU" text NULL,
            "l7GRxwpdjcWv8LSz2BlmV" text NULL,
            "cpyUkkJaPgvNbUQCb_z6e" text NULL,
            "Cghd2DGqBZpvfmqwFmd8z" text NULL,
            "bnWTWmxoqfafNDOrwqYVT" text NULL,
            "Adcwz0bdXvk6dvSCeN9xB" text NULL,
            "K7jbH86jW3DOVC9ElLn4z" text NULL,
            "A6WOvkJ7iVzFiSp6uftqC" text NULL,
            "8G9Iy7Dtj6ksL1ikYVmEp" text NULL,   -- PODetail JSON array
            created_at varchar(50) NULL,
            modified_at varchar(50) NULL,
            created_by varchar(50) NOT NULL DEFAULT '0',
            modified_by varchar(50) NOT NULL DEFAULT '0',
            is_deleted boolean NOT NULL DEFAULT false,
            today_task boolean NOT NULL DEFAULT true,
            is_marked boolean NOT NULL DEFAULT false
        );
        RAISE NOTICE 'Created dbo.ezfb_7f8a9b0c_items';
    ELSE
        RAISE NOTICE 'Table dbo.ezfb_7f8a9b0c_items already exists -- ensuring missing columns';
    END IF;

    -- Ensure all 11 top-level field columns + metadata exist (safe to re-run)
    ALTER TABLE dbo.ezfb_7f8a9b0c_items ADD COLUMN IF NOT EXISTS "pLIax1zKPXRCdlnCOLHzy" text NULL;
    ALTER TABLE dbo.ezfb_7f8a9b0c_items ADD COLUMN IF NOT EXISTS "IChfsjDJrJsfQDRVM9EAD" text NULL;
    ALTER TABLE dbo.ezfb_7f8a9b0c_items ADD COLUMN IF NOT EXISTS "szG2cMaKkEtkY-V8dNQQU" text NULL;
    ALTER TABLE dbo.ezfb_7f8a9b0c_items ADD COLUMN IF NOT EXISTS "l7GRxwpdjcWv8LSz2BlmV" text NULL;
    ALTER TABLE dbo.ezfb_7f8a9b0c_items ADD COLUMN IF NOT EXISTS "cpyUkkJaPgvNbUQCb_z6e" text NULL;
    ALTER TABLE dbo.ezfb_7f8a9b0c_items ADD COLUMN IF NOT EXISTS "Cghd2DGqBZpvfmqwFmd8z" text NULL;
    ALTER TABLE dbo.ezfb_7f8a9b0c_items ADD COLUMN IF NOT EXISTS "bnWTWmxoqfafNDOrwqYVT" text NULL;
    ALTER TABLE dbo.ezfb_7f8a9b0c_items ADD COLUMN IF NOT EXISTS "Adcwz0bdXvk6dvSCeN9xB" text NULL;
    ALTER TABLE dbo.ezfb_7f8a9b0c_items ADD COLUMN IF NOT EXISTS "K7jbH86jW3DOVC9ElLn4z" text NULL;
    ALTER TABLE dbo.ezfb_7f8a9b0c_items ADD COLUMN IF NOT EXISTS "A6WOvkJ7iVzFiSp6uftqC" text NULL;
    ALTER TABLE dbo.ezfb_7f8a9b0c_items ADD COLUMN IF NOT EXISTS "8G9Iy7Dtj6ksL1ikYVmEp" text NULL;
    ALTER TABLE dbo.ezfb_7f8a9b0c_items ADD COLUMN IF NOT EXISTS created_at varchar(50) NULL;
    ALTER TABLE dbo.ezfb_7f8a9b0c_items ADD COLUMN IF NOT EXISTS modified_at varchar(50) NULL;
    ALTER TABLE dbo.ezfb_7f8a9b0c_items ADD COLUMN IF NOT EXISTS created_by varchar(50) NOT NULL DEFAULT '0';
    ALTER TABLE dbo.ezfb_7f8a9b0c_items ADD COLUMN IF NOT EXISTS modified_by varchar(50) NOT NULL DEFAULT '0';
    ALTER TABLE dbo.ezfb_7f8a9b0c_items ADD COLUMN IF NOT EXISTS is_deleted boolean NOT NULL DEFAULT false;
    ALTER TABLE dbo.ezfb_7f8a9b0c_items ADD COLUMN IF NOT EXISTS today_task boolean NOT NULL DEFAULT true;
    ALTER TABLE dbo.ezfb_7f8a9b0c_items ADD COLUMN IF NOT EXISTS is_marked boolean NOT NULL DEFAULT false;

    RAISE NOTICE 'ezfb column check complete (expect 11 field cols + metadata, not 23).';
    RAISE NOTICE 'Next step: run Insert_MSP_MF_ezfb_items.sql on this database.';
END $$;

-- Temporal history table + trigger (Decision 2) for dbo.ezfb_7f8a9b0c_items -- same
-- pattern as workflow.WorkflowInstances in 02_CreateTenantDatabase.sql, done outside
-- the DO block above since it needs its own CREATE TABLE / CREATE FUNCTION / CREATE
-- TRIGGER statements (DDL that references the just-created ezfb table by name).
CREATE TABLE IF NOT EXISTS dbo.ezfb_7f8a9b0c_history (
    LIKE dbo.ezfb_7f8a9b0c_items INCLUDING DEFAULTS,
    "history_id" bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "history_operation" varchar(1) NOT NULL,
    "history_recorded_at" timestamptz NOT NULL DEFAULT now()
);

CREATE OR REPLACE FUNCTION dbo.fn_ezfb_7f8a9b0c_history() RETURNS trigger AS $$
BEGIN
    IF (TG_OP = 'UPDATE') THEN
        INSERT INTO dbo.ezfb_7f8a9b0c_history
        SELECT OLD.item_id, OLD."pLIax1zKPXRCdlnCOLHzy", OLD."IChfsjDJrJsfQDRVM9EAD", OLD."szG2cMaKkEtkY-V8dNQQU",
               OLD."l7GRxwpdjcWv8LSz2BlmV", OLD."cpyUkkJaPgvNbUQCb_z6e", OLD."Cghd2DGqBZpvfmqwFmd8z",
               OLD."bnWTWmxoqfafNDOrwqYVT", OLD."Adcwz0bdXvk6dvSCeN9xB", OLD."K7jbH86jW3DOVC9ElLn4z",
               OLD."A6WOvkJ7iVzFiSp6uftqC", OLD."8G9Iy7Dtj6ksL1ikYVmEp", OLD.created_at, OLD.modified_at,
               OLD.created_by, OLD.modified_by, OLD.is_deleted, OLD.today_task, OLD.is_marked, 'U', now();
        RETURN NEW;
    ELSIF (TG_OP = 'DELETE') THEN
        INSERT INTO dbo.ezfb_7f8a9b0c_history
        SELECT OLD.item_id, OLD."pLIax1zKPXRCdlnCOLHzy", OLD."IChfsjDJrJsfQDRVM9EAD", OLD."szG2cMaKkEtkY-V8dNQQU",
               OLD."l7GRxwpdjcWv8LSz2BlmV", OLD."cpyUkkJaPgvNbUQCb_z6e", OLD."Cghd2DGqBZpvfmqwFmd8z",
               OLD."bnWTWmxoqfafNDOrwqYVT", OLD."Adcwz0bdXvk6dvSCeN9xB", OLD."K7jbH86jW3DOVC9ElLn4z",
               OLD."A6WOvkJ7iVzFiSp6uftqC", OLD."8G9Iy7Dtj6ksL1ikYVmEp", OLD.created_at, OLD.modified_at,
               OLD.created_by, OLD.modified_by, OLD.is_deleted, OLD.today_task, OLD.is_marked, 'D', now();
        RETURN OLD;
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_ezfb_7f8a9b0c_history ON dbo.ezfb_7f8a9b0c_items;
CREATE TRIGGER trg_ezfb_7f8a9b0c_history
    AFTER UPDATE OR DELETE ON dbo.ezfb_7f8a9b0c_items
    FOR EACH ROW EXECUTE FUNCTION dbo.fn_ezfb_7f8a9b0c_history();

-- ========== REFERENCE ==========
SELECT "id" AS "FormGuid", "name", "type", "publishOption", "uniqueColumns" FROM dbo."wForm" WHERE "id" = '7f8a9b0c-1d2e-4f3a-9b5c-6d7e8f9a0b1c';
SELECT count(*) AS "wFormControlCount" FROM dbo."wFormControl" WHERE "wFormId" = '7f8a9b0c-1d2e-4f3a-9b5c-6d7e8f9a0b1c';
SELECT column_name FROM information_schema.columns
WHERE table_schema = 'dbo' AND table_name = 'ezfb_7f8a9b0c_items'
ORDER BY ordinal_position;
SELECT item_id, "pLIax1zKPXRCdlnCOLHzy" AS "PONumber" FROM dbo.ezfb_7f8a9b0c_items LIMIT 5;

-- 'Use this FormId in workflows / API: 7f8a9b0c-1d2e-4f3a-9b5c-6d7e8f9a0b1c'
-- 'ezfb table name: ezfb_7f8a9b0c_items'
