# Report Builder ΓÇö Frontend Integration Guide

**Audience:** Frontend team  
**Base path:** `/api/report-builder`  
**Auth:** JWT + `X-Tenant-Id` on every call

Live example (IIS PathBase): `https://demo.ezofis.com/V6Api/api/report-builder`

---

## 1. Common headers

```http
Authorization: Bearer {jwt_token}
X-Tenant-Id: {tenant_guid}
Content-Type: application/json
```

If the API uses PathBase `/V6Api`, the full URL is:

```text
{host}/V6Api/api/report-builder/...
```

JSON is **camelCase**.

---

## 2. Suggested UI flow

```
1. Dashboard          GET  /api/report-builder
2. Source Type        (Workflow | Repository | Form) ΓÇö client only for now
3. Source Form        GET  /api/report-builder/forms
4. Fields             GET  /api/report-builder/fields?sourceFormId={formId}
5. Live preview       POST /api/report-builder/preview
6. Save (draft)       POST /api/report-builder
7. Publish            POST /api/report-builder/{id}/publish
8. Open saved report  GET  /api/report-builder/{id}        ΓåÉ JSON config
                      GET  /api/report-builder/{id}/data   ΓåÉ columns + rows
9. Run / download     POST /api/report-builder/{id}/run
```

Optional wizard autosave (resume after network drop): `/api/report-builder/drafts` (see section 14).

**Important**

| UI control | Send this | Do not use this as the data table |
|------------|-----------|-----------------------------------|
| Source Type | `sourceType` (`Workflow` / `Repository` / `Form`) | ΓÇö |
| Source Form dropdown | `sourceForm` (name) **and** `sourceFormId` (`formId` from `/forms`) | workflow id |
| Domain (optional) | `domain` = workflow **name** | workflow GUID as form table |

Form data comes from `dbo.ezfb_{first 8 chars of sourceFormId}_items`.  
The workflow is only used for process-form join / schedule context.

---

## 3. List reports (dashboard)

```http
GET /api/report-builder
GET /api/report-builder?search=vessel&status=Published&scheduled=true&domain=Vessel%20Call
```

### Query

| Param | Type | Notes |
|-------|------|--------|
| `search` | string | Matches report name |
| `status` | string | `Draft` or `Published` |
| `scheduled` | bool | `true` / `false` |
| `domain` | string | Workflow name filter |

### Output `200`

```json
[
  {
    "id": "e7e5eed7-10a0-4036-a150-641e67734d5e",
    "name": "Vessel Call Report",
    "domain": "Vessel Call",
    "status": "Published",
    "scheduled": true,
    "owner": "You",
    "runs": 3,
    "modified": "2026-09-08T09:15:00Z",
    "sourceType": "Workflow",
    "sourceFormId": "ad061183-cac7-4fbf-b4ca-d853b07fc350",
    "workflowId": "b05ed64d-7c94-4ecd-a5b0-caa5839757f9",
    "visibility": "Selected Users",
    "canEdit": true,
    "access": "owner",
    "sharedUsers": [
      "c0e0d641-1e88-4fd1-ab51-d61040f607ce",
      "220f4440-b8c5-4737-b094-11059e98244b"
    ]
  }
]
```

`GET /api/report-builder` is **per logged-in user**:

| Logged in as | Report appears? |
|--------------|-----------------|
| Owner who created it | Yes |
| Admin | Yes (all reports) |
| User id in `sharedUsers` | Yes (`canEdit: false`) |
| Anyone else | **No** ΓÇö omitted from the list. `GET /{id}` and `GET /{id}/data` return `404` |

Shared users see the same row with `"canEdit": false`, `"access": "shared"`, and `"owner"` as the creator's name (not `"You"`).

---

## 3.1 Share with selected users (Admin / owner)

Admin (or the report owner) picks users on create/update. **Only those users** plus the owner and Admin can see the report.

User picker: `GET /api/users` (JWT + `X-Tenant-Id`). Send each user's `id`.

### Create / update body (share fields)

```json
{
  "visibility": "Selected Users",
  "sharedUsers": [
    "c0e0d641-1e88-4fd1-ab51-d61040f607ce",
    "220f4440-b8c5-4737-b094-11059e98244b"
  ],
  "sharedGroups": [],
  "sourceId": "b05ed64d-7c94-4ecd-a5b0-caa5839757f9"
}
```

