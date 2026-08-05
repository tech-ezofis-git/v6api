# Repository Item Timeline & Comments — Frontend Guide

**Audience:** Frontend  
**Last updated:** July 2026

---

## 1. Timeline

```http
GET /api/repositories/{repositoryId}/items/{itemId}/timeline
Authorization: Bearer {jwt}
X-Tenant-Id: {tenantId}
```

Optional share: `?shareToken=` / `?sharedtoken=`

### Response (enriched)

```json
{
  "events": [
    {
      "id": "00000000-0000-0000-0000-000000000000",
      "eventType": "system",
      "title": "Document uploaded manually",
      "description": null,
      "actorType": "User",
      "actorName": "aravinthan.s@ezofis.com",
      "createdAtUtc": "2026-07-27T12:20:00Z",
      "isDerived": true
    },
    {
      "id": "00000000-0000-0000-0000-000000000000",
      "eventType": "ai",
      "title": "OCR extraction complete — 98% confidence",
      "description": null,
      "actorType": "AI Engine",
      "actorName": "AI Engine",
      "createdAtUtc": "2026-07-27T12:21:00Z",
      "isDerived": true
    },
    {
      "id": "00000000-0000-0000-0000-000000000000",
      "eventType": "system",
      "title": "File linked to workflow instance",
      "description": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
      "actorType": "System",
      "actorName": "System",
      "createdAtUtc": "2026-07-27T12:20:30Z",
      "isDerived": true
    },
    {
      "id": "00000000-0000-0000-0000-000000000000",
      "eventType": "workflow",
      "title": "L1 Approval approval granted",
      "description": "Approved",
      "actorType": "User",
      "actorName": "rahul@company.com",
      "createdAtUtc": "2026-07-27T12:30:00Z",
      "isDerived": true
    }
  ],
  "totalCount": 4,
  "linkedWorkflowInstanceId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
  "linkedWorkflowId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
  "linkedWorkflowReferenceNumber": "INV-2026-6001"
}
```

### What FE should show

| `eventType` | Meaning |
|-------------|---------|
| `system` | Ingest (manual upload / email) or linked instance |
| `ai` | OCR % / AI validation |
| `workflow` | Approver / verifier / pending / completed from linked ticket |
| `user` | Manual timeline notes (if posted) |

**Notes**
- `"Metadata updated"` rows are **hidden** (no longer useful for this UI).
- `actorName` / comment `authorName` is the user **email** (falls back to display name only if email missing).
- `isDerived: true` = computed from item / workflow (not a stored note).
- Email ingest titles use **Document ingested via email** (`actorName`: `System (Email)`).
- Manual uploads use **Document uploaded manually**.

---

## 2. Comments (already available — like workflow)

```http
GET /api/repositories/{repositoryId}/items/{itemId}/comments?page=1&pageSize=50
Authorization: Bearer {jwt}
X-Tenant-Id: {tenantId}
```

```json
{
  "comments": [
    {
      "id": "…",
      "body": "Invoice verified against PO-4589.",
      "authorUserId": "…",
      "authorName": "Rahul K.",
      "authorEmail": "rahul@company.com",
      "createdAtUtc": "2026-07-27T12:30:00Z",
      "modifiedAtUtc": null
    }
  ],
  "totalCount": 2,
  "page": 1,
  "pageSize": 50
}
```

```http
POST /api/repositories/{repositoryId}/items/{itemId}/comments
Authorization: Bearer {jwt}
X-Tenant-Id: {tenantId}
Content-Type: application/json
```

```json
{ "body": "Checking with vendor on GST breakup." }
```

**Response `201`**

```json
{
  "commentId": "…",
  "authorName": "Priya M.",
  "authorEmail": "priya@company.com"
}
```

Use Comments tab like the workflow comments panel: list + Post.

---

## 3. Suggested tabs

| Tab | API |
|-----|-----|
| Timeline | `GET …/timeline` |
| Comments (N) | `GET …/comments` → use `totalCount` |
| Related Docs (loose) | `GET …/items/{itemId}/related` — see [Related Documents guide](./REPOSITORY_RELATED_DOCUMENTS_FRONTEND_GUIDE.md) |
| Related Docs (exact) | `GET …/items/{itemId}/related-exact` — all-fields score (same guide) |
