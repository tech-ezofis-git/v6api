-- =============================================
-- Phase 1: Merge sample PO masters into tenant SAP connector ConfigJson
-- Run against the TENANT database (ezofis_Tenant_{first8} or catalog ConnectionString DB).
--
-- Safe merge: keeps existing ConfigJson keys; sets/overwrites only:
--   provider, mode, samplePurchaseOrders, sampleVendors
--
-- Does NOT invent a connector row if none exists — lists SAP connectors first.
-- Confirm connector_id, then optionally set v_connector_id below to target one row.
-- =============================================

-- 1) Discover SAP connectors (copy "Id" for AP tests / Phase 0 payload)
SELECT
    c."Id"              AS connector_id,
    c."Name",
    c."ProviderCode",
    c."OAuthStatus",
    c."IsDefault",
    left(coalesce(c."ConfigJson", ''), 200) AS config_preview
FROM dbo."connector" c
WHERE c."IsDeleted" = false
  AND (
        upper(c."ProviderCode") = 'SAP'
     OR upper(c."ProviderCode") LIKE 'SAP\_%' ESCAPE '\'
  )
ORDER BY c."IsDefault" DESC, c."CreatedAtUtc" ASC;

-- 2) Merge samplePurchaseOrders (all active SAP connectors, or pin one Id)
DO $$
DECLARE
    -- Optional: set to a specific GUID to update only that connector.
    -- NULL = update every non-deleted ProviderCode=SAP row.
    v_connector_id uuid := NULL; -- e.g. 'xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx'::uuid

    v_samples jsonb := $json$
[
  {
    "po_number": "PO-60001",
    "vendor": "ACME Supplies",
    "total": 1500.00,
    "currency": "USD",
    "lines": [
      { "description": "Widget A", "qty": 10, "unit_price": 100, "amount": 1000 },
      { "description": "Widget B", "qty": 5, "unit_price": 100, "amount": 500 }
    ]
  },
  {
    "po_number": "PO-SAP-1001",
    "vendor": "Contoso Trading",
    "total": 2500.00,
    "currency": "USD",
    "lines": [
      { "description": "Service retainer", "qty": 1, "unit_price": 2500, "amount": 2500 }
    ]
  },
  {
    "po_number": "PO-SAP-1002",
    "vendor": "Fabrikam Ltd",
    "total": 875.50,
    "currency": "USD",
    "lines": [
      { "description": "Parts kit", "qty": 1, "unit_price": 875.50, "amount": 875.50 }
    ]
  }
]
$json$::jsonb;

    v_vendors jsonb := $json$
[
  { "id": "V-ACME", "displayName": "ACME Supplies", "email": "ap@acme.example" },
  { "id": "V-CONTOSO", "displayName": "Contoso Trading", "email": null },
  { "id": "V-FABRIKAM", "displayName": "Fabrikam Ltd", "email": null }
]
$json$::jsonb;

    v_patch jsonb;
    v_updated int := 0;
BEGIN
    v_patch := jsonb_build_object(
        'provider', 'SAP',
        'mode', 'sample',
        'samplePurchaseOrders', v_samples,
        'sampleVendors', v_vendors
    );

    UPDATE dbo."connector" c
    SET
        "ConfigJson" = (
            COALESCE(
                CASE
                    WHEN nullif(btrim(c."ConfigJson"), '') IS NULL THEN '{}'::jsonb
                    ELSE c."ConfigJson"::jsonb
                END,
                '{}'::jsonb
            ) || v_patch
        )::text,
        "ModifiedAtUtc" = now()
    WHERE c."IsDeleted" = false
      AND (
            upper(c."ProviderCode") = 'SAP'
         OR upper(c."ProviderCode") LIKE 'SAP\_%' ESCAPE '\'
      )
      AND (v_connector_id IS NULL OR c."Id" = v_connector_id);

    GET DIAGNOSTICS v_updated = ROW_COUNT;
    RAISE NOTICE 'SAP connector ConfigJson merge updated % row(s)', v_updated;

    IF v_updated = 0 THEN
        RAISE EXCEPTION 'No SAP connector row found. Create dbo.connector with ProviderCode=SAP first, then re-run.';
    END IF;
END $$;

-- 3) Verify
SELECT
    c."Id" AS connector_id,
    c."Name",
    c."ConfigJson"::jsonb ->> 'mode' AS mode,
    c."ConfigJson"::jsonb ->> 'provider' AS provider,
    jsonb_array_length(c."ConfigJson"::jsonb -> 'samplePurchaseOrders') AS sample_po_count,
    (
        SELECT string_agg(x.po, ', ' ORDER BY x.po)
        FROM jsonb_array_elements(c."ConfigJson"::jsonb -> 'samplePurchaseOrders') e
        CROSS JOIN LATERAL (SELECT e ->> 'po_number' AS po) x
    ) AS sample_po_numbers
FROM dbo."connector" c
WHERE c."IsDeleted" = false
  AND upper(c."ProviderCode") = 'SAP';