| Field | Who | Notes |
|-------|-----|--------|
| `visibility` | `"Private"` \| `"Selected Users"` \| `"Selected Groups"` | If you send `sharedUsers` and leave visibility empty/`Private`, the API sets `"Selected Users"` |
| `sharedUsers` | user GUIDs | The only non-admin, non-owner people who can open the report |
| `sharedGroups` | group GUIDs | Same, for `"Selected Groups"` |
| `sourceId` | workflow GUID | Optional alias of `workflowId` |

`schedule.recipients` / `cc` can be the same user GUIDs (email is resolved on send). Sharing and email lists are separate: a user can be emailed without being a viewer, and a viewer is not emailed unless they are also in `recipients`/`cc`.

### Who can do what

| Action | Owner | Admin | Shared user |
|--------|-------|-------|-------------|
| `GET /api/report-builder` (see it on the dashboard) | yes | all reports | only reports shared with them |
| `GET /{id}` config | yes | yes | **view only** (`canEdit: false`) |
| `GET /{id}/data` grid | yes | yes | yes |
| `POST /{id}/run` (view / download / email) | yes | yes | yes |
| `PUT /{id}` edit | yes | yes | **403** |
| `POST /{id}/publish` | yes | yes | **403** |
| `DELETE /{id}` | yes | yes | **403** |

Shared-user `GET /{id}` example:

```json
{
  "id": "e7e5eed7-10a0-4036-a150-641e67734d5e",
  "name": "sample",
  "visibility": "Selected Users",
  "sharedUsers": [
    "c0e0d641-1e88-4fd1-ab51-d61040f607ce",
    "220f4440-b8c5-4737-b094-11059e98244b"
  ],
  "canEdit": false,
  "access": "shared",
  "owner": "Kenneth"
}
```

Frontend: hide Edit / Delete / Publish / Share when `canEdit` is `false`. Show the grid from `GET /{id}/data`.

If a shared user is not on `sharedUsers`, list omits the report and `GET /{id}` returns `404`.

---

## 4. Domains (workflow dropdown ΓÇö optional)

```http
GET /api/report-builder/domains
```

### Output `200`

```json
[
  {
    "workflowId": "b05ed64d-7c94-4ecd-a5b0-caa5839757f9",
    "name": "Vessel Call",
    "formId": "ad061183-cac7-4fbf-b4ca-d853b07fc350",
    "description": null
  }
]
```

Use `name` as `domain` when saving. Prefer **Source Form** (`/forms`) for the actual data table.

---

## 5. Source Form dropdown

```http
GET /api/report-builder/forms
GET /api/report-builder/forms?sourceType=Workflow
```

`sourceType` is accepted but currently all `dbo.wForm` rows are returned.

### Output `200`

```json
[
  {
    "formId": "ad061183-cac7-4fbf-b4ca-d853b07fc350",
    "formName": "Vessel Call Form",
    "type": "Workflow"
  }
]
```

Store both:

- `sourceFormId` = `formId`
- `sourceForm` = `formName`

---

## 6. Fields (Fields step)

```http
GET /api/report-builder/fields?sourceFormId=ad061183-cac7-4fbf-b4ca-d853b07fc350
GET /api/report-builder/fields?sourceForm=Vessel%20Call%20Form&sourceType=Workflow
GET /api/report-builder/fields?domain=Vessel%20Call
```

Prefer `sourceFormId` (GUID from `/forms`).

### Output `200`

```json
[
  {
    "id": "customer",
    "label": "Customer",
    "type": "lookup",
    "isMandatory": true
  },
  {
    "id": "imoNumber",
    "label": "IMO Number",
    "type": "text",
    "isMandatory": false
  }
]
```

| Field | Use |
|-------|-----|
| `id` | Put in `fields[]` and as the key in `fieldSettings` |
| `label` | Column header shown in the grid |
| `type` | UI hint (`text`, `lookup`, `number`, ΓÇª) |
| `isMandatory` | Optional badge |

### Error `400`

```json
{ "error": "Source Form 'ΓÇª' (formId=ΓÇª) has no data table dbo.ezfb_ΓÇªΓÇª_items. ΓÇª" }
```

---

