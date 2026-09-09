# Branding API ΓÇö Frontend Guide

**Audience:** Frontend  
**Last updated:** August 2026

Public branding APIs for encrypt ΓåÆ load theme/config by encrypted name, and save/update branding JSON in the catalog DB.

**Auth:** All branding endpoints are **`[AllowAnonymous]`** ΓÇö **no JWT required**.

Base path: `/api/branding`

---

## 1. Which API to use

| Use case | Method | Endpoint |
|----------|--------|----------|
| Encrypt branding name (for URL / public link) | `POST` | `/api/branding/encrypt` |
| Get branding by encrypted name | `GET` | `/api/branding/{encryptedBrandingName}` |
| Save **or update** branding | `POST` | `/api/branding` |

There is **no separate PUT**. `POST /api/branding` upserts by **`tenantId` + `brandingName`**:

- First save ΓåÆ insert  
- Same tenant + same name ΓåÆ update `brandingJson`, `userId`, `userEmail`, `modifiedAtUtc`

---

## 2. Encrypt branding name

Use this before building a public branding URL (login page, white-label link, etc.).

```http
POST /api/branding/encrypt
Content-Type: application/json
```

### Request

```json
{
  "brandingName": "acme-corp"
}
```

| Field | Type | Required |
|-------|------|----------|
| `brandingName` | string | **Yes** |

### Response `200`

```json
{
  "brandingName": "acme-corp",
  "encryptedBrandingName": "Base64UrlEncodedCipherTextΓÇª"
}
```

Use `encryptedBrandingName` in the path for GET (URL-safe Base64Url). If you put it in a query/path, URI-encode if needed; the API also runs `Uri.UnescapeDataString` on decrypt.

### Errors

| Status | When |
|--------|------|
| `400` | Missing `brandingName`, or encrypt failed |

---

## 3. Get branding (by encrypted name)

Decrypts the path segment ΓåÆ looks up `catalog.Branding` by plain `brandingName`.

```http
GET /api/branding/{encryptedBrandingName}
```

Example:

```http
GET /api/branding/AbCdEf123ΓÇª
```

### Response `200`

```json
{
  "brandingName": "acme-corp",
  "encryptedBrandingName": "AbCdEf123ΓÇª",
  "tenantId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
  "branding": {
    "id": "11111111-2222-3333-4444-555555555555",
    "tenantId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
    "userId": "bbbbbbbb-cccc-dddd-eeee-ffffffffffff",
    "userEmail": "admin@acme.com",
    "brandingName": "acme-corp",
    "brandingJson": "{\"primaryColor\":\"#0B5FFF\",\"logoUrl\":\"https://ΓÇª\"}",
    "createdAtUtc": "2026-08-20T10:00:00Z",
    "modifiedAtUtc": "2026-08-27T09:15:00Z"
  }
}
```

FE should `JSON.parse(branding.brandingJson)` for theme/logo/config.

### Errors

| Status | When |
|--------|------|
| `400` | Empty / invalid encrypted name (decrypt failed) |
| `404` | Decrypted name has no branding row |

```json
{ "error": "Branding not found.", "brandingName": "acme-corp" }
```

---

## 4. Save / update branding

```http
POST /api/branding
Content-Type: application/json
```

### Request

```json
{
  "tenantId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
  "userId": "bbbbbbbb-cccc-dddd-eeee-ffffffffffff",
  "userEmail": "admin@acme.com",
  "brandingName": "acme-corp",
  "brandingJson": "{\"primaryColor\":\"#0B5FFF\",\"logoUrl\":\"https://cdn.example/logo.svg\",\"appTitle\":\"Acme AP\"}"
}
```

| Field | Type | Required | Notes |
|-------|------|----------|--------|
| `tenantId` | guid | **Yes** | Non-empty |
| `userId` | guid | **Yes** | Non-empty |
| `userEmail` | string | **Yes** | Non-empty |
| `brandingName` | string | **Yes** | Unique key **per tenant** for upsert |
| `brandingJson` | string | Recommended | JSON **string** (not a nested object). Empty/omit ΓåÆ `"{}"` |

