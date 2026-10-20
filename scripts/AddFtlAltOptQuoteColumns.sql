-- FTL RFQ (form 787372d8-d66a-47f6-ba3f-36cb6dffb360 -> dbo.ezfb_787372d8_items)
-- Add the ALT / OPT quote-section columns and backfill wFormControl."columnName".
-- Run this ONCE per tenant database (admin tenant first). Idempotent.
-- The API also auto-creates these on the next quote-agent run
-- (FtlAgentPipelineService.EnsureEzfbColumnsForMappedFieldsAsync), so this script
-- is only needed to fix the form before the next quote runs.

DO $$
DECLARE
    col text;
    cols text[] := ARRAY[
        'ALT_Line_Item','ALT_Subtotal','ALT_Discount','ALT_Freight','ALT_HST','ALT_Total','ALT_Remarks',
        'OPT_Line_Item','OPT_Subtotal','OPT_Discount','OPT_Freight','OPT_HST','OPT_Total','OPT_Remarks'
    ];
BEGIN
    FOREACH col IN ARRAY cols LOOP
        EXECUTE format('ALTER TABLE dbo."ezfb_787372d8_items" ADD COLUMN IF NOT EXISTS %I text NULL', col);
        IF EXISTS (SELECT 1 FROM information_schema.tables
                   WHERE table_schema = 'dbo' AND table_name = 'ezfb_787372d8_history') THEN
            EXECUTE format('ALTER TABLE dbo."ezfb_787372d8_history" ADD COLUMN IF NOT EXISTS %I text NULL', col);
        END IF;
    END LOOP;
END $$;

-- Backfill columnName for the top-level ALT/OPT controls of this form (only where missing).
UPDATE dbo."wFormControl" c
SET "columnName" = m.physical
FROM (VALUES
    ('altlineitem',   'ALT_Line_Item'),
    ('altlineitems',  'ALT_Line_Item'),
    ('altsubtotal',   'ALT_Subtotal'),
    ('altdiscount',   'ALT_Discount'),
    ('altfreight',    'ALT_Freight'),
    ('althst',        'ALT_HST'),
    ('alttotal',      'ALT_Total'),
    ('altremarks',    'ALT_Remarks'),
    ('optlineitem',   'OPT_Line_Item'),
    ('optlineitems',  'OPT_Line_Item'),
    ('optsubtotal',   'OPT_Subtotal'),
    ('optdiscount',   'OPT_Discount'),
    ('optfreight',    'OPT_Freight'),
    ('opthst',        'OPT_HST'),
    ('opttotal',      'OPT_Total'),
    ('optremarks',    'OPT_Remarks')
) AS m(normalized, physical)
WHERE c."wFormId" = '787372d8-d66a-47f6-ba3f-36cb6dffb360'
  AND c."parentId" = 0
  AND c."isDeleted" = false
  AND regexp_replace(lower(coalesce(c.name, '')), '[^a-z0-9]', '', 'g') = m.normalized
  AND (c."columnName" IS NULL OR btrim(c."columnName") = '');

-- Verify: expect ALT_* / OPT_* rows with a non-null columnName.
SELECT name, "jsonId", type, "columnName"
FROM dbo."wFormControl"
WHERE "wFormId" = '787372d8-d66a-47f6-ba3f-36cb6dffb360'
  AND "parentId" = 0 AND "isDeleted" = false
  AND (regexp_replace(lower(coalesce(name, '')), '[^a-z0-9]', '', 'g') LIKE 'alt%'
       OR regexp_replace(lower(coalesce(name, '')), '[^a-z0-9]', '', 'g') LIKE 'opt%');
