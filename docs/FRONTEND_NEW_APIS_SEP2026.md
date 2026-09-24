# Frontend API guide — new functions (Sep 2026)

**Audience:** Frontend  
**Branch:** `feature/bulk-upload-job-file-status`  
**Auth (all calls):**

| Header | Required |
|--------|----------|
| `Authorization: Bearer <jwt>` | Yes |
| `X-Tenant-Id: <tenant-guid>` | Yes (tenant context) |

Workspace (`GET …/items/{itemId}/workspace`) is **unchanged**. Ticket data is a **separate** API.

Related deep-dives (optional):

- [BULK_UPLOAD_FRONTEND_GUIDE.md](./BULK_UPLOAD_FRONTEND_GUIDE.md)
- [DOCUMENT_INTELLIGENT_AGENT.md](./DOCUMENT_INTELLIGENT_AGENT.md)
- [REPOSITORY_TIMELINE_AND_COMMENTS_FRONTEND_GUIDE.md](./REPOSITORY_TIMELINE_AND_COMMENTS_FRONTEND_GUIDE.md)

---

## 1. File ticket (open archive file → show ticket)

When a file was used to **raise a ticket**, show ticket ref, current stage, assignee, and full history.

```http
GET /api/repositories/{repositoryId}/items/{itemId}/ticket
```

### Response

```json
{
  "itemId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
  "hasTicket": true,
  "ticket": {
    "workflowInstanceId": "…",
    "workflowId": "…",
    "referenceNumber": "AP-2026-00123",
    "currentStage": "Document Approval",
    "ticketStatus": "Pending — Document Approval",
    "assigneeEmail": "approver@example.com",
    "history": [
      {
        "sequence": 1,
        "title": "Request initiated for this file",
        "description": "Assigned to …",
        "stageName": "Start",
        "actorName": "user@example.com",
        "occurredAtUtc": "2026-09-24T08:00:00Z",
        "milestone": "start",
        "actionStatus": 0
      },
      {
        "sequence": 2,
        "title": "Approved",
        "milestone": "approved",
        "actionStatus": 1
      },
      {
        "sequence": 3,
        "title": "Forwarded",
        "description": "Forwarded to other@example.com",
        "milestone": "forwarded",
        "actionStatus": 0
      }
    ]
  }
}
```

| Field | UI use |
|-------|--------|
| `hasTicket: false` | Hide ticket panel |
| `ticket.currentStage` / `ticketStatus` | Status chip |
| `ticket.history` | Timeline / stepper |
| `milestone` | Optional icons: `start`, `approved`, `forwarded`, `pending`, `completed` |

**When to call:** opening a repository file (filename click), not inside workspace payload.

---

## 2. Item timeline (activity feed)

```http
GET /api/repositories/{repositoryId}/items/{itemId}/timeline
```

Same auth. Also returns ticket summary fields on the timeline payload:

| Field | Meaning |
|-------|---------|
| `hasTicket` | File linked to a ticket |
| `linkedWorkflowInstanceId` / `linkedWorkflowId` / `linkedWorkflowReferenceNumber` | Ticket ids |
| `currentStage` / `ticketStatus` / `assigneeEmail` | Current workflow state |
| `ticketHistory` | Same steps as `/ticket` |
| `events[]` | Full feed (upload, OCR, workflow, comments, …) |

Workflow event **titles** FE should expect:

- `Request initiated for this file`
- `Approved`
- `Forwarded`
- `Pending — {stage}`
- `Workflow completed`

Workspace is **not** modified for ticket data — prefer `/ticket` for the ticket panel and `/timeline` for the activity feed.

---

## 3. Document Intelligent Agent (classify repo)

Infers which **repository** a document belongs to. Proxies agents `/chat` with `intent=document_intelligent`.

### JSON

```http
POST /api/repositories/document-intelligent-agent
Content-Type: application/json
```

```json
{
  "sessionId": "demo-di",
  "ocrText": "TAX INVOICE\nVendor: …",
  "filepath": null,
  "pageno": "1",
  "includeRepositoryCatalog": true
}
```

