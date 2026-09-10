# AP Agent — Status Tracking APIs

**Audience:** Python (AP Agent service) + Frontend teams  
**Base path:** `/api/workflows`  
**Auth:** JWT Bearer + `X-Tenant-Id` header (or JWT `tid` claim)

**How to call agents `/chat` (OCR + AP curl + Azure URL):** see [CLOUD_OCR_AP_AGENT_CURL.md](./CLOUD_OCR_AP_AGENT_CURL.md).

---

## Quick reference

| Who | Action | Method | Endpoint |
|-----|--------|--------|----------|
| **Python** | **Write / update live progress** | `PATCH` | `/api/workflows/{workflowId}/instances/{instanceId}/ap-agent/progress` |
| **Python** (alt) | Write progress by Hangfire job id | `PATCH` | `/api/workflows/ap-agent/jobs/{jobId}/progress` |
| **Frontend** | **Poll status every ~5 seconds** | `GET` | `/api/workflows/ap-agent/jobs/{jobId}` |

---

## Flow overview

```
Frontend                          .NET API                         Python AP Agent
   |                                 |                                    |
   | POST /workflows/{id}/start      |                                    |
   | (with file) ------------------->| enqueue Hangfire job               |
   |<-- 201 { apAgentJobId } --------|                                    |
   |                                 | POST startPayload ---------------->|
   |                                 |   (includes apAgentProgressUrl,    |
   |                                 |    apAgentJobStatusUrl, jobId)     |
   |                                 |                                    |
   |                                 |<-- PATCH progress (stage/message) -|
   | GET /ap-agent/jobs/{jobId}      |                                    |
   | (every 5 sec) ----------------->| read DB + Hangfire state           |
   |<-- { stage, message, percent } -|                                    |
   |                                 |                                    |
   | stop polling when isTerminal    |                                    |
```

1. Workflow **start with file** (or `POST .../ap-agent/run?background=true`) creates a Hangfire job and returns `apAgentJobId`.
2. .NET sends a `/chat` body (`intent=ap`, mapped `payload`) to the agents service. When `ApAgent:ApiBaseUrl` is configured, the payload includes callback URLs.
3. **Python** calls `PATCH` progress as OCR / extraction / validation steps run.
4. **Frontend** polls `GET` job status every **5 seconds** until `isTerminal` is `true`.

---

## 1. Python — store progress (PATCH)

Use the URL from `payload.apAgentProgressUrl` when present (recommended).  
Otherwise build:

`PATCH /api/workflows/{workflowId}/instances/{instanceId}/ap-agent/progress`

Alternative (when you only have the Hangfire job id):

`PATCH /api/workflows/ap-agent/jobs/{jobId}/progress`

### Headers

Use `pilotAccessToken` from the `/chat` payload when present (recommended). Otherwise login per tenant (see below).

```http
Authorization: Bearer <pilotAccessToken-or-login-jwt>
X-Tenant-Id: <tenant-guid-from-startPayload.tenantId>
Content-Type: application/json
```

**Per-tenant login fallback** (when token is not in payload):

```http
POST /api/auth/ezofis/login
X-Tenant-Id: {tenantId}

{ "email": "pilot@ezofis.com", "password": "<TenantPilotUser.Password>" }
```

Cache JWT per `tenantId` in Python; do not reuse one token across tenants.

### Request body

All fields are optional; send at least `stage` or `message` on each update.

```json
{
  "stage": "OCR_RUNNING",
  "message": "Running OCR on invoice PDF",
  "percent": 25
}
```

### Suggested `stage` values

| Stage | When to send |
|-------|----------------|
| `QUEUED` | Job waiting (usually set by API) |
| `OCR_RUNNING` | OCR in progress |
| `EXTRACTING` | Parsing invoice fields |
| `VALIDATING` | Business-rule / AI validation |
| `PROCESSING` | General processing |
| `COMPLETED` | Finished successfully (`percent`: 100) |
| `FAILED` | Error (put details in `message`) |

### Response

- **204 No Content** — progress saved
- **404** — job / instance not found

### Python example

```python
import requests

def report_progress(progress_url: str, token: str, tenant_id: str,
                    stage: str, message: str, percent: int | None = None):
    headers = {
        "Authorization": f"Bearer {token}",
        "X-Tenant-Id": tenant_id,
        "Content-Type": "application/json",
    }
    body = {"stage": stage, "message": message}
    if percent is not None:
        body["percent"] = percent
    r = requests.patch(progress_url, json=body, headers=headers, timeout=30)
    r.raise_for_status()  # expect 204
```

### Fields injected into `/chat` payload (background jobs)

When the job runs via Hangfire and `ApAgent:ApiBaseUrl` is set (e.g. `https://api.example.com/api/workflows`):

```json
{
  "blobPath": "...",
  "tenantId": "9483F673-416A-43C8-B293-83A72025AF58",
  "workflowId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "instanceId": "f0e1d2c3-b4a5-6789-0123-456789abcdef",
  "apAgentJobId": "12345",
  "apAgentJobStatusUrl": "https://api.example.com/api/workflows/ap-agent/jobs/12345",
  "apAgentProgressUrl": "https://api.example.com/api/workflows/a1b2c3d4-e5f6-7890-abcd-ef1234567890/instances/f0e1d2c3-b4a5-6789-0123-456789abcdef/ap-agent/progress"
}
```

