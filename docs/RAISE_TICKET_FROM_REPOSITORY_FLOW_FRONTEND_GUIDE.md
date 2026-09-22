# Raise Ticket from Repository — Generic Frontend Flow

**Audience:** Frontend  
**Date:** 22 Sep 2026  
**Base URL:** `https://cloud.ezofis.com/api` (local: your API host)

Generic flow: start **any** workflow ticket from a **repository archive file**, then Inbox → Forward → complete.

Works for **any** `workflowId`. The ticket binds to the **caller’s** `repositoryId` + `itemId` (especially when the workflow designer InitiateUsing repository is empty). Do **not** hardcode a single workflow name or id in the UI — pass the workflow the user selected.

1. Raise ticket from a repository archive file  
2. Inbox / Sent / Completed (file + repo fields)  
3. Forward to another user  
4. Complete / proceed (designer ProceedAction)

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
   {workflowId} = whatever workflow the user chose
        │
        ▼
 Ticket in Inbox (raiser / assignee)
   row has: repositoryId, itemId, activityId, formData, repositoryItem, action
        │
        ├─ Forward ──► POST .../instances/{instanceId}/move-next
        │                review: "Forward", activityUserId: <user>
        │                Recipient → Inbox (action=1, action buttons visible)
        │                Forwarder → Sent
        │
        └─ Complete ► POST .../instances/{instanceId}/move-next
                         review: <designer ProceedAction for that step>
                         → next step or Completed
```

---

## 1. Raise Ticket (from file viewer)

When the user is viewing a repository archive file and clicks **Raise Ticket** (pick any workflow):

```http
POST /api/workflows/{workflowId}/raise-ticket
```

```json
{
  "repositoryId": "<repository-guid-of-open-file>",
  "itemId": "<archive-item-guid>",
  "formData": {
    "<form-field-guid>": "value"
  },
  "fileName": "sample.pdf",
  "fieldId": "optional-ezfb-file-field-id",
  "context": null,
  "envType": null
}
```

| Field | Required | Notes |
|-------|----------|--------|
| `repositoryId` | **Yes** | Repository of the open file (do **not** use workflow designer repo) |
| `itemId` | **Yes** | Archive item id of that file |
| `formData` | No | Same shape as normal workflow start / move-next (field GUID → value) |
| `fileName` | No | Display name |
| `fieldId` / `formJsonId` | No | ezfb FILE field to store `itemId` |
| `envType` | No | Optional environment hint |

**Path param:** `{workflowId}` = selected workflow (any).

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
| `activityId` | Required for Forward / complete (`move-next`) |
| `workflowInstanceId` | Route id for `move-next` |
| `transactionId` | Optional on move-next |
| `repositoryId` / `itemId` | Preview / download file |
| `formData` | Workflow form values (field GUIDs) |
| `repositoryItem` | Archive file metadata + **repo field columns** |
| `action` | `1` = show proceed buttons; `0` = hide |

### `repositoryItem` (when `repositoryId` + `itemId` set)

```json
"repositoryItem": {
  "fileName": "sample.pdf",
  "filePath": "repository/…/….pdf",
  "fileType": "application/pdf",
  "fileSize": 12345,
  "fields": {
    "FileName": "sample.pdf",
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

Same for every workflow. There is **no** separate `/forward` API. Use **move-next** with `review: "Forward"`.

```http
POST /api/workflows/instances/{instanceId}/move-next
```

```json
{
  "activityid": "<from inbox row>",
  "review": "Forward",
  "comments": "Please review",
  "activityUserId": "<target-tenant-user-guid>",
  "workflowId": "<same-workflow-guid>",
  "transactionId": "<optional from inbox>",
  "repositoryId": "<optional>",
  "itemId": "<optional>",
  "formId": "<optional>",
  "formEntryId": "<optional>",
  "formData": { }
}
```

| Field | Required | Notes |
|-------|----------|--------|
| `activityid` | **Yes** | Open step from **Inbox** |
| `activityUserId` | **Yes** for Forward | Target **tenant user** guid |
| `review` | **Yes** | Must be `"Forward"` (case-insensitive) |
| `formData` | No | Same as normal tickets |

### Mailbox after Forward

| Who | Folder | Action buttons |
|-----|--------|----------------|
| **Recipient** (`activityUserId`) | **Inbox** | Yes (`action: 1`) |
| **Forwarder** | **Sent** (removed from Inbox) | No |
| After recipient completes the step / workflow | Both → **Completed** (when workflow ends) | — |

**Important:** `"Forward"` is **not** a designer ProceedAction. It only reassigns the open step. Do **not** use `"Forward"` to finish the ticket.

---

## 4. Complete / proceed (any workflow)

Use **move-next** with the open step’s designer **ProceedAction** (from workflow Rules for that `activityId`).

```http
POST /api/workflows/instances/{instanceId}/move-next
```

```json
{
  "activityid": "<from inbox>",
  "review": "<ProceedAction label from designer>",
  "comments": "optional",
  "formData": { }
}
```

| Field | Notes |
|-------|--------|
| `review` | Must match a **ProceedAction** on the current step (e.g. `Submit`, `Approve`, `Reject`, `Matched`, … — whatever the designer defined) |
| `activityUserId` | Optional; used when assigning the **next** step, not for Forward |

After a completing ProceedAction → next step Inbox / or **Completed** when the workflow ends. Users who had the ticket in Sent (after Forward) also move to **Completed** when the instance completes.

**How FE knows valid `review` values:** use the workflow definition / step Rules (`ProceedAction`) for the current `activityId`. Do not assume one label for all workflows.

---

## 5. FE checklist

1. File viewer → user picks a **workflow** → **Raise Ticket** → `POST .../workflows/{workflowId}/raise-ticket` with viewer `repositoryId`, `itemId`, optional `formData`.  
2. Do **not** take repository from workflow designer when raising from a file.  
3. Inbox list: file via `repositoryId` / `itemId` / `repositoryItem`; form via `formData`.  
4. Show proceed buttons when `action === 1`.  
5. **Forward** → `move-next` with `review: "Forward"` + `activityUserId`.  
6. **Complete** → `move-next` with that step’s designer ProceedAction (not `"Forward"`).  
7. After Forward: recipient → **Inbox**; forwarder → **Sent**; after workflow complete → **Completed**.

---

## 6. Common mistakes

| Mistake | Result |
|---------|--------|
| `review: "Forward"` without `activityUserId` | Error — Forward requires target user |
| `review: "Forward"` expecting ticket to complete | Wrong — Forward only reassigns |
| Hardcoding one ProceedAction for every workflow | Error — use each step’s designer Rules |
| Using workflow designer `repositoryId` on raise | Wrong file / empty repo — always pass viewer’s ids |
| Looking for ticket in Inbox after you forwarded it | You are on **Sent**; recipient is on **Inbox** |

---

## Related APIs

- `POST /api/workflows/{workflowId}/raise-ticket`  
- `POST /api/workflows/instances/{instanceId}/move-next`  
- Mailbox list (existing Inbox / Sent / Completed)  
- Short summary: [`RAISE_TICKET_FROM_REPOSITORY_FRONTEND_GUIDE.md`](./RAISE_TICKET_FROM_REPOSITORY_FRONTEND_GUIDE.md)