## 7. Preview (no save)

```http
POST /api/report-builder/preview
```

### Input

Same body as save (section 8). `id` can be empty.

```json
{
  "name": "Vessel Call Report",
  "domain": "Vessel Call",
  "sourceType": "Workflow",
  "sourceForm": "Vessel Call Form",
  "sourceFormId": "ad061183-cac7-4fbf-b4ca-d853b07fc350",
  "fields": ["customer", "imoNumber", "voyageNumber"],
  "fieldSettings": {
    "customer": { "label": "Customer", "width": 180, "calc": "None", "colType": "value" },
    "imoNumber": { "label": "IMO Number", "width": 140, "calc": "None", "colType": "value" },
    "voyageNumber": { "label": "Voyage Number", "width": 140, "calc": "None", "colType": "value" }
  },
  "filters": []
}
```

### Output `200` ΓÇö `ReportRunResult`

```json
{
  "reportId": null,
  "name": "Vessel Call Report",
  "domain": "Vessel Call",
  "columns": [
    { "key": "Customer", "label": "Customer", "calc": "None", "colType": "value", "width": 180, "isCalculated": false },
    { "key": "IMO Number", "label": "IMO Number", "calc": "None", "colType": "value", "width": 140, "isCalculated": false },
    { "key": "Voyage Number", "label": "Voyage Number", "calc": "None", "colType": "value", "width": 140, "isCalculated": false }
  ],
  "rows": [
    {
      "Customer": "Maersk",
      "IMO Number": "9123456",
      "Voyage Number": "AE-102"
    }
  ],
  "totals": null,
  "rowCount": 1,
  "workflowId": "b05ed64d-7c94-4ecd-a5b0-caa5839757f9",
  "sourceFormId": "ad061183-cac7-4fbf-b4ca-d853b07fc350"
}
```

**Grid binding:** `rows[]` keys are the **column labels**, not GUIDs and not `fields[].id`.

```js
columns.map(c => row[c.label] ?? row[c.key] ?? "")
```

Empty `rows` + `rowCount: 0` is a valid `200` (no matching form items).

---

## 8. Create report

```http
POST /api/report-builder
```

Default `status` is `Draft` if omitted. Hangfire email is registered only when `status = Published` **and** `scheduled = true` **and** there is at least one recipient.

### Full input

```json
{
  "name": "Vessel Call Report",
  "description": "Weekly vessel movements",
  "domain": "Vessel Call",
  "sourceType": "Workflow",
  "sourceForm": "Vessel Call Form",
  "sourceFormId": "ad061183-cac7-4fbf-b4ca-d853b07fc350",
  "workflowId": null,
  "fields": ["customer", "imoNumber", "voyageNumber"],
  "fieldSettings": {
    "customer": {
      "label": "Customer",
      "width": 180,
      "calc": "None",
      "colType": "value"
    },
    "imoNumber": {
      "label": "IMO Number",
      "width": 140,
      "calc": "None",
      "colType": "value"
    }
  },
  "filters": [
    {
      "id": "f1",
      "field": "Customer",
      "fieldId": "customer",
      "operator": "equals",
      "value": "Maersk"
    }
  ],
  "customFields": [
    {
      "id": "totalAmount",
      "label": "Total Amount",
      "formula": "[Amount] * [Qty]",
      "calc": "Sum",
      "colType": "calculated",
      "width": 140
    }
  ],
  "schedule": {
    "recurrence": "Weekly",
    "day": "Monday",
    "time": "09:00",
    "timezone": "India Standard Time",
    "format": "PDF",
    "recipients": ["ops@example.com"],
    "cc": [],
    "subject": "Vessel Call Report",
    "message": "Please find the attached report."
  },
  "scheduled": true,
  "sharedGroups": [],
  "sharedUsers": [],
  "status": "Draft",
  "visibility": "Private"
}
```

### Required to save

| Field | Rule |
|-------|------|
| `name` | Required |
| `domain` | Required (workflow **name**, e.g. `"Vessel Call"`) |
| `sourceFormId` | Strongly recommended ΓÇö this is what picks the ezfb table |

### Output `200`

Same `ReportBuilderConfig` with `id`, `ownerUserId`, `createdAt`, `modified` filled in.

### Error `400`

```json
{ "error": "Report name is required." }
```

