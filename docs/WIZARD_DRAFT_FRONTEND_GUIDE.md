# Wizard Draft APIs ΓÇö Folder Creation & User Creation

**Audience:** Frontend  
**Last updated:** August 2026

Same pattern for both wizards: save progress after **every step** as JSON so if the network drops, resume from the last saved step instead of starting over.

Auth for both: `Authorization: Bearer {jwt}` + `X-Tenant-Id`.

| Wizard | Base path |
|--------|-----------|
| Create Folder | `/api/folder-creation/drafts` |
| Create User | `/api/user-creation/drafts` |

One **active** draft per `tenantId` + `userId` per wizard (incomplete, not deleted). Save upserts that row.

---

## Shared APIs (same for both)

Replace `{base}` with the wizard base path above.

| Use case | Method | Endpoint |
|----------|--------|----------|
| Save after each step | `PUT` or `POST` | `{base}` |
| Resume (get active draft) | `GET` | `{base}` |
| Get by id | `GET` | `{base}/{draftId}` |
| Mark done after create | `POST` | `{base}/{draftId}/complete` |
| Discard draft | `DELETE` | `{base}/{draftId}` |

### Shared request body (save)

```json
{
  "tenantId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
  "userId": "bbbbbbbb-cccc-dddd-eeee-ffffffffffff",
  "draftId": null,
  "currentStep": 2,
  "currentStepKey": "ΓÇª",
  "draftJson": "{ΓÇªfull wizard state so farΓÇª}"
}
```

| Field | Required | Notes |
|-------|----------|--------|
| `tenantId` | Yes* | Empty GUID ΓåÆ JWT tenant |
| `userId` | Yes* | Empty GUID ΓåÆ JWT user |
| `currentStep` | **Yes** | 1ΓÇô5 |
| `currentStepKey` | No | Defaults from step number |
| `draftJson` | Recommended | `JSON.stringify(state)` ΓÇö full form so far |
| `draftId` | No | Pass after first save to update same row |

### Shared response `200`

```json
{
  "id": "11111111-2222-3333-4444-555555555555",
  "tenantId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
  "userId": "bbbbbbbb-cccc-dddd-eeee-ffffffffffff",
  "currentStep": 2,
  "currentStepKey": "ΓÇª",
  "draftJson": "{ΓÇª}",
  "createdAtUtc": "2026-08-27T10:00:00Z",
  "modifiedAtUtc": "2026-08-27T10:05:00Z",
  "isCompleted": false
}
```

Keep returned `id` as `draftId` for later saves.

### Resume

```http
GET {base}
Authorization: Bearer {jwt}
X-Tenant-Id: {tenantId}
```

- `200` ΓåÆ jump UI to `currentStep`, hydrate with `JSON.parse(draftJson)`
- `404` ΓåÆ start at step 1

Optional query: `?tenantId=&userId=` (defaults from JWT).

### Complete / discard

```http
POST {base}/{draftId}/complete
DELETE {base}/{draftId}
```

### Shared FE flow

```text
Open wizard:
  ΓåÆ GET {base}
  ΓåÆ 200: resume at currentStep + parse draftJson
  ΓåÆ 404: start step 1

After each Continue:
  ΓåÆ PUT {base}
     { tenantId, userId, draftId?, currentStep, currentStepKey, draftJson }
  ΓåÆ keep returned id as draftId

Network cut mid-wizard:
  ΓåÆ reopen ΓåÆ GET ΓåÆ restore from currentStep + draftJson

Final create OK:
  ΓåÆ POST {base}/{draftId}/complete
```

### Shared errors

| Status | When |
|--------|------|
| `400` | Invalid step / missing user |
| `401` | Missing / invalid token |
| `404` | No active draft / unknown id |

---

## 1. Create Folder

**Base:** `/api/folder-creation/drafts`

### Steps

