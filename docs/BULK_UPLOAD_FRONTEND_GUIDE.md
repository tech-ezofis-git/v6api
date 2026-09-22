# Bulk Upload + Stage Export — Frontend guide

**Audience:** Frontend  
**Date:** 21 Sep 2026  
**Base path:** `https://cloud.ezofis.com/api` (local: your API host, e.g. `https://localhost:44311`)

Bulk upload stages many files with the **same field inputs**, runs OCR in the background (Hangfire), keeps rows in the **stage** table until the user **exports**. Export archives **immediately** (no Hangfire).

---

## 1. Common headers

| Header | When |
|--------|------|
| `Authorization: Bearer <jwt>` | Always |
| `X-Tenant-Id: <tenant-guid>` | Tenant context |
| `Content-Type` | `multipart/form-data` for bulk upload; `application/json` for list / load / export |

---

## 2. End-to-end flow

```text
1. POST /api/uploadAndIndex/bulkUpload     → stage files + jobId (OCR queued)
2. GET  /api/uploadAndIndex/bulkUpload/jobs/{jobId}  → poll until OCR done
3. POST /api/uploadAndIndex/index/all      → list stage files by repositoryId
4. POST /api/uploadAndIndex/load/{fileId}  → open one file for review/edit
5. PUT  /api/uploadAndIndex/index/{fileId} → export (archive sync) → ARCHIVED
```

| Step | Hangfire? |
|------|-----------|
| Bulk OCR | Yes (background) |
| Export / archive | **No** — sync in the PUT response |

---

## 3. Bulk upload

```http
POST /api/uploadAndIndex/bulkUpload
Authorization: Bearer <jwt>
X-Tenant-Id: <tenant>
Content-Type: multipart/form-data
```

### Form fields

| Field | Required | Notes |
|-------|----------|--------|
| `files` | Yes | Multiple files (repeat the `files` part) |
| `repositoryId` | Yes | Repository GUID |
| `fields` | Recommended | Shared values **or** OCR field definitions (see below) |
| `pageNo` | No | OCR page option (omit Swagger default `string`) |
| `ocrType` | No | Same |
| `validateType` | No | Same |

### Shared field **values** (preferred for bulk metadata)

Send JSON once; same values apply to every file:

```text
fields: [{"name":"Customer","value":"Acme"},{"name":"Port","value":"Chennai"}]
```

Or one JSON object:

```text
fields: {"Customer":"Acme","Port":"Chennai"}
```

### OCR field definitions (type hints)

Swagger-style lines are OK — used as OCR parameters. Empty values do **not** overwrite OCR results:

```text
fields: Year, SINGLE_SELECT
fields: Customer, SHORT_TEXT
fields: Vessel, SHORT_TEXT
…
```

### Example (curl)

```bash
curl -X POST 'https://cloud.ezofis.com/api/uploadAndIndex/bulkUpload' \
  -H 'Authorization: Bearer <jwt>' \
  -H 'X-Tenant-Id: <tenant>' \
  -F 'files=@doc1.pdf;type=application/pdf' \
  -F 'files=@doc2.pdf;type=application/pdf' \
  -F 'repositoryId=b7a11af6-41c3-49e9-9009-988a335b8585' \
  -F 'fields=[{"name":"Customer","value":"Acme"}]'
```

### Response — **202 Accepted**

```json
{
  "repositoryId": "b7a11af6-41c3-49e9-9009-988a335b8585",
  "jobId": "36850",
  "message": "Upload successful. OCR queued — poll job status for progress.",
  "files": [
    {
      "fileId": "050393f8-abbc-4f56-8f3e-8b0df7805d27",
      "repositoryId": "b7a11af6-41c3-49e9-9009-988a335b8585",
      "fileName": "doc1.pdf",
      "filePath": "",
      "ocrJson": "",
      "ocrFieldList": null,
      "succeeded": true,
      "error": null,
      "status": "Queued"
    }
  ],
  "succeeded": 2,
  "failed": 0
}
```

| Field | Use |
|-------|-----|
| `jobId` | Poll OCR progress (one job for the whole batch) |
| `files[].fileId` | Stage id — load / export later |
| `files[].succeeded` | Upload/stage succeeded (OCR may still be running) |
| `files[].status` | `Queued` right after upload |

Save `jobId` + each `fileId` in UI state.

---

## 4. Poll OCR job status

One `jobId` covers **all files** in that bulk batch.

```http
GET /api/uploadAndIndex/bulkUpload/jobs/{jobId}
Authorization: Bearer <jwt>
X-Tenant-Id: <tenant>
```

### Response — **200**

```json
{
  "jobId": "36850",
  "hangfireState": "Processing",
  "isTerminal": false,
  "errorMessage": null,
  "repositoryId": "b7a11af6-41c3-49e9-9009-988a335b8585",
  "files": [
    {
      "fileId": "050393f8-abbc-4f56-8f3e-8b0df7805d27",
      "fileName": "doc1.pdf",
      "status": "OCR",
      "error": null
    },
    {
      "fileId": "062b7eeb-554e-451d-a949-b8351b568638",
      "fileName": "doc2.pdf",
      "status": "Queued",
      "error": null
    }
  ],
  "ocrCompleted": 1,
  "ocrPending": 1,
  "ocrFailed": 0
}
```