Do **not** send `tenantId` — taken from token / `X-Tenant-Id`.

### Multipart (file)

```http
POST /api/repositories/document-intelligent-agent/upload
Content-Type: multipart/form-data
```

| Form field | Notes |
|------------|--------|
| `file` | PDF / image / Word / Excel |
| `session_id` | Optional |
| `pageno` | Optional (default `1` when file present) |
| `include_repository_catalog` | Optional, default `true` |

**Do not** set a manual `Content-Type` header without boundary. Do not send Swagger placeholders (`ocr_text=string`).

### Response (unwrapped)

Only `document_intelligent_result`:

```json
{
  "confidence_score": 88.0,
  "repository_id": "…",
  "repository_name": "Accounts Payable",
  "rationale": "…",
  "candidates": [
    { "repository_id": "…", "repository_name": "…", "score": 88.0 }
  ],
  "ocr_text": "…",
  "source_reference": "invoice.pdf"
}
```

Use `repository_id` to pre-select the target repository for upload / raise-ticket.

---

## 4. Bulk upload + job file status

### Start bulk upload

```http
POST /api/uploadAndIndex/bulkUpload
Content-Type: multipart/form-data
```

| Form | Notes |
|------|--------|
| `files` | One or more files |
| `repositoryId` | Repository GUID |
| `fields` | Shared metadata (same for all files) |
| `pageNo` / `ocrType` / `validateType` | Optional OCR hints |

**202 response** includes `jobId` + staged `files[]` (`fileId` = stage id).

### Poll job (per-file OCR / index status)

```http
GET /api/uploadAndIndex/bulkUpload/jobs/{jobId}
```

```json
{
  "jobId": "36911",
  "hangfireState": "Processing",
  "isTerminal": false,
  "repositoryId": "…",
  "ocrCompleted": 1,
  "ocrPending": 1,
  "ocrFailed": 0,
  "indexed": 0,
  "readyToIndex": 1,
  "notCompleted": 1,
  "files": [
    {
      "fileId": "…",
      "fileName": "a.pdf",
      "status": "OCR",
      "phase": "OcrCompleted",
      "ocrCompleted": true,
      "indexed": false,
      "completed": false,
      "promotedItemId": null
    }
  ]
}
```

| `phase` | Meaning |
|---------|---------|
| `PendingOcr` | Waiting / running OCR |
| `OcrCompleted` | OCR done — **ready to archive/export** |
| `OcrFailed` | OCR failed |
| `Indexed` | Already exported (`PUT index/{id}`) |
| `Missing` | Stage row gone |

### Active job (optional)

```http
GET /api/uploadAndIndex/bulkUpload/jobs/active?repositoryId={optional}
```

404 = no active job.

---

## 5. Repository index files (ready to archive)

When user opens a **repository**, list staged files with status, field values, and view/download URLs:

```http
POST /api/uploadAndIndex/index/all
Content-Type: application/json
```

```json
{
  "repositoryId": "{repo-guid}",
  "currentPage": 1,
  "itemsPerPage": 50,
  "mode": "browse"
}
```

### Response item (enriched)

```json
{
  "id": "{stage-fileId}",
  "fileId": "{stage-fileId}",
  "name": "invoice.pdf",
  "status": "OCR",
  "stageStatus": "OCR",
  "fileType": "application/pdf",
  "filePath": "monitor/{repoId}/…/invoice.pdf",
  "size": 12345,
  "createdAt": "2026-09-24T10:00:00Z",
  "repositoryId": "…",
  "repositoryName": "Accounts Payable",
  "itemId": null,
  "promotedItemId": null,
  "isIndexed": false,
  "fields": [
    { "name": "Year", "value": "2026", "type": "SHORT_TEXT" },
    { "name": "Vendor", "value": "Acme", "type": "SHORT_TEXT" }
  ],
  "fileUrl": "/api/uploadAndIndex/files/{stage-fileId}",
  "downloadUrl": "/api/uploadAndIndex/files/{stage-fileId}?disposition=attachment",
  "itemFileUrl": null,
  "itemDownloadUrl": null
}
```