| Step | `currentStep` | `currentStepKey` |
|------|---------------|------------------|
| Folder Details | `1` | `folderDetails` |
| Fields | `2` | `fields` |
| Storage | `3` | `storage` |
| Versioning | `4` | `versioning` |
| Integrations | `5` | `integrations` |

### Save example (after Fields)

```http
PUT /api/folder-creation/drafts
Authorization: Bearer {jwt}
X-Tenant-Id: {tenantId}
Content-Type: application/json
```

```json
{
  "tenantId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
  "userId": "bbbbbbbb-cccc-dddd-eeee-ffffffffffff",
  "draftId": null,
  "currentStep": 2,
  "currentStepKey": "fields",
  "draftJson": "{\"folderDetails\":{\"name\":\"AP Invoices\",\"description\":\"ΓÇª\"},\"fields\":[{\"name\":\"Cost Center\",\"type\":\"SHORT_TEXT\",\"folder\":true,\"mandatory\":false}]}"
}
```

### Suggested `draftJson` shape

```json
{
  "folderDetails": {
    "name": "AP Invoices",
    "description": "Accounts payable folder"
  },
  "fields": [
    {
      "name": "Cost Center",
      "type": "SHORT_TEXT",
      "folder": true,
      "mandatory": false
    }
  ],
  "storage": {
    "storageProviderCode": "EZOFIS",
    "storageDrive": null
  },
  "versioning": {
    "strategy": "majorMinor"
  },
  "integrations": {
    "erp": null,
    "syncMapping": []
  }
}
```

---

## 2. Create User

**Base:** `/api/user-creation/drafts`

**Security:** API **strips** `password` / `confirmPassword` / similar keys from `draftJson` before save. On resume, re-enter password on Login Details if login type is Password.

### Steps

| Step | `currentStep` | `currentStepKey` |
|------|---------------|------------------|
| Login Details | `1` | `loginDetails` |
| Business Detail | `2` | `businessDetail` |
| Group Assignment | `3` | `groupAssignment` |
| Authentication | `4` | `authentication` |
| Review | `5` | `review` |

### Save example (after Login Details)

```http
PUT /api/user-creation/drafts
Authorization: Bearer {jwt}
X-Tenant-Id: {tenantId}
Content-Type: application/json
```

```json
{
  "tenantId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
  "userId": "bbbbbbbb-cccc-dddd-eeee-ffffffffffff",
  "draftId": null,
  "currentStep": 1,
  "currentStepKey": "loginDetails",
  "draftJson": "{\"loginDetails\":{\"firstName\":\"Arun\",\"lastName\":\"Kumar\",\"email\":\"arun@company.com\",\"username\":\"arun.k\",\"phoneCode\":\"+91\",\"phoneNumber\":\"9876543210\",\"loginType\":\"Password\"}}"
}
```

### Suggested `draftJson` shape

```json
{
  "loginDetails": {
    "firstName": "Arun",
    "lastName": "Kumar",
    "email": "arun@company.com",
    "username": "arun.k",
    "phoneCode": "+91",
    "phoneNumber": "9876543210",
    "loginType": "Password"
  },
  "businessDetail": {
    "departmentId": "ΓÇª",
    "businessUnitId": "ΓÇª"
  },
  "groupAssignment": {
    "groupIds": ["ΓÇª"]
  },
  "authentication": {
    "mfaEnabled": false
  },
  "review": {
    "confirmed": true
  }
}
```

Do **not** put `password` in `draftJson`.

---

## Quick compare

| | Folder creation | User creation |
|--|-----------------|---------------|
| Base URL | `/api/folder-creation/drafts` | `/api/user-creation/drafts` |
| Step 1 key | `folderDetails` | `loginDetails` |
| Step 2 key | `fields` | `businessDetail` |
| Step 3 key | `storage` | `groupAssignment` |
| Step 4 key | `versioning` | `authentication` |
| Step 5 key | `integrations` | `review` |
| Password in draft | N/A | Stripped / omit |
