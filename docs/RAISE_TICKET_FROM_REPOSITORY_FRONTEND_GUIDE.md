# Raise Ticket from Repository (Document Approval) — Frontend guide

**Audience:** Frontend  
**Date:** 22 Sep 2026  
**Base:** `https://cloud.ezofis.com/api`

Raise a normal workflow ticket from a **repository archive file**. Works for Document Approval (`workflowId` `1b9d63b9-090f-448d-a379-1cecff1e8c99`) and **any** workflow whose InitiateUsing repository is empty — the ticket uses the **caller** `repositoryId` + `itemId`, not the workflow form repository.

---

## 1. Raise Ticket (from file viewer)

```http
POST /api/workflows/{workflowId}/raise-ticket
Authorization: Bearer <jwt>
X-Tenant-Id: <tenant>
Content-Type: application/json
```

```json
{
  "repositoryId": "b7a11af6-41c3-49e9-9009-988a335b8585",
  "itemId": "050393f8-abbc-4f56-8f3e-8b0df7805d27",
  "formData": { },
  "fileName": "Vessel_Call_Sample_06_GA.pdf",
  "fieldId": "optional-ezfb-file-field-id",
  "context": null,
  "envType": null
}
```

| Field | Required | Notes |
|-------|----------|--------|
| `repositoryId` | Yes | Exact repository of the open file |
| `itemId` | Yes | Archive item id of that file |
| `formData` | No | Same shape as normal workflow start / move-next |
| `fileName` | No | Display name |
| `fieldId` / `formJsonId` | No | ezfb FILE field to store itemId |

**201** → same as `start/json` (`instanceId`, ticket/reference, etc.).

Ticket appears in **Inbox / Sent / Completed** like any other ticket. Attachment stores `repositoryId` + `itemId`.

---

## 2. Open ticket → show the file

1. Mailbox row already has `repositoryId` + `itemId` (and `activityId`).
2. **Inbox / Sent / Completed list** also returns `repositoryItem` when those ids are set:

```json
"repositoryItem": {
  "fileName": "Vessel_Call_Sample_06_GA.pdf",
  "filePath": "...",
  "fileType": "application/pdf",
  "fileSize": 12345,
  "fields": {
    "FileName": "...",
    "InvoiceNumber": "...",
    "...": "all repository archive columns"
  }
}
```

Use `repositoryItem.fields` for repo field values; use `formData` for the workflow form. Preview/download still uses existing repository item APIs with `repositoryId` + `itemId`.

3. Or load attachments:

```http
GET /api/workflows/{workflowId}/instances/{instanceId}/attachments
```

Each attachment includes `repositoryId`, `itemId`, `fileName`, `filePath`.

4. Load / preview the file with existing repository item APIs using those ids (same file as when the ticket was raised).

Form UI = same as normal tickets (`formData` / form entry).

---

## 3. Forward to any tenant user (via move-next)

Use open-step `activityId` from inbox. `review: "Forward"` **reassigns** the open step (does not complete it). `activityUserId` is required.

```http
POST /api/workflows/instances/{instanceId}/move-next
Authorization: Bearer <jwt>
X-Tenant-Id: <tenant>
Content-Type: application/json
```

```json
{
  "activityid": "<from inbox>",
  "review": "Forward",
  "comments": "Please review",
  "activityUserId": "<any-tenant-user-guid>",
  "formData": { }
}
```

| Field | Role |
|-------|------|
| `activityid` | Current open step (from inbox) |
| `activityUserId` | **Required** for Forward — tenant user who receives the open task |
| `review` | `Forward` = reassign only (recipient Inbox + Submit; forwarder Sent). To complete use designer ProceedAction (Document Approval = **`Submit`**) |
| `formData` | Optional; same as normal tickets |

After Forward:
- **Recipient** (`activityUserId`): **Inbox** + Submit (`action: 1`)
- **Forwarder**: removed from Inbox → visible in **Sent** (not hidden)
- After recipient **Submit** / workflow ends → both move to **Completed**

**Complete Document Approval Manual User** (goes to END):

```json
{
  "activityid": "aXDBwy4ARoE3N7DqpIet2",
  "review": "Submit",
  "formData": { }
}
```

No separate `/forward` API — use move-next with `review: "Forward"` + `activityUserId`.

---

## 4. FE checklist

1. Repository file view → **Raise Ticket** → call `raise-ticket` with current `repositoryId`, `itemId`, chosen `workflowId`, optional `formData`.
2. Do **not** read repository from workflow designer when raising from a file.
3. Inbox/Sent/Completed: use `repositoryItem` for repo fields; `formData` for the form; show Submit when `action === 1`.
4. Ticket detail: show form + load file via attachment / `repositoryId`+`itemId`.
5. Forward: `move-next` with `review: "Forward"` + `activityUserId` (recipient Inbox, forwarder Sent).
6. Complete Document Approval: `move-next` with `review: "Submit"`.

Full flow guide: [`DOCUMENT_APPROVAL_RAISE_TICKET_FRONTEND_GUIDE.md`](./DOCUMENT_APPROVAL_RAISE_TICKET_FRONTEND_GUIDE.md).
