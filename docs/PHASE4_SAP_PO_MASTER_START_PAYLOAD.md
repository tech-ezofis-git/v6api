# Phase 4 — PO Master = SAP → Hangfire start payload

When a workflow’s email-ingest **PO Master** is SAP, workflow start automatically injects SAP PO validation into the Python AP Agent `startPayload`.

## Configure PO Master = SAP (tenant workflow)

**Prereqs**

1. SAP connector exists in `dbo.connector` (e.g. Phase 1 sample: `983bddbe-6a1a-4cd8-a024-9b4d84ba9981`, `ProviderCode` `SAP` / `SAP_XSUAA`).
2. AP workflow has a dedicated **AP AGENT** step (Hangfire Python job).
3. EMAIL initiate: Gmail/Outlook connector linked as the mailbox.

**Preferred — create/update workflow**

```http
PUT /api/workflows/{workflowId}
Authorization: Bearer <JWT>
X-Tenant-Id: <tenant-guid>
```

```json
{
  "emailConnectorId": "<gmail-or-outlook-connector-guid>",
  "emailIsEnabled": true,
  "masterSource": "SAP",
  "masterConnectorId": "983bddbe-6a1a-4cd8-a024-9b4d84ba9981"
}
```

**Or mailbox API**

```http
POST /api/email-ingest/mailboxes
```

```json
{
  "connectorId": "<gmail-or-outlook-connector-guid>",
  "workflowId": "<ap-workflow-guid>",
  "isEnabled": true,
  "masterSource": "SAP",
  "masterConnectorId": "983bddbe-6a1a-4cd8-a024-9b4d84ba9981"
}
```

`masterConnectorId` must be an SAP-family connector (`SAP`, `SAP_XSUAA`, or `SAP_*`).

## What startPayload gets

| Field | Value |
|--------|--------|
| `resource` | `"SAP"` |
| `connector_id` | SAP connector GUID |
| `skills` | default AP skills with `po_lookup_sap` **before** `po_match` |

Example skills slice:

`extract_invoice` → `po_lookup_sap` → `po_match` → `duplicate_detect` → … → `workflow_move_next`

**InternalForm** — no `resource` / `connector_id` / `skills` injection (Python default pipeline + form PO master).  
**QuickBooks** — same pattern with `resource=QUICKBOOKS` and `po_lookup_quickbooks`.

Existing payload `resource` / `connector_id` are not overwritten; if `skills` is already set, only `po_lookup_sap` is inserted before `po_match` when missing.

## Non-email / DOCUMENT_FORM workflows

PO Master can live on designer JSON (no mailbox required):

```json
{
  "Settings": {
    "PoMaster": {
      "masterSource": "SAP",
      "masterConnectorId": "983bddbe-6a1a-4cd8-a024-9b4d84ba9981"
    }
  }
}
```

Or pass the same fields on create/update body (`masterSource` / `masterConnectorId`) — Core writes `Settings.PoMaster` even when initiate type is not EMAIL.

## Live SAP (beyond sample)

Sample ConfigJson is the default. For a customer gateway / iPaaS:

```json
{
  "live": {
    "purchaseOrderLookupUrl": "https://your-gateway.example/sap/po/{poNumber}"
  }
}
```

Native SAP OData/BAPI is not built into Core; use the gateway URL or keep `mode=sample`.