Python should PATCH `apAgentProgressUrl` during processing.

---

## 2. Frontend — poll status every 5 seconds (GET)

`GET /api/workflows/ap-agent/jobs/{jobId}`

### Where to get `jobId`

| Source | Field |
|--------|--------|
| Workflow start (multipart with file) | `apAgentJobId` on **201** response |
| Manual AP Agent run | `apAgentJobId` on **202** from `POST .../ap-agent/run?background=true` |
| Status URL | `statusUrl` on **202** response (relative path) |

### Headers

```http
Authorization: Bearer <jwt>
X-Tenant-Id: <tenant-guid>
```

### Sample response (200)

```json
{
  "jobId": "12345",
  "workflowId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "instanceId": "f0e1d2c3-b4a5-6789-0123-456789abcdef",
  "hangfireStatus": "Processing",
  "stage": "OCR_RUNNING",
  "message": "Running OCR on invoice PDF",
  "percent": 25,
  "errorMessage": null,
  "updatedAtUtc": "2026-06-15T10:30:45.123Z",
  "isTerminal": false
}
```

### `hangfireStatus` values

`Enqueued` | `Processing` | `Succeeded` | `Failed` | `Deleted`

### Polling rules

- Poll every **5 seconds** while `isTerminal` is `false`.
- Stop when `isTerminal` is `true` (`hangfireStatus` is `Succeeded`, `Failed`, or `Deleted`).
- Show UI from `stage`, `message`, and `percent`.
- On failure, show `errorMessage` if present.

### Frontend example (JavaScript)

```javascript
async function pollApAgentStatus(jobId, token, tenantId) {
  const url = `/api/workflows/ap-agent/jobs/${jobId}`;
  const headers = {
    Authorization: `Bearer ${token}`,
    "X-Tenant-Id": tenantId,
  };

  return new Promise((resolve, reject) => {
    const interval = setInterval(async () => {
      try {
        const res = await fetch(url, { headers });
        if (!res.ok) throw new Error(await res.text());
        const status = await res.json();

        // update UI: status.stage, status.message, status.percent
        console.log(status.stage, status.message, status.percent);

        if (status.isTerminal) {
          clearInterval(interval);
          resolve(status);
        }
      } catch (err) {
        clearInterval(interval);
        reject(err);
      }
    }, 5000);
  });
}
```

---

## 3. Related endpoints

| Method | Endpoint | Purpose |
|--------|----------|---------|
| `POST` | `/api/workflows/{workflowId}/instances/{instanceId}/ap-agent/run?background=true` | Start AP Agent in background; returns `apAgentJobId` + `statusUrl` |
| `PATCH` | `/api/workflows/{workflowId}/instances/{instanceId}/ap-agent/metadata` | Apply extracted invoice metadata after Python completes |
| `POST` | `/api/workflows/{id}/start` | Start workflow with file; auto-enqueues AP Agent when file attached |

---

## 4. Configuration (.NET)

`appsettings.json`:

```json
"ApAgent": {
  "Enabled": true,
  "PythonServiceUrl": "http://agents:8000/chat",
  "TimeoutMinutes": 10,
  "ApiBaseUrl": "https://your-api-host/api/workflows",
  "DefaultSkills": [
    "extract_invoice",
    "po_match",
    "duplicate_detect",
    "backorder_detect",
    "finalize_decision",
    "workflow_move_next"
  ]
}
```

Hangfire posts the `/chat` body (`intent=ap`). Pass `skills` on `POST .../ap-agent/run` to override; omit/`[]` for full plan. Full curl samples: [CLOUD_OCR_AP_AGENT_CURL.md](./CLOUD_OCR_AP_AGENT_CURL.md).

`ApiBaseUrl` must be the public base through which Python can reach the progress/status endpoints (used to populate `apAgentProgressUrl` / `apAgentJobStatusUrl` in the payload).

---

## 5. Parallel jobs (multi-tenant)

AP Agent Hangfire jobs **run in parallel** — one job per workflow start, no global queue lock.

| Layer | Concurrency |
|-------|-------------|
| **Hangfire (.NET)** | Up to `Hangfire:WorkerCount` jobs at once (default **10**). Each customer/tenant start gets its own job. |
| **Python** | Must scale separately (e.g. multiple uvicorn/gunicorn workers). Python should accept the POST quickly and process OCR in the background. |

**appsettings:**

```json
"Hangfire": {
  "RunServerInApi": true,
  "WorkerCount": 10
}
```

Increase `WorkerCount` when many customers start workflows at the same time (e.g. 20–50 for heavy load).

**Agents team:** If the `/chat` endpoint blocks until OCR finishes, parallel Hangfire jobs will still pile up waiting on agents. Return **202 Accepted** quickly and run OCR in a worker thread/process.

---

## 6. Swagger

Open Swagger UI (`/swagger` in Development) and look under **Workflows**:

- **PATCH** `.../ap-agent/progress` — sample progress body
- **PATCH** `ap-agent/jobs/{jobId}/progress` — sample progress body
- **GET** `ap-agent/jobs/{jobId}` — sample status response

Use **Try it out** with a real `jobId` from a workflow start response.