After archive (`itemId` set):

| Field | Use |
|-------|-----|
| `fileUrl` / `downloadUrl` | Stage/monitor file (may still exist until cleanup) |
| `itemFileUrl` / `itemDownloadUrl` | **Archive** view/download — prefer these when `isIndexed` |
| `fields` | Stage column / OCR field values for the grid |
| `status` / `stageStatus` | Show status chip |

| Condition | UI |
|-----------|-----|
| `isIndexed == false` / `itemId == null` | **Ready to archive** |
| `isIndexed == true` | Already archived — use item URLs |

### IndexFilesDownload — stage `fileId` (monitor)

Swagger / action name: **`IndexFilesDownload`**.

```http
GET /api/uploadAndIndex/files/{fileId}?disposition=inline
GET /api/uploadAndIndex/files/{fileId}?disposition=attachment
```

Same auth headers. `inline` = browser view/PDF preview; `attachment` = download.

### View / download — archive `itemId`

```http
GET /api/repositories/{repositoryId}/items/{itemId}/file?disposition=inline
GET /api/repositories/{repositoryId}/items/{itemId}/file?disposition=attachment
```

(Or use `itemFileUrl` / `itemDownloadUrl` from index/all.)

### Archive one file

```http
PUT /api/uploadAndIndex/index/{fileId}
Content-Type: application/json
```

Body: field updates (same as existing index/export). Sync archive — no Hangfire.

### Delete staged files

```http
POST /api/uploadAndIndex/index/deleteFiles
```

```json
{
  "fileIds": ["{stage-guid}", "…"],
  "repositoryId": "{repo-guid}"
}
```

Also available as `POST /api/uploadAndIndex/upload/deleteFiles`. Soft-deletes stage row and removes monitor blob.

---

## 6. Upload with OCR (stage, not archive)

```http
POST /api/uploadAndIndex/uploadWithOcr
Content-Type: multipart/form-data
```

| Form | Notes |
|------|--------|
| `file` | Required (PDF, images, **Word `.doc/.docx`**, Excel supported) |
| `repositoryId` | Required GUID |
| `metadata` | JSON field values **or** |
| `fields` | Alternate field JSON |
| `ocrJson` / `ocrText` | Pre-extracted OCR (from `uploadForOcr`) |

Returns stage `fileId` — **does not archive**. Archive with `PUT …/index/{fileId}` or raise-ticket start (promotes stage).

OCR-only (no stage): `POST /api/uploadAndIndex/uploadForOcr`.

---

## 7. Forward (workflow) — FE notes

Move-next with review = `"Forward"`:

- Step stays **open** (`actionStatus` 0)
- Assignee gets **Inbox**; sender keeps **Sent**
- History / file timeline show **`Forwarded`**
- If target user has no workflow access, API **grants access** automatically

No new Forward URL — use existing move-next / action payload with `review: "Forward"`.

---

## Quick map

| FE need | Call |
|---------|------|
| Ticket panel on open file | `GET …/items/{itemId}/ticket` |
| Activity feed | `GET …/items/{itemId}/timeline` |
| Classify document → repository | `POST …/document-intelligent-agent` (+ `/upload`) |
| Bulk upload | `POST …/bulkUpload` → poll `…/jobs/{jobId}` |
| Repo “ready to archive” list | `POST …/index/all` + `repositoryId` |
| Stage file view/download | **IndexFilesDownload** `GET …/uploadAndIndex/files/{fileId}` |
| Archive item view/download | `GET …/repositories/{repoId}/items/{itemId}/file` |
| Export / archive | `PUT …/index/{fileId}` |
| Stage with OCR JSON | `POST …/uploadWithOcr` |

---

## Errors (common)

| HTTP | Typical cause |
|------|----------------|
| 400 | Missing `repositoryId` / file / validation |
| 404 | Item / job / no active bulk job |
| 502 / 504 | Agents `/chat` down or timeout (Document Intelligent, OCR) |
