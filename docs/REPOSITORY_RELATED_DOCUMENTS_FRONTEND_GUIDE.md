# Repository Related Documents — Frontend Guide

**Audience:** Frontend  
**Last updated:** August 2026

Related documents for an open file. FE sends **`repositoryId` + `itemId`**.  
Backend reads metadata and searches **all repositories** in the tenant.

**Saved related docs** are stored per **Item ID + Repository ID**. A new save **replaces** the previous set.

---

## 1. Which API to use

| Use case | Endpoint | Match / persist rule |
|----------|----------|----------------------|
| Related Docs tab (same folder path) | `GET …/related` | Folder-structure fields only; **any 2 of 3** is enough |
| “Check for matches” (score %) | `GET …/related-exact` | No field → **all** fields with values; with field(s) → only those |
| Open item → show linked related docs | `GET …/related-saved` | Latest saved set for this item |
| User clicks `+` / Save selection | `PUT …/related-saved` | **Replace** previous links with new selection |

All require:

```http
Authorization: Bearer {jwt}
```

Tenant comes from the JWT / tenant context (same as other repository APIs).

---

## 2. Related (loose) — folder structure

```http
GET /api/repositories/{repositoryId}/items/{itemId}/related?page=1&pageSize=50
Authorization: Bearer {jwt}
```

### Query params

| Param | Default | Notes |
|-------|---------|--------|
| `page` | `1` | 1-based |
| `pageSize` | `50` | Max `200` |

### How match works

1. Load source file metadata.
2. Take source repo fields with `includeInFolderStructure = true` (ordered by level), e.g.:
   - Vendor Name → PO Number → Invoice Number  
3. Build match values from the open file.
4. Search all active tenant repositories in parallel.
5. Exclude the current `itemId`.
6. Apply document security (non-admins).

**Threshold**

| Folder fields with values | Required matches |
|---------------------------|------------------|
| 3 | **any 2** |
| 2 | both |
| 1 | that 1 |

Aliases are resolved (e.g. `Vendor Name` ↔ `Supplier`, `PO Number` ↔ `PONumber`, `Invoice No` ↔ `InvoiceNumber`).

If the repo has **no** folder-structure fields, fallback is Supplier + PONumber / InvoiceNo.

### Sample response

```json
{
  "sourceRepositoryId": "6dcebb44-2942-4ed3-beb6-dbd937e97d5f",
  "sourceItemId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
  "match": {
    "VendorName": "ACME Corp",
    "PONumber": "PO-12345",
    "InvoiceNo": "INV-9"
  },
  "matchFields": ["VendorName", "PONumber", "InvoiceNo"],
  "page": 1,
  "pageSize": 50,
  "totalCount": 8,
  "data": [
    {
      "repositoryId": "11111111-2222-3333-4444-555555555555",
      "repositoryName": "AP Default",
      "id": "bbbbbbbb-cccc-dddd-eeee-ffffffffffff",
      "fileName": "PO-12345.pdf",
      "fileType": "pdf",
      "fileSize": 204800,
      "documentType": "PO",
      "supplier": "ACME Corp",
      "poNumber": "PO-12345",
      "invoiceNumber": null,
      "createdAtUtc": "2026-08-01T10:00:00Z",
      "matchScore": 67,
      "matchCount": 2,
      "matchedFields": ["VendorName", "PONumber"]
    }
  ]
}
```

---

## 3. Related exact (scoring) — “Check for matches”

```http
GET /api/repositories/{repositoryId}/items/{itemId}/related-exact?page=1&pageSize=50
Authorization: Bearer {jwt}
```

### Optional field filter

| Param | Notes |
|-------|--------|
| `field` / `fields` | Comma-separated field name or SQL column (e.g. `Supplier`). Omit → match **all** fields with values on the source item. |
| `value` | Optional override when a **single** field is specified (e.g. banner text “Nexus Industrial Solutions Ltd.”). If omitted, value comes from the open item. |

