# Document Approval — Raise Ticket, Inbox, Forward, Submit (Frontend)

**Audience:** Frontend  
**Date:** 22 Sep 2026  
**Base URL:** `https://cloud.ezofis.com/api` (local: your API host)

This guide covers the full **repository file → Document Approval ticket** flow:

1. Raise ticket from a repository archive file  
2. Inbox / Sent / Completed (file + repo fields)  
3. Forward to another user  
4. Submit / complete  

**Example workflow:** Document Approval  
`workflowId` = `1b9d63b9-090f-448d-a379-1cecff1e8c99`

Works for **any** workflow whose designer InitiateUsing `repositoryId` is empty — the ticket binds to the **caller’s** `repositoryId` + `itemId`.

---

## Headers (all calls)

```http
Authorization: Bearer <jwt>
X-Tenant-Id: <tenant-guid>
Content-Type: application/json
```

---

## End-to-end flow

```text
Repository file viewer
        │
        ▼
 POST /api/workflows/{workflowId}/raise-ticket
   body: repositoryId, itemId, formData?
        │
        ▼
 Ticket in Inbox (raiser / assignee)
   row has: repositoryId, itemId, activityId, formData, repositoryItem
        │
        ├─ Forward ──► POST .../instances/{instanceId}/move-next
        │                review: "Forward", activityUserId: <user>
        │                Recipient → Inbox (action=1, Submit visible)
        │                Forwarder → Sent
        │
        └─ Submit ───► POST .../instances/{instanceId}/move-next
                         review: "Submit"   ← Document Approval ProceedAction
                         → Completed (forwarder + recipient)
```

---

## 1. Raise Ticket (from file viewer)

When the user is viewing a repository archive file and clicks **Raise Ticket**:

```http
POST /api/workflows/{workflowId}/raise-ticket
```

```json
{
  "repositoryId": "a6169a5c-1468-4fb5-90a9-220082a89f2a",
  "itemId": "dde71fe2-8ce9-47d9-8a02-74fd514d03aa",
  "formData": {
    "03c459cb-404e-4acc-a897-3c61e1c8a44f": "value",
    "70f625fb-ca6e-4f43-b377-ebffbc402592": "2026-09-03"
  },
  "fileName": "sample.pdf",
  "fieldId": "optional-ezfb-file-field-id",
  "context": null,
  "envType": "trial"
}
```

| Field | Required | Notes |
|-------|----------|--------|
| `repositoryId` | **Yes** | Repository of the open file (do **not** use workflow designer repo) |
| `itemId` | **Yes** | Archive item id of that file |
| `formData` | No | Same shape as normal workflow start / move-next (field GUID → value) |
| `fileName` | No | Display name |
| `fieldId` / `formJsonId` | No | ezfb FILE field to store `itemId` |
| `envType` | No | e.g. `trial` |

**Response:** `201` — same shape as `start/json`, including:

- `instanceId`
- `formEntryId`
- `startPayload` / `formDataJson` (includes `repositoryId`, `itemId`, blob path, etc.)

Ticket appears in mailbox like any other workflow ticket. The archive file is linked via `repositoryId` + `itemId`.

---

## 2. Inbox / Sent / Completed list

Use existing mailbox list APIs (same as other workflows).

Each row with a linked file includes:

| Field | Use |
|-------|-----|
| `activityId` | Required for Forward / Submit (`move-next`) |
| `workflowInstanceId` | Route id for `move-next` |
| `transactionId` | Optional on move-next |
| `repositoryId` / `itemId` | Preview / download file |
| `formData` | Workflow form values (field GUIDs) |
| `repositoryItem` | Archive file metadata + **repo field columns** |
| `action` | `1` = show Submit / approve buttons; `0` = hide |

### `repositoryItem` (when `repositoryId` + `itemId` set)

```json
"repositoryItem": {
  "fileName": "sample.pdf",
  "filePath": "repository/…/….pdf",
  "fileType": "application/pdf",
  "fileSize": 12345,
  "fields": {
    "FileName": "sample.pdf",
    "InvoiceNumber": "…",
    "…": "all repository archive columns for that item"
  }
}
```

- **Form UI** → `formData`  
- **Repo side panel / index fields** → `repositoryItem.fields`  
- **Preview / download** → existing repository item APIs with `repositoryId` + `itemId`

### Attachments (optional)