```json
{ "error": "Domain (workflow name) is required." }
```

---

## 9. Get saved JSON (edit screen)

```http
GET /api/report-builder/{id}
```

### Output `200`

Full config (same shape as create). Use this to hydrate the wizard / editor.

### Error `404`

```json
{ "error": "Report not found." }
```

---

## 10. Get saved report **data** (grid)

```http
GET /api/report-builder/{id}/data
```

Loads the saved JSON, runs the query, returns columns + rows. Does **not** increment `runs`.

### Output `200`

Same as preview (`ReportRunResult`). `reportId` is the saved id.

```json
{
  "reportId": "e7e5eed7-10a0-4036-a150-641e67734d5e",
  "name": "Vessel Call Report",
  "domain": "Vessel Call",
  "columns": [
    { "key": "Customer", "label": "Customer", "calc": "None", "colType": "value", "width": 180, "isCalculated": false },
    { "key": "IMO Number", "label": "IMO Number", "calc": "None", "colType": "value", "width": 140, "isCalculated": false }
  ],
  "rows": [
    { "Customer": "Maersk", "IMO Number": "9123456" }
  ],
  "totals": null,
  "rowCount": 1,
  "workflowId": "b05ed64d-7c94-4ecd-a5b0-caa5839757f9",
  "sourceFormId": "ad061183-cac7-4fbf-b4ca-d853b07fc350"
}
```

If there are no items: `"rows": []`, `"rowCount": 0` ΓÇö still `200`.

---

## 11. Update / publish / delete

### Update

```http
PUT /api/report-builder/{id}
```

**Input:** full config body (same as create). Path `id` wins over body `id`.

**Output `200`:** saved config.

**Errors:** `404` not found ┬╖ `403` only owner can update ┬╖ `400` validation.

### Publish

```http
POST /api/report-builder/{id}/publish
```

No body. Sets `status` to `Published` and registers Hangfire if `scheduled` + recipients.

**Output `200`:** saved config.

### Delete (soft)

```http
DELETE /api/report-builder/{id}
```

**Output `200`**

```json
{ "deleted": true, "id": "e7e5eed7-10a0-4036-a150-641e67734d5e" }
```

Also removes the Hangfire recurring job.

---

## 12. Run now / download / email

```http
POST /api/report-builder/{id}/run
POST /api/report-builder/{id}/run?download=true&format=Excel
POST /api/report-builder/{id}/run?sendEmail=true
```

| Query | Type | Default | Notes |
|-------|------|---------|--------|
| `download` | bool | `false` | Returns a file instead of JSON |
| `sendEmail` | bool | `false` | Sends using saved `schedule.recipients` / `cc` |
| `format` | string | schedule format or PDF | `PDF` or `Excel` |

Increments `runs` on success.

### JSON output (`download=false`)

Same `ReportRunResult` as `/data`.

### File output (`download=true`)

Binary file with `Content-Disposition` filename, e.g. `Vessel Call Report.pdf`.

---

## 13. Field settings, filters, schedule

### `fieldSettings` (key = field `id` from `/fields`)

| Property | Values |
|----------|--------|
| `label` | Column header (this is also the row key in `/data`) |
| `width` | pixels, default `160` |
| `calc` | `None` \| `Sum` \| `Avg` \| `Count` \| `Min` \| `Max` |
| `colType` | `value` \| `calculated` |
| `formula` / `expression` | e.g. `[Amount] * [Qty]` ΓÇö labels in `[brackets]` |

When `calc` is not `None`, `/data` may include `totals`:

```json
{
  "totals": {
    "Customer": "Total",
    "Amount": "12500.00"
  }
}
```

### Filters

| Property | Notes |
|----------|--------|
| `fieldId` | Form control id (preferred) |
| `field` | Label if `fieldId` is missing |
| `operator` or `op` | see below |
| `value` | string / number / bool. Empty value is **ignored** (filter not applied), except `isEmpty` / `isNotEmpty` |

Operators:

| Send | Meaning |
|------|---------|
| `equals` / `eq` / `=` | equal |
| `notEquals` / `ne` | not equal |
| `contains` | like `%value%` |
| `startsWith` | like `value%` |
| `endsWith` | like `%value` |
| `gt` / `gte` / `lt` / `lte` | numeric |
| `isEmpty` | null or blank |
| `isNotEmpty` | has a value |