Examples:

```http
# All fields with values on the source item (default)
GET …/related-exact

# Particular field — use item’s current value
GET …/related-exact?field=Supplier

# Particular field + explicit value (UI banner)
GET …/related-exact?field=Supplier&value=Nexus%20Industrial%20Solutions%20Ltd.
```

### How match / score works

1. Load repository field definitions (or only the requested field).
2. Skip empty values and `DYNAMIC_TABLE` (line items).
3. Search all repos; score each candidate file.
4. Sort by `matchScore` desc, then `matchCount`, then date.

### Score formula

```text
matchScore = round(matchCount / totalFields × 100)
```

Only rows with **`matchScore >= 50`** are returned (weaker partials are dropped).

| Example | Score | Returned? |
|---------|-------|-----------|
| 14 of 14 matched | **100** | Yes |
| 10 of 14 matched | **71** | Yes |
| 7 of 14 matched | **50** | Yes |
| 5 of 15 matched | **33** | No |
| Single-field match | **100** if equal | Yes |

- `matchCount` — how many fields matched  
- `matchedFields` — which keys matched  
- `match` / `matchFields` — criteria used for this search  

FE shows the results list with % and a `+` to select docs to save.

### Sample item in `data`

```json
{
  "repositoryId": "…",
  "repositoryName": "Procurement Ledger",
  "id": "…",
  "fileName": "PO-2026-991.pdf",
  "matchScore": 92,
  "matchCount": 13,
  "matchedFields": ["Supplier", "PONumber", "…"]
}
```

---

## 4. Saved related documents (persist / reopen / replace)

For each **Item ID + Repository ID**, only the **latest** saved set is kept.

### Get saved (on item open)

```http
GET /api/repositories/{repositoryId}/items/{itemId}/related-saved
Authorization: Bearer {jwt}
```

### Save / replace selection

```http
PUT /api/repositories/{repositoryId}/items/{itemId}/related-saved
Authorization: Bearer {jwt}
Content-Type: application/json
```

Path uses the **source** (open) file. Body `items[]` uses each **related** file’s `repositoryId` + `id` from the search response (`itemId` in the body = search row `id`).

#### A) Overall match (all fields) — default `related-exact`

Do **not** set `matchField` / `matchValue` (or send `null`). This is the common case after `GET …/related-exact` with no `field` param.

```json
{
  "items": [
    {
      "repositoryId": "f1138fe5-ddfa-4daf-8562-11fa4e989f23",
      "itemId": "2d692628-b881-45f5-9002-3f3ce158f4cd",
      "matchScore": 93
    }
  ]
}
```

#### B) Particular field match — after `?field=Supplier&value=…`

```json
{
  "matchField": "Supplier",
  "matchValue": "Nexus Industrial Solutions Ltd.",
  "items": [
    {
      "repositoryId": "11111111-2222-3333-4444-555555555555",
      "itemId": "bbbbbbbb-cccc-dddd-eeee-ffffffffffff",
      "matchScore": 100
    }
  ]
}
```

| Behavior | Detail |
|----------|--------|
| Replace | Previous links for this source item are soft-deleted, then `items` are inserted |
| Empty `items` | Clears all saved related docs for this item |
| Reopen | `GET …/related-saved` returns only the latest set |
| Overall vs field | Overall → omit `matchField`/`matchValue`; field banner → set both |

### Flow (matches product requirement)

1. Overall: `GET …/related-exact` **or** field: `GET …/related-exact?field=…&value=…`
2. Results show % (only ≥ 50).
3. User selects docs (`+`) → `PUT …/related-saved` (body as A or B above).
4. Reopen same item → `GET …/related-saved` shows Document A.
5. New match → select Document B → `PUT` again → Document A is replaced; only B remains.

### Sample saved response