### Upsert rule

Match: `tenantId` + `brandingName` (trimmed), not deleted.

| Case | Behavior |
|------|----------|
| No row | Insert new branding |
| Row exists | Update `userId`, `userEmail`, `brandingJson`, set `modifiedAtUtc` |

### Response `200` (`BrandingDto`)

```json
{
  "id": "11111111-2222-3333-4444-555555555555",
  "tenantId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
  "userId": "bbbbbbbb-cccc-dddd-eeee-ffffffffffff",
  "userEmail": "admin@acme.com",
  "brandingName": "acme-corp",
  "brandingJson": "{\"primaryColor\":\"#0B5FFF\",\"logoUrl\":\"https://cdn.example/logo.svg\"}",
  "createdAtUtc": "2026-08-20T10:00:00Z",
  "modifiedAtUtc": "2026-08-27T09:15:00Z"
}
```

### Errors

| Status | When |
|--------|------|
| `400` | Missing `tenantId` / `userId` / `userEmail` / `brandingName` |

```json
{ "error": "BrandingName is required." }
```

---

## 5. Suggested FE flows

### Public / login branding load

```text
1. Have brandingName (from subdomain, query, or tenant config)
2. POST /api/branding/encrypt  { brandingName }
3. Store or navigate with encryptedBrandingName
4. GET /api/branding/{encryptedBrandingName}
5. JSON.parse(response.branding.brandingJson) ΓåÆ apply theme
```

Direct load if you already have the encrypted value in the URL:

```text
GET /api/branding/{encryptedFromUrl}
```

### Admin save / update theme

```text
1. Collect theme object in UI
2. POST /api/branding
   {
     tenantId, userId, userEmail, brandingName,
     brandingJson: JSON.stringify(themeObject)
   }
3. Use returned brandingJson / modifiedAtUtc to confirm
4. Optionally re-encrypt brandingName for public link
```

---

## 6. `brandingJson` shape

The API stores an opaque **string**. Schema is FE-owned. Example:

```json
{
  "primaryColor": "#0B5FFF",
  "secondaryColor": "#111827",
  "logoUrl": "https://cdn.example/logo.svg",
  "faviconUrl": "https://cdn.example/favicon.ico",
  "appTitle": "Acme AP",
  "loginBackgroundUrl": "https://cdn.example/bg.jpg"
}
```

Always send as a stringified JSON field:

```ts
brandingJson: JSON.stringify(theme)
```

---

## 7. Quick TypeScript types

```ts
type EncryptBrandingRequest = { brandingName: string };
type EncryptBrandingResponse = {
  brandingName: string;
  encryptedBrandingName: string;
};

type BrandingDto = {
  id: string;
  tenantId: string;
  userId: string;
  userEmail: string;
  brandingName: string;
  brandingJson: string; // parse with JSON.parse
  createdAtUtc: string;
  modifiedAtUtc: string | null;
};

type BrandingGetResponse = {
  brandingName: string;
  encryptedBrandingName: string;
  tenantId: string;
  branding: BrandingDto;
};

type SaveBrandingRequest = {
  tenantId: string;
  userId: string;
  userEmail: string;
  brandingName: string;
  brandingJson: string;
};
```

---

## 8. Notes for FE

- No Bearer token on these routes.
- Get is by **encrypted name in the path**, not by plain name.
- `tenantId` is returned at **root** and inside `branding` (use either for `X-Tenant-Id`).
- Update = same `POST` with same `tenantId` + `brandingName`.
- Keep `brandingName` stable once public URLs are shared (changing the name creates a new logical key / encrypt result).
- `encryptedBrandingName` depends on server `Branding:EncryptionKey` ΓÇö do not invent ciphertext on the client.

---

## 9. Related docs

| Doc | Topic |
|-----|--------|
| `REPOSITORY_RELATED_DOCUMENTS_FRONTEND_GUIDE.md` | Related / related-exact / related-saved |
| `FRONTEND_TEAM_API_GUIDE.md` | General API conventions |
