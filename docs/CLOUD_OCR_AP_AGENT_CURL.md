# OCR and AP agents — how to call `/chat`

This guide covers:

1. **V6 .NET on Azure** — Hangfire calls the internal agents service  
2. **Manual testing** — `curl` / console against the public live endpoint  

Both use the **same** `/chat` JSON contract (`session_id` + `intent` + `payload` / multipart).

---

## Which URL to use

| Who | URL | Notes |
|-----|-----|--------|
| **V6 API / Hangfire (Azure)** | `http://agents:8000/chat` | Set `ApAgent:PythonServiceUrl` (or env `ApAgent__PythonServiceUrl`) |
| **Developers / testers (curl)** | `https://cloud.ezofis.com/chat` | Public live endpoint — same `/chat` API shape |
| **Health** | `https://cloud.ezofis.com/health` | Public |
| **Interactive UI** | `https://cloud.ezofis.com/console` | Browser |

**Do not** use the old URL anymore:

```text
http://localhost:8001/api/ap-agent/run   ← legacy (startPayload wrapper)
```

---

## V6 .NET configuration (Azure)

In App Service / container settings:

```text
ApAgent__Enabled=true
ApAgent__PythonServiceUrl=http://agents:8000/chat
ApAgent__ApiBaseUrl=https://<your-v6-api-host>/V6API/api/workflows
ApAgent__TimeoutMinutes=30
```

Or in `appsettings`:

```json
"ApAgent": {
  "Enabled": true,
  "PythonServiceUrl": "http://agents:8000/chat",
  "ApiBaseUrl": "https://<your-v6-api-host>/V6API/api/workflows",
  "TimeoutMinutes": 30
}
```

When a workflow starts with AP Agent (or `POST .../ap-agent/run`), Hangfire posts JSON like:

```json
{
  "session_id": "<hangfireJobId>",
  "intent": "ap",
  "payload": {
    "tenant_id": "2e3b7b37-38a3-4f94-878e-a006dad93230",
    "formid": "29171de4-e210-466e-9e90-40fa9fa4354d",
    "item_id": "<repositoryItemId>",
    "filepath": "repository/<repoId>/<itemId>.pdf",
    "pageno": "1",
    "workflowId": "967f9423-ac93-4c70-93cb-df500f0d4cc9",
    "instanceId": "a96efa0d-28f1-4b48-afc2-c9791a346ce9",
    "repositoryId": "ef178e9c-e44b-4a88-b827-05268b54264e",
    "repositoryItemId": "<repositoryItemId>",
    "transactionId": "<transaction-guid>",
    "formentryId": "42",
    "apAgentJobId": "<hangfireJobId>",
    "apAgentJobStatusUrl": "...",
    "apAgentProgressUrl": "..."
  }
}
```

- `intent` is always `ap` from V6 Hangfire.
- **`skills`**: optional. When omitted/null → **not sent** on `/chat` → agents run the **full** tenant default plan.
- **Workflow start** (file upload) always enqueues with `skills: null` unless you later call `ap-agent/run` with a `skills` array.
- Configure optional defaults only if you pass them yourself on `POST .../ap-agent/run` (not auto-applied on start).
- `filepath` comes from the uploaded repository document (`blobPath` on start).
- `formid`, `workflowId`, `instanceId`, `repositoryId`, `transactionId` (GUID), `formentryId` are filled from start-workflow bootstrap.
- `processId` is **not** sent.

### Skills on `/chat` payload (optional)

```json
"skills": [
  "extract_invoice",
  "po_match",
  "duplicate_detect",
  "backorder_detect",
  "finalize_decision",
  "workflow_move_next"
]
```

| Input | Behavior |
|-------|----------|
| Workflow **start** (no skills) | `skills` **null** / omitted → **full plan** |
| No `skills` on `ap-agent/run` | Same — omitted → **full plan** |
| `"skills": []` | Omitted → **full plan** |
| `"skills": ["po_match", ...]` | Pass only those skills to agents |

`POST .../ap-agent/run` body example:

```json
{
  "skills": [
    "extract_invoice",
    "po_match",
    "duplicate_detect",
    "backorder_detect",
    "finalize_decision",
    "workflow_move_next"
  ],
  "tenantId": "...",
  "blobPath": "repository/.../file.pdf",
  "workflowId": "...",
  "instanceId": "..."
}
```

Or put `skills` under `payload` / `startPayload`.

Azure / appsettings:

```text
ApAgent__DefaultSkills__0=extract_invoice
ApAgent__DefaultSkills__1=po_match
ApAgent__DefaultSkills__2=duplicate_detect
ApAgent__DefaultSkills__3=backorder_detect
ApAgent__DefaultSkills__4=finalize_decision
ApAgent__DefaultSkills__5=workflow_move_next
```


---

## Manual curl (Windows)

Use **`curl.exe`** (not the PowerShell `curl` alias).  
Save JSON to a file and pass `--data-binary "@file.json"` so quotes survive.

```bash
# health
curl.exe -sS "https://cloud.ezofis.com/health"
```

Every `/chat` call needs a `session_id`. Set `intent` to `ocr` or `ap` (do not rely on keyword routing for document jobs).

---

## OCR agent

Needs a file upload or a blob filepath. `message` is optional.

### JSON — blob path

```bash
curl.exe -sS -X POST "https://cloud.ezofis.com/chat" ^
  -H "Content-Type: application/json" ^
  --data-binary "@ocr-blob.json"
```

`ocr-blob.json`:

```json
{
  "session_id": "ocr-demo-1",
  "intent": "ocr",
  "instruction": "Region: India. Normalize DATE fields to YYYY-MM-DD.",
  "payload": {
    "filepath": "repository/ac40db26306b4d138aebf80a056d9a73/b4df8469e49743379c40609a5690053a.pdf",
    "pageno": "1",
    "parameters": ["Invoice No,SHORT_TEXT", "Due Date,DATE"],
    "tableparameters": []
  }
}
```

- `pageno`: `"1"` (one page) or `"-1"` (up to max pages).
- Blob paths need `AZURE_STORAGE_CONNECTION_STRING` on the agents app.

### Multipart — local file (wins over filepath)

```bash
curl.exe -sS -X POST "https://cloud.ezofis.com/chat" ^
  -F "session_id=ocr-demo-2" ^
  -F "intent=ocr" ^
  -F "pageno=1" ^
  -F "instruction=Region: India. Normalize DATE fields to YYYY-MM-DD." ^
  -F "parameters=Invoice No,SHORT_TEXT" ^
  -F "parameters=Due Date,DATE" ^
  -F "file=@invoice.pdf"
```

**Success:** HTTP 200 with `ocr_result` (extracted fields).  
**Failures:** `400` missing file/filepath, `502` extract engine error.

---

## AP agent

Set `intent` to `ap` plus one of: `invoice_json`, blob `filepath`, uploaded `file`, or `item_id` (re-run from stored artifacts).

`tenant_id` should be the full tenant UUID. The agents app uses App Settings `DATABASE_URL` and opens database `ezofis_Tenant_{first 8}`  
(example: `2e3b7b37-38a3-4f94-878e-a006dad93230` → `ezofis_Tenant_2e3b7b37`).

If `skills` is omitted, the tenant default plan runs (Phase 1):

`extract_invoice` → `po_match` → `duplicate_detect` → `vendor_validate` → `backorder_detect` → `finalize_decision`

Each skill charges 1 credit (mocked if `EZOFIS_LOGIN_EMAIL` / `PASSWORD` are empty).

### JSON — pre-extracted invoice (skips OCR)

```bash
curl.exe -sS -X POST "https://cloud.ezofis.com/chat" ^
  -H "Content-Type: application/json" ^
  --data-binary "@ap-invoice.json"
```

`ap-invoice.json`:

```json
{
  "session_id": "ap-demo-1",
  "intent": "ap",
  "payload": {
    "tenant_id": "2e3b7b37-38a3-4f94-878e-a006dad93230",
    "item_id": "inv-100",
    "invoice_json": {
      "invoice_number": "INV-100",
      "vendor": "ACME Supplies",
      "po_number": "PO-1",
      "total": 1234.56,
      "currency": "USD",
      "line_items": [{"description": "Widget", "qty": 10, "amount": 1234.56}]
    }
  }
}
```