```json
{
  "sourceRepositoryId": "f1138fe5-ddfa-4daf-8562-11fa4e989f23",
  "sourceItemId": "f26ac1d1-8a13-4fe1-b353-b6b8d14f5e86",
  "matchField": null,
  "matchValue": null,
  "totalCount": 1,
  "data": [
    {
      "id": "link-guid",
      "relatedRepositoryId": "f1138fe5-ddfa-4daf-8562-11fa4e989f23",
      "relatedRepositoryName": "Accounts Payable",
      "relatedItemId": "2d692628-b881-45f5-9002-3f3ce158f4cd",
      "fileName": "INV-2026-3101_v8",
      "matchScore": 93,
      "matchField": null,
      "matchValue": null,
      "createdAtUtc": "2026-08-11T10:00:00Z"
    }
  ]
}
```

---

## 5. Response fields

### Exact / loose search (`related`, `related-exact`)

| Field | Type | Meaning |
|-------|------|---------|
| `sourceRepositoryId` | guid | Open file’s repo |
| `sourceItemId` | guid | Open file’s id |
| `match` | object | Values used for matching |
| `matchFields` | string[] | Keys used for matching |
| `page` / `pageSize` / `totalCount` | number | Paging |
| `data` | array | Candidate related files |

### Each search `data[]` row

| Field | Type | Meaning |
|-------|------|---------|
| `repositoryId` | guid | Where the related file lives |
| `repositoryName` | string | Repo display name |
| `id` | guid | Related item id |
| `fileName` / `fileType` / `fileSize` | | File info |
| `documentType` / `supplier` / `poNumber` / `invoiceNumber` | string? | Common columns when present |
| `createdAtUtc` | datetime? | |
| `matchScore` | int | 0–100 (≥ 50 for exact results) |
| `matchCount` | int | Fields matched |
| `matchedFields` | string[] | Which keys matched |

### Saved (`related-saved`)

| Field | Type | Meaning |
|-------|------|---------|
| `matchField` / `matchValue` | string? | Set only for single-field saves; `null` for overall |
| `data[].relatedRepositoryId` | guid | Related file’s repo |
| `data[].relatedItemId` | guid | Related file’s item id |
| `data[].matchScore` | int? | Score at save time |

---

## 6. FE wiring (file detail)

```text
On open item:
  Related tab (saved)  → GET …/related-saved
  Optional folder hint → GET …/related

Overall “Check for matches”:
  → GET …/related-exact
  → user selects rows with +
  → PUT …/related-saved   { "items": [ { repositoryId, itemId, matchScore } ] }

Field banner “Check for matches”:
  → GET …/related-exact?field=Supplier&value=…
  → PUT …/related-saved   { "matchField", "matchValue", "items": […] }
```

### Open a related file

```text
/repositories/{relatedRepositoryId}/items/{relatedItemId}
```

For search results use row `repositoryId` + `id`.  
For saved results use `relatedRepositoryId` + `relatedItemId`.

### File download / preview

```http
GET /api/repositories/{repositoryId}/items/{id}/file?disposition=inline
```

---

## 7. Errors

| Status | When |
|--------|------|
| `401` | Missing / invalid token |
| `403` | No view access to source repo/item |
| `404` | Source item or repository not found |
| `200` + empty `data` | No usable match values, or no related / saved files |

Empty match example (no metadata to search on):

```json
{
  "match": {},
  "matchFields": [],
  "totalCount": 0,
  "data": []
}
```

---

## 8. Performance notes

- One HTTP call searches all repos (server-side parallel). **Do not** loop `items/query` per repo from FE.
- New repos get indexes on folder-structure columns (`IncludeInFolderStructure`) for faster related lookups.
- Prefer small `pageSize` (e.g. 20–50) for the Related tab.

---

## 9. Related docs

| Doc | Topic |
|-----|--------|
| `REPOSITORY_TIMELINE_AND_COMMENTS_FRONTEND_GUIDE.md` | Timeline + comments tabs |
| `REPOSITORY_FILE_SHARE_FRONTEND_GUIDE.md` | Cross-tenant file share |