```http
GET /api/workflows/{workflowId}/instances/{instanceId}/attachments
```

Each attachment includes `repositoryId`, `itemId`, `fileName`, `filePath`.

---

## 3. Forward (reassign open step)

There is **no** separate `/forward` API. Use **move-next** with `review: "Forward"`.

```http
POST /api/workflows/instances/{instanceId}/move-next
```

```json
{
  "activityid": "<from inbox row>",
  "review": "Forward",
  "comments": "Please review",
  "activityUserId": "d642d729-afae-4eca-8862-b69282472690",
  "workflowId": "1b9d63b9-090f-448d-a379-1cecff1e8c99",
  "transactionId": "<optional from inbox>",
  "repositoryId": "a6169a5c-1468-4fb5-90a9-220082a89f2a",
  "itemId": "dde71fe2-8ce9-47d9-8a02-74fd514d03aa",
  "formId": "1c075a09-98b0-4560-9ab8-365399c22496",
  "formEntryId": "e9b32c63-d0f2-4101-ad1c-fe185ba90053",
  "formData": { }
}
```

| Field | Required | Notes |
|-------|----------|--------|
| `activityid` | **Yes** | Open step from **Inbox** (not from raise-ticket response alone) |
| `activityUserId` | **Yes** for Forward | Target **tenant user** guid |
| `review` | **Yes** | Must be exactly `"Forward"` (case-insensitive) |
| `formData` | No | Same as normal tickets |

### Mailbox after Forward

| Who | Folder | Submit button |
|-----|--------|----------------|
| **Recipient** (`activityUserId`) | **Inbox** | Yes (`action: 1`) |
| **Forwarder** | **Sent** (removed from Inbox) | No |
| After recipient completes | Both → **Completed** | — |

**Important:** `"Forward"` is **not** a designer ProceedAction. It only reassigns the open step. Do **not** use `"Forward"` when you want to finish the ticket.

---

## 4. Submit / complete (Document Approval)

Document Approval **Manual User** step only allows ProceedAction **`Submit`** (routes to END).

```http
POST /api/workflows/instances/{instanceId}/move-next
```

```json
{
  "activityid": "aXDBwy4ARoE3N7DqpIet2",
  "review": "Submit",
  "comments": "Approved",
  "formData": {
    "03c459cb-404e-4acc-a897-3c61e1c8a44f": "value",
    "70f625fb-ca6e-4f43-b377-ebffbc402592": "2026-09-03"
  }
}
```

| Field | Notes |
|-------|--------|
| `review` | Must match designer rule: Document Approval = **`Submit`** (not `Approve` / `Forward`) |
| `activityUserId` | Not required for Submit |

After Submit → ticket moves to **Completed** for participants (including who had it in Sent after Forward).

---

## 5. FE checklist

1. File viewer → **Raise Ticket** → `POST .../raise-ticket` with current `repositoryId`, `itemId`, `workflowId`, optional `formData`.  
2. Do **not** take repository from workflow designer when raising from a file.  
3. Inbox list: show file via `repositoryId`/`itemId`/`repositoryItem`; form via `formData`.  
4. Show Submit when `action === 1`.  
5. **Forward** → `move-next` with `review: "Forward"` + `activityUserId`.  
6. **Complete** → `move-next` with designer ProceedAction (`Submit` for Document Approval).  
7. After Forward: recipient checks **Inbox**; forwarder checks **Sent**; after Submit both check **Completed**.

---

## 6. Common mistakes

| Mistake | Result |
|---------|--------|
| `review: "Forward"` without `activityUserId` | Error — Forward requires target user |
| `review: "Forward"` expecting ticket to complete | Wrong — Forward only reassigns; use `Submit` to complete |
| `review: "Approve"` on Document Approval Manual User | Error — only `Submit` is a valid ProceedAction |
| Using workflow designer `repositoryId` on raise | Wrong file / empty repo — always pass viewer’s ids |
| Looking for ticket in Inbox after you forwarded it | You are on **Sent**; recipient is on **Inbox** |

---

## Related

- API: `POST /api/workflows/{workflowId}/raise-ticket`  
- API: `POST /api/workflows/instances/{instanceId}/move-next`  
- Also see: `docs/RAISE_TICKET_FROM_REPOSITORY_FRONTEND_GUIDE.md` (shorter summary)
