# Repository Related Documents — Frontend Guide

**Audience:** Frontend  
**Last updated:** August 2026

Related documents for an open file. FE sends only **`repositoryId` + `itemId`**.  
Backend reads metadata and searches **all repositories** in the tenant.

---

## 1. Which API to use

| Use case | Endpoint | Match rule |
|----------|----------|------------|
| Related Docs tab (same folder path) | `GET …/related` | Folder-structure fields only; **any 2 of 3** is enough |
| Match score / similarity | `GET …/related-exact` | **All** repository fields (with values); proportional score |

Both require:

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

## 3. Related exact (scoring) — all repository fields

```http
GET /api/repositories/{repositoryId}/items/{itemId}/related-exact?page=1&pageSize=50
Authorization: Bearer {jwt}
```

### How match / score works

1. Load **all** repository field definitions on the source repo (not only folder fields).
2. Skip empty values and `DYNAMIC_TABLE` (line items).
3. Search all repos; score each candidate file.
4. Sort by `matchScore` desc, then `matchCount`, then date.

### Score formula

```text
matchScore = round(matchCount / totalFields × 100)
```

| Example | Score |
|---------|-------|
| 14 of 14 matched | **100** |
| 10 of 14 matched | **71** |
| 7 of 14 matched | **50** |

- `matchCount` — how many fields matched  
- `matchedFields` — which keys matched  
- `match` / `matchFields` — criteria taken from the source file  

FE can show a score badge, or filter `matchScore === 100` for full matches only.

### Sample item in `data`

```json
{
  "repositoryId": "…",
  "repositoryName": "Accounts",
  "id": "…",
  "fileName": "invoice.pdf",
  "matchScore": 71,
  "matchCount": 10,
  "matchedFields": ["Supplier", "PONumber", "InvoiceNo", "Currency", "…"]
}
```

---

## 4. Response fields (both APIs)

### Root

| Field | Type | Meaning |
|-------|------|---------|
| `sourceRepositoryId` | guid | Open file’s repo |
| `sourceItemId` | guid | Open file’s id |
| `match` | object | Values used for matching |
| `matchFields` | string[] | Keys used for matching |
| `page` / `pageSize` / `totalCount` | number | Paging |
| `data` | array | Related files |

### Each `data[]` row

| Field | Type | Meaning |
|-------|------|---------|
| `repositoryId` | guid | Where the related file lives |
| `repositoryName` | string | Repo display name |
| `id` | guid | Related item id |
| `fileName` / `fileType` / `fileSize` | | File info |
| `documentType` / `supplier` / `poNumber` / `invoiceNumber` | string? | Common columns when present |
| `createdAtUtc` | datetime? | |
| `matchScore` | int | 0–100 |
| `matchCount` | int | Fields matched |
| `matchedFields` | string[] | Which keys matched |

---

## 5. FE wiring (file detail tabs)

```text
Timeline  → GET …/timeline
Comments  → GET …/comments
Related   → GET …/related          (folder / any-2-of-3)
Score     → GET …/related-exact    (optional; all-fields score)
```

### Open a related file

```text
/repositories/{repositoryId}/items/{id}
```

Use row `repositoryId` + `id` (not the source repo if different).

### File download / preview

```http
GET /api/repositories/{repositoryId}/items/{id}/file?disposition=inline
```

---

## 6. Errors

| Status | When |
|--------|------|
| `401` | Missing / invalid token |
| `403` | No view access to source repo/item |
| `404` | Source item or repository not found |
| `200` + empty `data` | No usable match values, or no related files |

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

## 7. Performance notes

- One HTTP call searches all repos (server-side parallel). **Do not** loop `items/query` per repo from FE.
- New repos get indexes on folder-structure columns (`IncludeInFolderStructure`) for faster related lookups.
- Prefer small `pageSize` (e.g. 20–50) for the Related tab.

---

## 8. Related docs

| Doc | Topic |
|-----|--------|
| `REPOSITORY_TIMELINE_AND_COMMENTS_FRONTEND_GUIDE.md` | Timeline + comments tabs |
| `REPOSITORY_FILE_SHARE_FRONTEND_GUIDE.md` | Cross-tenant file share |
| `REPOSITORY_FOLDER_DOCUMENT_SECURITY.md` | Folder / document security |