### Schedule

Hangfire job is created only when **all** of these are true:

1. `status` = `Published` (or you call `/publish`)
2. `scheduled` = `true`
3. `schedule` is present
4. At least one of `recipients`, `cc`, `sharedUsers` is non-empty

| Property | Example | Notes |
|----------|---------|--------|
| `recurrence` | `Hourly` \| `Daily` \| `Weekly` \| `Monthly` | default Weekly |
| `day` | `Monday` or `1`ΓÇô`28` | weekday for Weekly; day-of-month (1ΓÇô28) for Monthly |
| `time` | `"09:00"` or `"9:00 AM"` | default `09:00` |
| `timezone` | `UTC` or `India Standard Time` / `Asia/Kolkata` | |
| `format` | `PDF` \| `Excel` | attachment for email/download |
| `recipients` | email strings | required for scheduled send |
| `cc` | email strings | |
| `subject` / `message` | email text | |

Schedules are stored on the report row `reporting.ReportDefinitions` (`Scheduled` + `ConfigJson.schedule`), not a separate schedule table. Hangfire holds the cron in catalog `HangFire.Hash` / `HangFire.Set`.

---

## 14. Wizard drafts (optional autosave)

Same pattern as folder/user creation drafts. One **active** draft per tenant + user.

Base: `/api/report-builder/drafts`

| Use | Method | Path |
|-----|--------|------|
| Save after each step | `PUT` or `POST` | `/api/report-builder/drafts` |
| Resume | `GET` | `/api/report-builder/drafts` |
| Get by id | `GET` | `/api/report-builder/drafts/{draftId}` |
| Mark done after real save | `POST` | `/api/report-builder/drafts/{draftId}/complete` |
| Discard | `DELETE` | `/api/report-builder/drafts/{draftId}` |

Steps:

| `currentStep` | `currentStepKey` |
|---------------|------------------|
| 1 | `ai` |
| 2 | `details` |
| 3 | `fields` |
| 4 | `filters` |
| 5 | `schedule` |

### Save input

```json
{
  "tenantId": "00000000-0000-0000-0000-000000000000",
  "userId": "00000000-0000-0000-0000-000000000000",
  "draftId": null,
  "currentStep": 3,
  "currentStepKey": "fields",
  "draftJson": "{\"name\":\"Vessel Call Report\",\"sourceFormId\":\"ad061183-cac7-4fbf-b4ca-d853b07fc350\",\"fields\":[\"customer\"]}"
}
```

Empty `tenantId` / `userId` ΓåÆ taken from JWT + `X-Tenant-Id`.  
`draftJson` should be `JSON.stringify` of the **full wizard state so far**.

### Save output `200`

```json
{
  "id": "11111111-2222-3333-4444-555555555555",
  "tenantId": "0822952c-999b-4756-8bb9-495df6e98108",
  "userId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
  "currentStep": 3,
  "currentStepKey": "fields",
  "draftJson": "{ΓÇª}",
  "createdAtUtc": "2026-09-08T10:00:00Z",
  "modifiedAtUtc": "2026-09-08T10:05:00Z",
  "isCompleted": false
}
```

Keep returned `id` as `draftId` on later saves.

After `POST /api/report-builder` succeeds, call `POST .../drafts/{draftId}/complete`.

Resume: `GET /drafts` ΓåÆ `200` hydrate UI, or `404` start at step 1.

```json
{ "error": "No in-progress report builder draft found." }
```

---

## 15. Error shapes

| Status | When |
|--------|------|
| `400` | `{ "error": "ΓÇª" }` validation / missing form table / unresolved fields |
| `401` | missing/invalid JWT |
| `403` | `{ "error": "Only the owner can update this report." }` |
| `404` | `{ "error": "Report not found." }` |

---

## 16. Minimal working sequence

```http
GET  /api/report-builder/forms
GET  /api/report-builder/fields?sourceFormId={formId}
POST /api/report-builder/preview          ΓåÉ body with sourceFormId + fields
POST /api/report-builder                  ΓåÉ same body, status Draft
POST /api/report-builder/{id}/publish
GET  /api/report-builder/{id}/data        ΓåÉ bind grid to columns + rows
```
