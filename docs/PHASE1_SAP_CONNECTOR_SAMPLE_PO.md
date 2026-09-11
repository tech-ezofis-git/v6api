# Phase 1 — SAP connector + sample PO master

## Goal

Make `ProviderCode=SAP` a catalog provider and seed **sample PO masters** on the tenant `dbo.connector` row so AP can validate against them in Phase 2/3.

## Live write result (2026-09-11)

Applied via `PUT https://cloud.ezofis.com/api/connector/{id}` (direct Azure Postgres host from local `.env` was unreachable).

| Field | Value |
|-------|--------|
| Tenant | EZOFIS `b843b988-00ec-44e3-aca2-b8470133ef63` |
| Connector name | SAP BTP XSUAA |
| **connector_id** | **`983bddbe-6a1a-4cd8-a024-9b4d84ba9981`** |
| **ProviderCode** | **`SAP_XSUAA`** (not plain `SAP`) |
| mode | `sample` |
| Sample POs | `PO-60001`, `PO-SAP-1001`, `PO-SAP-1002` |

**Contract note for Phase 2/3:** treat `resource=SAP` as matching connector `ProviderCode` in (`SAP`, `SAP_XSUAA`, or `SAP_*`). Do not require catalog code to equal the live ProviderCode string.

Verify:

```http
GET /api/connector/all
Authorization: Bearer <token>
X-Tenant-Id: b843b988-00ec-44e3-aca2-b8470133ef63
```

Find id `983bddbe-6a1a-4cd8-a024-9b4d84ba9981` → `configJson.mode=sample` and three `samplePurchaseOrders`.

## Catalog (ProviderCode SAP accepted)


`dbo.connector` CRUD already accepts any `ProviderCode` string (no hard deny-list).  
Catalog listing/seed now includes **SAP** in:

- `scripts/postgres/CreateCatalogConnectorProviders.sql`
- `scripts/postgres/01a_CreateCatalogDatabase.sql`
- `scripts/CreateCatalogConnectorProviders.sql` (SQL Server)
- `src/BuildingBlocks/Catalog/ConnectorProviderCatalog.cs` (runtime seed safety net)

SAP catalog row uses empty Auth/Token/Scopes for now (`ConfigJson.mode=sample` does not need OAuth).

## Sample PO numbers (AP tests)

| po_number | vendor | total |
|-----------|--------|------:|
| `PO-60001` | APEX INDUSTRIAL COMPONENTS LTD | 5203.65 CAD |
| `PO-SAP-1001` | Contoso Trading | 2500.00 |
| `PO-SAP-1002` | Fabrikam Ltd | 875.50 |

## ConfigJson shape (merged keys only)

Merge **overwrites/sets** these keys only; all other existing keys are preserved:

```json
{
  "provider": "SAP",
  "mode": "sample",
  "samplePurchaseOrders": [ /* three POs above with lines */ ]
}
```

## Tenant seed script (you run — no live write from agent yet)

File: [`scripts/postgres/SeedSapConnectorSamplePurchaseOrders.sql`](../scripts/postgres/SeedSapConnectorSamplePurchaseOrders.sql)

1. Connect to the **tenant** DB (not catalog), e.g. `ezofis_Tenant_<first8>` or the tenant ConnectionString database.
2. Run the script’s **SELECT** to list SAP connectors → copy `connector_id`.
3. Optional: set `v_connector_id` in the `DO` block to pin one row.
4. Run the merge + verify SELECT.

Expected verify: `mode=sample`, `sample_po_count=3`, `sample_po_numbers` includes `PO-60001, PO-SAP-1001, PO-SAP-1002`.

## How to verify via API (after seed)

```http
GET /api/connector
```

Find the row with `providerCode: "SAP"` and inspect `configJson` for `samplePurchaseOrders`.

## connector_id for AP Phase 0/3 payloads

| Field | Value |
|-------|--------|
| `tenant_id` | `b843b988-00ec-44e3-aca2-b8470133ef63` |
| `connector_id` | `983bddbe-6a1a-4cd8-a024-9b4d84ba9981` |
| `resource` | `SAP` |
| Live `ProviderCode` | `SAP_XSUAA` |
| Sample POs | `PO-60001`, `PO-SAP-1001`, `PO-SAP-1002` |

## Out of scope (later phases)

- Phase 2: `POST /api/connector/{id}/sap/purchase-orders/lookup`
- Phase 3: Orchestrator `po_lookup_sap` skill
- Phase 4: Hangfire start payload enrichment when `masterSource=SAP` — see [PHASE4_SAP_PO_MASTER_START_PAYLOAD.md](./PHASE4_SAP_PO_MASTER_START_PAYLOAD.md)
- Phase 5: Orchestrator harden + smoke — see orchestrator `deploy/PHASE5_SAP_PO_SMOKE.md` and `docs/chat-ap-ocr.md` (SAP section)
- Live SAP API / OAuth secrets

## Live DB note

This Phase 1 deliverable **does not write production/tenant data automatically**.  
Run `SeedSapConnectorSamplePurchaseOrders.sql` yourself (or provide tenant DB credentials / approve a live run).
