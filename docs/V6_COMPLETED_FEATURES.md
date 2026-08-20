# V6 API — Completed Features Documentation

**Audience:** Frontend, Python, QA, DevOps  
**Last updated:** June 2026  
**Base path:** `/V6API` (configurable via `PathBase` in appsettings)

This document describes recently completed V6 features: Upload OCR, Repository Upload/Index fixes, Social Login, Bulk Move-Next, AP Agent (multi-tenant + pilot user), and Master File Upload.

---

## Table of contents

1. [Common authentication](#1-common-authentication)
2. [Upload OCR](#2-upload-ocr)
3. [Repository upload & index (fixes)](#3-repository-upload--index-fixes)
4. [Social login](#4-social-login)
5. [Bulk move-next](#5-bulk-move-next)
6. [AP Agent (all tenants) + pilot user](#6-ap-agent-all-tenants--pilot-user)
7. [Upload master file (form import)](#7-upload-master-file-form-import)
8. [Configuration reference](#8-configuration-reference)
9. [Pending / next steps](#9-pending--next-steps)

---

## 1. Common authentication

Most tenant APIs require:

| Header | Required | Description |
|--------|----------|-------------|
| `Authorization` | Yes | `Bearer {JWT}` |
| `X-Tenant-Id` | Yes* | Tenant GUID |

\*Some auth endpoints (social login, signup) require `X-Tenant-Id` but no JWT. After login, JWT includes `tid` claim; header can still be sent.

**Swagger:** `/V6API/swagger` (when `Swagger:Enabled` is true)

---

## 2. Upload OCR

Direct OCR extraction for repository documents. Calls an external Python OCR service; does **not** stage or archive the file.

### Endpoint

| Method | Route |
|--------|-------|
| `POST` | `/api/uploadAndIndex/uploadForOcr` |

**Auth:** JWT + `X-Tenant-Id`  
**Content-Type:** `multipart/form-data`  
**Max size:** 100 MB

### Request (form fields)

| Field | Required | Description |
|-------|----------|-------------|
| `file` | Yes | Document to OCR (PDF, image, etc.) |
| `repositoryId` | Yes | Repository GUID |
| `fields` | No* | OCR field definitions (see below) |
| `pageNo` | No | Page number(s); default from config |
| `ocrType` | No | e.g. `ADVANCED`, `tesseract`; default from config |
| `validateType` | No | Default from config |

\*If `fields` is omitted, parameters are built from the repository field definitions.

### `fields` format

Accepted formats:

- JSON array: `["Invoice Number,SHORT_TEXT","Invoice Date,DATE"]`
- JSON objects: `[{"name":"Invoice Number","type":"SHORT_TEXT"}]`
- Plain text: `Invoice Number,SHORT_TEXT`
- Multiple `fields` form parts (Swagger sends one per parameter — all are merged)

Supported types: `SHORT_TEXT`, `DATE`, `NUMBER`, `LONG_TEXT`, `TABLE`.

### Response `200 OK`

```json
{
  "ocrJson": "<raw JSON string from Python OCR API>",
  "ocrFieldList": [
    { "name": "Invoice Number", "value": "12345", "type": "SHORT_TEXT" }
  ]
}
```

### Outbound call to Python OCR

V6 POSTs JSON to `Repository:Ocr:UploadForOcrApiUrl`:

```json
{
  "filepath": "<base64>",
  "file": "<base64>",
  "pageno": "1",
  "ocrtype": "ADVANCED",
  "validatetype": "1",
  "parameters": ["Invoice Number, SHORT_TEXT"],
  "tableparameters": [{ "LineItems": [] }],
  "filename": "invoice.pdf",
  "repositoryId": "guid"
}
```

If Python returns `"no text extracted"`, V6 retries once with `ocrtype: "tesseract"`.

### Key files

| Role | Path |
|------|------|
| Controller | `src/Api/Controllers/UploadAndIndexController.cs` |
| OCR service | `src/Modules/Repository/Repository.Infrastructure/Services/OcrExtractionService.cs` |
| Options | `src/Modules/Repository/Repository.Infrastructure/Options/RepositoryOcrOptions.cs` |

---

## 3. Repository upload & index (fixes)

v5-compatible upload → stage → index → archive flow, with several binding and archive fixes.

### Endpoints

| Method | Route | Purpose |
|--------|-------|---------|
| `POST` | `/api/uploadAndIndex/upload` | Stage file (no OCR call) |
| `POST` | `/api/uploadAndIndex/uploadForOcr` | OCR only (see section 2) |
| `POST` | `/api/uploadAndIndex/load/{id}` | Load staged row for indexing UI |
| `PUT` | `/api/uploadAndIndex/index/{id}` | Save fields + queue Hangfire archive |
| `POST` | `/api/uploadAndIndex/index/all` | List staged rows (v5-shaped response) |

### `POST /upload` — stage file

**multipart/form-data:**

| Field | Required |
|-------|----------|
| `file` | Yes |
| `repositoryId` | Yes (GUID) |
| `filename` | No |
| `fields` | No (pre-filled values, not sent to OCR) |

**Response:**

```json
{
  "fileId": "stage-guid",
  "ocrFieldList": [{ "name": "...", "value": "...", "type": null }]
}
```

### `PUT /index/{id}` — save and archive

**JSON body:**

```json
{
  "repositoryId": "guid",
  "itemId": "optional",
  "status": "optional",
  "fields": [
    { "name": "VendorName", "value": "ACME", "type": "text" }
  ],
  "ocrResult": "optional raw OCR JSON to persist"
}
```

**Response `202 Accepted`:**

```json
{
  "stageId": "guid",
  "hangfireJobId": "...",
  "message": "Archive queued..."
}
```

### `POST /index/all` — list staged items

```json
{
  "repositoryId": "guid",
  "currentPage": 1,
  "itemsPerPage": 50,
  "mode": "browse"
}
```

**Response (v5-compatible shape):**

```json
{
  "data": [{ "key": "", "value": [ /* items */ ] }],
  "meta": { "currentPage": 1, "itemsPerPage": 50, "totalItems": 10 }
}
```

### Fixes included

| Issue | Fix |
|-------|-----|
| Multiple `fields` in multipart upload | All Swagger `fields` parts are merged (previously only first was kept) |
| Archive metadata | `CreatedBy` uses user email in workspace/archive records |
| `index/all` response shape | v5-compatible `{ data, meta }` wrapper |
| Legacy int `repositoryId` | Rejected with 400 — GUID required |
| Archive promotion | Queued via Hangfire `ArchiveStageItemJob` (async, not blocking) |

### Storage paths

| Stage | Blob path pattern |
|-------|-------------------|
| Monitor (staging) | `monitor/{repositoryId:N}/{timestamp}/{fileName}` |
| Archive (after job) | `archive/{repositoryName}/{folder-fields}/{file}.ext` |

### Key files

| Role | Path |
|------|------|
| Controller | `src/Api/Controllers/UploadAndIndexController.cs` |
| Service | `src/Modules/Repository/Repository.Infrastructure/Services/RepositoryUploadIndexService.cs` |
| Archive job | `src/Modules/Repository/Repository.Infrastructure/Jobs/ArchiveStageItemJob.cs` |
| Stage store | `src/Modules/Repository/Repository.Infrastructure/RepositoryStageStore.cs` |

---

## 4. Social login

Google and Microsoft login using email + provider (no password). Frontend completes OAuth with Google/Microsoft first; V6 matches the identity against the tenant user record.

### Endpoint

| Method | Route | Auth |
|--------|-------|------|
| `POST` | `/api/auth/social/login` | None (anonymous) |

**Required header:** `X-Tenant-Id: {tenant-guid}`

### Request

```json
{
  "email": "user@company.com",
  "provider": "google"
}
```

| `provider` value | Accepted aliases |
|------------------|------------------|
| `google` | `GOOGLE`, `Google` |
| `microsoft` | `office365`, `MICROSOFT`, `Office365` |

### Response `200 OK`

```json
{
  "userId": "guid",
  "accessToken": "jwt...",
  "tokenType": "Bearer",
  "expiresIn": 86400
}
```

### Validation rules

1. User must exist in the tenant DB (matched by email).
2. User `tenantId` must match `X-Tenant-Id`.
3. **Rejected:** EZOFIS password-only users (`loginType`/`authStrategy` = Ezofis) — they must use `POST /api/auth/ezofis/login`.
4. **Required:** Social identity must match provider:
   - Google: `loginType` = `GOOGLE` or `authStrategy` = `Google`
   - Microsoft: `loginType` = `MICROSOFT`/`Office365` or `authStrategy` = `Office365`

### JWT claims

`sub`, `email`, `name`, `role`, `tid` (tenant id)

### Errors

| Status | Condition |
|--------|-----------|
| 400 | Missing tenant, email, or provider; invalid provider |
| 401 | User not found, wrong tenant, EZOFIS user, provider mismatch |
| 500 | JWT signing key misconfigured |

### Related endpoints

| Endpoint | Purpose |
|----------|---------|
| `POST /api/auth/ezofis/login` | Email + password login |
| `POST /api/auth/2fa/complete` | Complete 2FA after Ezofis login |
| `GET /api/auth/tenants?email=` | Pre-login org picker |
| `GET /api/me/tenants` | Post-login org list |

### Key files

| Role | Path |
|------|------|
| Controller | `src/Api/Controllers/Auth/LoginController.cs` |
| Service | `src/Api/Services/EzofisAuthService.cs` |
| Tenant middleware | `src/Api/Middleware/TenantConnectionMiddleware.cs` |

### Security note

V6 does **not** validate Google/Microsoft ID tokens server-side yet. The frontend must complete OAuth and send the verified email. Server-side token validation is a future enhancement.

---

## 5. Bulk move-next

Move multiple workflow tickets in one request. No `formData` — ticket-only moves (approve/send to next step).

### Endpoint

| Method | Route |
|--------|-------|
| `POST` | `/api/workflows/instances/bulk-move-next` |

**Auth:** JWT + `X-Tenant-Id`

### Request

```json
{
  "instanceId": "guid1,guid2,guid3",
  "activityid": "ACTIVITY-BLOCK-ID",
  "review": "Approve",
  "comments": "optional",
  "activityUserId": "optional-guid"
}
```

Alternative — use array instead of comma-separated string:

```json
{
  "instanceIds": ["guid1", "guid2", "guid3"],
  "activityid": "ACTIVITY-BLOCK-ID",
  "review": "Approve"
}
```

Both `instanceId` and `instanceIds` can be combined; duplicates are removed.

| Field | Required | Notes |
|-------|----------|-------|
| `activityid` | Yes | Current workflow activity/block id (lowercase JSON property) |
| `instanceId` | Yes* | Comma-separated GUIDs |
| `instanceIds` | Yes* | GUID array |
| `review` | No | e.g. `Approve` — completes step and opens next |
| `comments` | No | Optional comment |
| `activityUserId` | No | Assign next step to specific user |

\*At least one instance id source is required.

### Response `200 OK`

```json
{
  "total": 3,
  "succeeded": 2,
  "failed": 1,
  "results": [
    {
      "instanceId": "guid",
      "success": true,
      "message": "Review updated.",
      "workflowCompleted": false,
      "error": null
    }
  ]
}
```

Each instance is processed independently. One failure does not stop the others.

### Inbox / Sent / Completed sync

Bulk move uses the same engine as single move-next. After each successful move:

- **Inbox** — ticket removed or updated for pending tasks
- **Sent** — ticket appears for the user who acted
- **Completed** — ticket moves when workflow reaches END

Refresh inbox/sent/completed lists after bulk move; counts update automatically.

### Usage notes

| Source list | Bulk move |
|-------------|-----------|
| Inbox | Primary use case — pending tasks |
| Sent | Only if ticket can still move at given `activityid` |
| Completed | Usually fails — workflow already finished |

### Single move (with formData)

For one ticket with form data, use the existing endpoint:

`POST /api/workflows/instances/{instanceId}/move-next`

### Key files

| Role | Path |
|------|------|
| Controller | `src/Api/Controllers/WorkflowsController.cs` |
| Command | `src/Modules/Workflow/Workflow.Application/Workflows/Commands/MoveToNextStep/BulkMoveToNextStepCommand.cs` |
| Handler | `src/Modules/Workflow/Workflow.Application/Workflows/Commands/MoveToNextStep/BulkMoveToNextStepCommandHandler.cs` |

---

## 6. AP Agent (all tenants) + pilot user

AP Agent runs per tenant using tenant-scoped databases, Hangfire jobs, and a service account (pilot user) for Python callbacks.

### Pilot user (`TenantPilotUser`)

Created automatically on **new tenant signup** when configured.

| Config key | Default | Purpose |
|------------|---------|---------|
| `Enabled` | `true` | Create pilot on signup |
| `Email` | `pilot@ezofis.com` | Service account email |
| `Password` | *(required)* | BCrypt-hashed password |
| `DisplayName` | `AP Agent Pilot` | Display name |
| `Role` | `TenantUser` | Role in tenant + catalog |

**Each tenant** gets its own `pilot@ezofis.com` user in its own database. Isolation is by `X-Tenant-Id`.

**Existing tenants:** Pilot user is not auto-created. Add manually via `POST /api/users` (Admin) or run a one-time script.

### AP Agent flow

```
Frontend → POST /workflows/{id}/start (with file)
         → .NET enqueues Hangfire job, returns apAgentJobId
         → .NET POSTs startPayload to Python (includes tenantId, callback URLs)
         → Python logs in as pilot@ezofis.com (POST /api/auth/ezofis/login + X-Tenant-Id)
         → Python PATCHes progress, applies metadata
         → Frontend polls GET /ap-agent/jobs/{jobId} every ~5s
```

**Parallel execution:** Multiple customers can start workflows at the same time. Each start creates a separate Hangfire job; jobs run in parallel up to `Hangfire:WorkerCount` (default 10). Python must also scale workers to match load.

### AP Agent endpoints

| Method | Route | Who |
|--------|-------|-----|
| `POST` | `/api/workflows/{workflowId}/start` | Frontend (multipart + file) |
| `POST` | `/api/workflows/{workflowId}/instances/{instanceId}/ap-agent/run?background=true` | Frontend (manual run) |
| `GET` | `/api/workflows/ap-agent/jobs/{jobId}` | Frontend (poll status) |
| `PATCH` | `/api/workflows/ap-agent/jobs/{jobId}/progress` | Python |
| `PATCH` | `/api/workflows/{workflowId}/instances/{instanceId}/ap-agent/progress` | Python |
| `PATCH` | `/api/workflows/{workflowId}/instances/{instanceId}/ap-agent/metadata` | Python |

**Detailed AP Agent docs:** [AP_AGENT_STATUS_TRACKING.md](./AP_AGENT_STATUS_TRACKING.md)

### Start payload (sent to Python)

```json
{
  "startPayload": {
    "blobPath": "...",
    "envType": "trial",
    "tenantId": "guid",
    "workflowId": "guid",
    "repositoryId": "guid",
    "itemId": "guid",
    "instanceId": "guid",
    "transactionId": "guid",
    "formentryId": 123,
    "formId": "...",
    "apAgentJobId": "12345",
    "apAgentJobStatusUrl": "/api/workflows/ap-agent/jobs/12345",
    "apAgentProgressUrl": "/api/workflows/{wf}/instances/{inst}/ap-agent/progress"
  }
}
```

### Python multi-tenant contract

1. Read `startPayload.tenantId`
2. Login: `POST /api/auth/ezofis/login` with pilot credentials + `X-Tenant-Id`
3. Call tenant APIs with JWT + `X-Tenant-Id`
4. PATCH progress using URLs from payload

### Key files

| Role | Path |
|------|------|
| Pilot options | `src/Api/Options/TenantPilotUserOptions.cs` |
| Pilot creation | `src/Api/Services/TenantSignupService.cs` |
| AP Agent controller | `src/Api/Controllers/WorkflowsController.cs` |
| Python pipeline | `src/Modules/Workflow/Workflow.Infrastructure/Services/ApAgentPythonPipelineService.cs` |
| Hangfire job | `src/Modules/Workflow/Workflow.Infrastructure/Jobs/RunApAgentPythonJob.cs` |
| Start payload | `src/Modules/Workflow/Workflow.Infrastructure/Services/WorkflowStartBootstrapService.cs` |

---

## 7. Upload master file (form import)

v5-compatible master CSV/XLSX upload for bulk form data import. V6 handles upload + queue; row import is done by external Python/data-import worker.

### Endpoint

| Method | Route |
|--------|-------|
| `POST` | `/api/form/uploadMasterFile` |

**Auth:** JWT + `X-Tenant-Id`  
**Content-Type:** `multipart/form-data`  
**Max size:** 100 MB

### Request

| Field | Required | Description |
|-------|----------|-------------|
| `formId` | Yes | Form GUID |
| `workflowId` | No | Workflow GUID |
| `instanceId` | No | Workflow instance GUID (v6 — replaces v5 `processId`) |
| `file` | Yes | CSV, XLS, or XLSX |

### Response `200 OK`

Returns `masterFileprocess` id as plain string:

```
"42"
```

### What V6 does

1. Uploads file to tenant blob container `ezts{tenantGuid}` (lowercase)
2. Inserts row in `dbo.masterFileprocess` (`inputType=FORM`, `status=0`)
3. Writes queue JSON blob for external importer

### Blob paths

| Purpose | Path pattern |
|---------|--------------|
| Master file | `Form Files/{formId}/Master CSV/{yyyyMMddHHmmss}/{filename}` |
| Import queue JSON | `ezPackages/MasterExcel/{tenantGuid}_{processId}_{timestamp}.json` |

Queue prefix configurable via `FormMasterFileImport:QueueBlobPathPrefix`.

### Queue JSON payload

```json
{
  "fileName": "invoices.csv",
  "filepath": "Form Files/{formId}/Master CSV/{timestamp}/invoices.csv",
  "id": "42",
  "formId": "...",
  "tenantId": "{tenant-guid}",
  "conditionColumn": ["entryId"],
  "userid": "{user-guid}",
  "workflowId": "{workflow-guid}",
  "instanceId": "{instance-guid}",
  "settingsJson": "{...}",
  "notifyId": 1,
  "masterFileProcessId": 42
}
```

`tenantId`, `workflowId`, and `instanceId` are always GUID strings. There is no separate `tenantGuid` or v5 `processId`.

**Notification `inputJson`:**

```json
{
  "workflowId": "{workflow-guid}",
  "instanceId": "{instance-guid}"
}
```

`conditionColumn` comes from `dbo.wForm.uniqueColumns` (comma-split); default `["entryId"]`.

### Python / data-import worker (pending)

V6 does **not** import rows into `ezfb_{form}_items` in-process. The existing **external data-import worker** (v5 `MasterExcel` flow) must:

1. Watch `ezPackages/MasterExcel/*.json` in tenant blob storage
2. Read the master file from the path in `fileName`
3. Import rows into the form's `ezfb_*` tables
4. Update `dbo.masterFileprocess.status` when done

Disable queuing with `FormMasterFileImport:Enabled: false` if blob worker is not deployed.

### Key files

| Role | Path |
|------|------|
| Controller | `src/Api/Controllers/FormController.cs` |
| Service | `src/Modules/Workflow/Workflow.Infrastructure/Services/FormMasterFileUploadService.cs` |
| Interface | `src/Modules/Workflow/Workflow.Application/Contracts/IFormMasterFileUploadService.cs` |

---

## 8. Configuration reference

From `appsettings.example.json` / `appsettings.Production.json`:

```json
{
  "PathBase": "/V6API",
  "ApAgent": {
    "Enabled": true,
    "PythonServiceUrl": "http://agents:8000/chat",
    "ApiBaseUrl": "http://localhost/V6API/api/workflows",
    "TimeoutMinutes": 30
  },
  "TenantPilotUser": {
    "Enabled": true,
    "Email": "pilot@ezofis.com",
    "Password": "CHANGE_ME",
    "DisplayName": "AP Agent Pilot",
    "Role": "TenantUser"
  },
  "EzofisBlobStorage": {
    "ConnectionString": "...",
    "ContainerPrefix": "ezts"
  },
  "FormMasterFileImport": {
    "Enabled": true,
    "QueueBlobPathPrefix": "ezPackages/MasterExcel"
  },
  "Repository": {
    "Ocr": {
      "UploadForOcrApiUrl": "http://localhost:8090/EztGetMetadataforV6",
      "OcrType": "ADVANCED",
      "PageNo": "1",
      "ValidateType": "1",
      "TimeoutMinutes": 5
    }
  }
}
```

---

## 9. Pending / next steps

| Item | Status | Notes |
|------|--------|-------|
| Master file row import | **Pending** | Python/data-import worker for `ezPackages/MasterExcel/*.json` |
| Pilot user for existing tenants | Manual | Use Admin `POST /api/users` or migration script |
| Social login ID token validation | Future | Currently trusts frontend OAuth result |
| Hangfire master import job in V6 | Optional | Could replace external worker |

---

## Quick API summary

| Feature | Method | Endpoint |
|---------|--------|----------|
| Upload OCR | `POST` | `/api/uploadAndIndex/uploadForOcr` |
| Stage upload | `POST` | `/api/uploadAndIndex/upload` |
| Index save | `PUT` | `/api/uploadAndIndex/index/{id}` |
| Social login | `POST` | `/api/auth/social/login` |
| Bulk move | `POST` | `/api/workflows/instances/bulk-move-next` |
| Master file upload | `POST` | `/api/form/uploadMasterFile` |
| AP Agent start | `POST` | `/api/workflows/{workflowId}/start` |
| AP Agent status | `GET` | `/api/workflows/ap-agent/jobs/{jobId}` |

---

## Related documentation

- [AP Agent status tracking](./AP_AGENT_STATUS_TRACKING.md) — Python progress callbacks and frontend polling
- [V6 API status](./V6_API_STATUS.md) — broader API coverage tracker (some routes may be outdated vs this doc)
