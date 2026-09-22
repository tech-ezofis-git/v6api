# Raise Ticket from Repository — Frontend (short)

**Audience:** Frontend  
**Date:** 22 Sep 2026  
**Base:** `https://cloud.ezofis.com/api`

Generic flow: raise a normal workflow ticket from a **repository archive file** for **any** `workflowId`. The ticket uses the **caller** `repositoryId` + `itemId` (not the workflow designer repository).

Full guide: [`RAISE_TICKET_FROM_REPOSITORY_FLOW_FRONTEND_GUIDE.md`](./RAISE_TICKET_FROM_REPOSITORY_FLOW_FRONTEND_GUIDE.md).

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
  "repositoryId": "<open-file-repository-guid>",
  "itemId": "<archive-item-guid>",
  "formData": { },
  "fileName": "optional.pdf",
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

`{workflowId}` = any workflow the user selected.

**201** → same as `start/json` (`instanceId`, ticket/reference, etc.).

---

## 2. Open ticket → show the file

1. Mailbox row has `repositoryId` + `itemId` + `activityId`.
2. List also returns `repositoryItem` when those ids are set (file metadata + repo field columns).
3. Use `formData` for the form; `repositoryItem.fields` for archive fields.
4. Optional: `GET /api/workflows/{workflowId}/instances/{instanceId}/attachments`.

---

## 3. Forward (any workflow)

```http
POST /api/workflows/instances/{instanceId}/move-next
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

| After Forward | Folder |
|---------------|--------|
| Recipient | **Inbox** (`action: 1`) |
| Forwarder | **Sent** |
| After complete | **Completed** |

No separate `/forward` API.

---

## 4. Complete / proceed (any workflow)

```json
{
  "activityid": "<from inbox>",
  "review": "<ProceedAction from that step’s designer Rules>",
  "formData": { }
}
```

`review` must match the current step’s ProceedAction (e.g. `Submit`, `Approve`, … — workflow-specific). Not `"Forward"`.

---

## 5. FE checklist

1. File viewer → choose workflow → `raise-ticket` with viewer `repositoryId` + `itemId`.  
2. Do not use designer repository when raising from a file.  
3. Inbox: `repositoryItem` + `formData`; show buttons when `action === 1`.  
4. Forward: `review: "Forward"` + `activityUserId`.  
5. Complete: designer ProceedAction for that step.