### UI status mapping

| `hangfireState` | Meaning |
|-----------------|--------|
| `Enqueued` | Waiting for worker |
| `Processing` | OCR running (one file at a time) |
| `Succeeded` | Job finished (check per-file `status`) |
| `Failed` | Job failed — see `errorMessage` |

| Per-file `status` | Meaning |
|-------------------|--------|
| `Queued` / `PendingOCR` | Waiting for OCR |
| `OCR` | OCR done — ready to open / export |
| `OCRFailed` | That file’s OCR failed |
| `ARCHIVED` | Already exported |

**Suggested poll:** every 2–3s while `!isTerminal` or `ocrPending > 0`. Stop when `isTerminal && ocrPending === 0`.

---

## 5. List stage files by repository

When user opens a repository’s upload/index list:

```http
POST /api/uploadAndIndex/index/all
Authorization: Bearer <jwt>
X-Tenant-Id: <tenant>
Content-Type: application/json
```

```json
{
  "repositoryId": "b7a11af6-41c3-49e9-9009-988a335b8585",
  "currentPage": 1,
  "itemsPerPage": 50
}
```

### Response shape

```json
{
  "data": [
    {
      "key": "",
      "value": [
        {
          "id": "050393f8-abbc-4f56-8f3e-8b0df7805d27",
          "name": "doc1.pdf",
          "status": "OCR",
          "repositoryId": "b7a11af6-41c3-49e9-9009-988a335b8585",
          "repositoryName": "…",
          "size": 6686,
          "createdAt": "…",
          "promotedItemId": null
        }
      ]
    }
  ],
  "meta": {
    "currentPage": 1,
    "itemsPerPage": 50,
    "totalItems": 2
  }
}
```

`id` = **fileId** for load / export. Archived rows may be excluded from the default list.

---

## 6. Open one stage file (review / edit)

```http
POST /api/uploadAndIndex/load/{fileId}
Authorization: Bearer <jwt>
X-Tenant-Id: <tenant>
```

Use `fields` from the response to bind the indexing form. User can edit before export.

---

## 7. Export (archive) — sync, no Hangfire

```http
PUT /api/uploadAndIndex/index/{fileId}
Authorization: Bearer <jwt>
X-Tenant-Id: <tenant>
Content-Type: application/json
```

```json
{
  "repositoryId": "b7a11af6-41c3-49e9-9009-988a335b8585",
  "status": "Indexed",
  "fields": [
    { "name": "Customer", "value": "Atlas Ocean Transport Ltd" },
    { "name": "Vessel", "value": "MV NORTH VOYAGER" },
    { "name": "Port", "value": "Tuticorin (VOC Port)" }
  ]
}
```

- `fields` optional if stage columns are already filled by OCR; send what the user changed.
- Response is **200** when archive completes (not 202).

### Success response

```json
{
  "stageId": "050393f8-abbc-4f56-8f3e-8b0df7805d27",
  "hangfireJobId": "",
  "message": "Archived successfully.",
  "itemId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
  "fileName": "Vessel_Call_Sample_06_GA.pdf",
  "filePath": "archive/…"
}
```

| Field | Use |
|-------|-----|
| `itemId` | Archive document id |
| `filePath` | Archive storage path |
| `message` | Show success toast |

Stage row becomes **`ARCHIVED`**. File leaves the active stage list.

---

## 8. Frontend checklist

1. Multi-file picker → `POST bulkUpload` with shared `fields` + `repositoryId`.
2. Show toast “Upload successful” using `succeeded` / `failed` counts.
3. Store `jobId`; poll `GET bulkUpload/jobs/{jobId}` and show per-file progress (`ocrCompleted` / `ocrPending` / `ocrFailed`).
4. Repository open → `POST index/all` with `repositoryId`.
5. Row click → `POST load/{fileId}` → edit form.
6. **Export** button → `PUT index/{fileId}` → wait for **200** → refresh list / navigate to archive item.
7. Do not expect a Hangfire job id for export (`hangfireJobId` is empty).

---

## 9. Errors

| HTTP | Typical cause |
|------|----------------|
| 400 | Missing `repositoryId`, no files, validation / archive metadata error |
| 401 / 403 | Auth / tenant |
| 404 | Unknown `fileId` or job id |
| 202 on bulk | Normal — OCR still running; use `jobId` |

Per-file upload failures appear in `files[].succeeded: false` + `files[].error` without failing the whole batch.

---

## 10. Quick reference

| Action | Method + path |
|--------|----------------|
| Bulk upload | `POST /api/uploadAndIndex/bulkUpload` |
| OCR job status | `GET /api/uploadAndIndex/bulkUpload/jobs/{jobId}` |
| List stage | `POST /api/uploadAndIndex/index/all` |
| Load one | `POST /api/uploadAndIndex/load/{fileId}` |
| Export / archive | `PUT /api/uploadAndIndex/index/{fileId}` |