### JSON — blob + optional skill subset

Use `item_id` so later re-runs can reuse artifacts.

```json
{
  "session_id": "ap-demo-2",
  "intent": "ap",
  "payload": {
    "tenant_id": "2e3b7b37-38a3-4f94-878e-a006dad93230",
    "item_id": "doc-b4df8469",
    "filepath": "repository/ac40db26306b4d138aebf80a056d9a73/b4df8469e49743379c40609a5690053a.pdf",
    "pageno": "1",
    "skills": ["extract_invoice", "po_match", "finalize_decision"]
  }
}
```

```bash
curl.exe -sS -X POST "https://cloud.ezofis.com/chat" ^
  -H "Content-Type: application/json" ^
  --data-binary "@ap-blob.json"
```

### JSON — re-run one skill from stored artifacts

```json
{
  "session_id": "ap-demo-3",
  "intent": "ap",
  "payload": {
    "tenant_id": "2e3b7b37-38a3-4f94-878e-a006dad93230",
    "item_id": "inv-100",
    "skills": ["vendor_validate"]
  }
}
```

### Multipart — local invoice file

```bash
curl.exe -sS -X POST "https://cloud.ezofis.com/chat" ^
  -F "session_id=ap-demo-4" ^
  -F "intent=ap" ^
  -F "tenant_id=2e3b7b37-38a3-4f94-878e-a006dad93230" ^
  -F "item_id=upload-inv-100" ^
  -F "pageno=1" ^
  -F "file=@invoice.pdf"
```

### Phase 2 (opt-in — not in the default plan)

Enable on the tenant plan or pass them in `skills`. Extra payload fields as needed:

| Skills | Extra fields |
|--------|----------------|
| `po_lookup_quickbooks`, `po_lookup_sage` | `connector_id`, `resource` (`QUICKBOOKS` or `SAGE`) |
| `gl_match`, `grn_match`, `matter_validate` | `matter_master_id` for matter |
| `workflow_progress`, `workflow_move_next` | `workflow_id`, `instance_id` |

```json
{
  "session_id": "ap-demo-5",
  "intent": "ap",
  "payload": {
    "tenant_id": "2e3b7b37-38a3-4f94-878e-a006dad93230",
    "item_id": "inv-100",
    "invoice_json": {
      "invoice_number": "INV-100",
      "vendor": "ACME Supplies",
      "po_number": "PO-1",
      "total": 1234.56,
      "currency": "USD",
      "line_items": [{"description": "Widget", "qty": 10, "amount": 1234.56}]
    },
    "skills": ["extract_invoice", "po_match", "finalize_decision", "workflow_progress"],
    "workflow_id": "967f9423-ac93-4c70-93cb-df500f0d4cc9",
    "instance_id": "a96efa0d-28f1-4b48-afc2-c9791a346ce9"
  }
}
```

**Success:** HTTP 200 with `ap_result` (`run_id`, `skills_run`, `credits_charged`, `decision`, `artifacts`).

| Status | Meaning |
|--------|---------|
| 400 | Missing file / filepath / `invoice_json` / `item_id`, or skill not enabled |
| 503 | AP store unavailable (wrong DB or tables missing on `ezofis_Tenant_…`) |

---

## Bash (Git Bash / macOS / Linux)

Replace `^` line continuations with `\`:

```bash
curl -sS -X POST "https://cloud.ezofis.com/chat" \
  -H "Content-Type: application/json" \
  --data-binary @ap-invoice.json
```

---

## Quick reference

| Action | Command / setting |
|--------|-------------------|
| Health | `curl.exe -sS "https://cloud.ezofis.com/health"` |
| Console UI | https://cloud.ezofis.com/console |
| Manual OCR/AP | `POST https://cloud.ezofis.com/chat` |
| V6 Hangfire AP | `POST http://agents:8000/chat` via `ApAgent__PythonServiceUrl` |
