# Mailbox by activity — frontend guide

**Audience:** Frontend  
**Endpoint:** `POST /api/workflows/by-activity`  
**Auth:** JWT and tenant header, same as inbox and sent.

This call returns the current user's inbox or sent row for the activity ids you send. The item shape is the same as `GET /api/workflows/inbox` and `GET /api/workflows/sent`. It does not list the whole folder, and it does not look in completed.

---

## 1. Headers

| Header | Required |
|--------|----------|
| `Authorization: Bearer <jwt>` | Yes |
| `X-Tenant-Id: <tenant-guid>` | Yes |
| `Content-Type: application/json` | Yes |

---

## 2. Request

```http
POST /api/workflows/by-activity
```

| Field | Required | What to send |
|-------|----------|----------------|
| `workflowId` | Yes | Workflow guid. |
| `activityIds` | Yes | Activity ids from the inbox or sent row (`activityId`). 1 to 100 ids. |

Blank ids are ignored. The same id sent twice is looked up once. Matching is case-insensitive.

```json
{
  "workflowId": "1325cf43-3902-426d-800f-ffde5bbfe0da",
  "activityIds": [
    "activity-id-in-inbox",
    "activity-id-in-sent"
  ]
}
```

Use the `activityId` already returned on inbox and sent rows. This is the workflow activity id, not the mailbox row `id` and not the instance id.

---

## 3. Which row comes back

| Where the activity id is | Result |
|--------------------------|--------|
| Inbox for this user | That inbox row. `mailbox` is `"inbox"`. |
| Sent only | That sent row. `mailbox` is `"sent"`. |
| Both inbox and sent | The inbox row. |
| Neither, or not visible to this user | Omitted from `items`. |

Visibility is the same as the inbox and sent lists: the signed-in user must be the assignee, or a member of the assigned group. Sent also includes rows this user created.

---

## 4. Response `200`

```json
{
  "items": [
    {
      "id": 1204,
      "workflowId": "1325cf433902426d800fffde5bbfe0da",
      "workflowInstanceId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
      "activityId": "activity-id-in-inbox",
      "transactionId": "88",
      "name": "RFQ",
      "stage": "Review",
      "stageType": "USER",
      "formId": "787372d8-d66a-47f6-ba3f-36cb6dffb360",
      "formEntryId": "entry-guid",
      "formData": {},
      "repositoryId": null,
      "itemId": null,
      "repositoryItem": null,
      "action": 1,
      "mailbox": "inbox"
    }
  ],
  "totalCount": 1,
  "pageNumber": 1,
  "pageSize": 2,
  "tableExists": true
}
```

| Field | Use |
|-------|-----|
| `items` | Same fields as inbox and sent. Render with the existing mailbox row component. |
| `mailbox` | `"inbox"` or `"sent"`. Present only on this call. Use it to choose the inbox or sent action. |
| `formData` | Form values, same as inbox and sent. |
| `repositoryItem` | Attached archive file when `repositoryId` and `itemId` are set. |
| `action` | `1` show verify/approve. `0` hide those buttons. |
| `totalCount` | How many of the requested ids were found. |
| `pageSize` | How many distinct ids were requested. |
| `tableExists` | `false` when this workflow has no inbox table and no sent table. `items` is then empty. |

Rows are returned in the same order as `activityIds`. An id that was not found is skipped, so `items.length` can be smaller than `activityIds.length`.

---

## 5. Errors `400`

```json
{ "error": "activityIds is required." }
```

| `error` | Cause |
|---------|--------|
| `workflowId is required.` | `workflowId` is missing or an empty guid. |
| `activityIds is required.` | `activityIds` is missing, empty, or only blank strings. |
| `activityIds cannot exceed 100.` | More than 100 ids after blanks and duplicates are removed. |
