# Share + Email OTP — Frontend guide

**Audience:** Frontend  
**Date:** 21 Sep 2026

Three share types use the **same guest invite login**. Email OTP is a separate login step when the user’s MFA method is `Email OTP`.

Base path examples: `https://demo.ezofis.com/v6api` or `https://cloud.ezofis.com/api`.

---

## 1. Common headers

| Header | When |
|--------|------|
| `Authorization: Bearer <jwt>` | Sharer APIs and guest APIs after login |
| `X-Tenant-Id: <tenant-guid>` | Tenant APIs. After guest login use `sourceTenantId` from preview / share result |
| `Content-Type: application/json` | POST bodies |

No `X-Tenant-Id` for share preview, set-password, or social-login (tenant comes from the share token).

Guest invite URL (emailed and returned as `shareUrl`):

```text
{frontend}/sign-in?shareToken={token}&email={email}&isnew=true|false
```

| `isnew` | UI |
|---------|----|
| `true` | First time: set password **or** Google / Microsoft |
| `false` | Existing account: normal sign-in |

`action`: `0` = Can View, `1` = Can Edit (upload).  
`permission`: `"Can View"` or `"Can Edit"`.

---

## 2. Share types

| Kind | Who creates it | What the guest sees |
|------|----------------|---------------------|
| `Item` | One document | Only that file (+ their uploads if Can Edit) |
| `Filter` | A filter on one repository | Live list of documents matching that filter. New matching files appear automatically. Other files stay hidden. |
| `Dashboard` | One saved dashboard | That dashboard, and documents in **that dashboard’s repository** (live, including new files). Not other repositories. |

All three return `shareKind` (`Item` / `Filter` / `Dashboard`).

---

## 3. File share (existing)

```http
POST /api/repositories/{repositoryId}/items/{itemId}/share
```

```json
{
  "email": "guest@example.com",
  "message": "Please review",
  "action": 0
}
```

After login, open that item with `?shareToken=` (or header `X-Share-Token`).  
Item list is limited to that one file.

---

## 4. Filter share (new)

Sharer is already looking at a filtered list, for example:

`GET /api/repositories/{repositoryId}/items?Filters={"Supplier":"APC-T001"}`

Invite with the **same filter JSON**:

```http
POST /api/repositories/{repositoryId}/share-filter
Authorization: Bearer <sharer-jwt>
X-Tenant-Id: <tenant>
```

```json
{
  "email": "guest@example.com",
  "filters": { "Supplier": "APC-T001" },
  "message": "Supplier APC invoices",
  "action": 0
}
```

Multiple values are allowed: `"filters": { "Status": ["Verifier", "Approved"] }`.

**Response (201)** includes:

| Field | Use |
|-------|-----|
| `shareUrl` | Email link (also sent by API) |
| `shareToken` | Pass on later repository calls |
| `shareKind` | `"Filter"` |
| `filtersJson` | Locked filters, e.g. `{"Supplier":"APC-T001"}` |
| `sourceRepositoryId` | Only this folder |
| `sourceTenantId` | `X-Tenant-Id` after login |
| `isNew` | Same as `isnew` on the URL |
| `requiresPasswordSetup` | Show set-password |
| `allowedAuthMethods` | `password_setup`, `google`, `microsoft`, or `password_login` |

### Guest

1. `GET /api/repositories/share/{shareToken}/preview` (anonymous)  
   `shareKind` is `Filter`. `sourceItemId` is null. Show repository name + filters, not one file.
2. If `isnew=true`: `POST /api/auth/share/set-password`  
   `{ "shareToken", "email", "password" }`  
   or `POST /api/auth/share/social-login` `{ "shareToken", "email", "provider": "google"|"microsoft" }`
3. If `isnew=false`: normal `POST /api/auth/ezofis/login` with `X-Tenant-Id` = `sourceTenantId`
4. List (filters are **forced** by the share; do not let the guest widen them):

```http
GET /api/repositories/{sourceRepositoryId}/items?shareToken={token}&Page=1&PageSize=50
Authorization: Bearer <guest-jwt>
X-Tenant-Id: {sourceTenantId}
```

5. Open / download a row only if it is in that list (`GET .../items/{itemId}/file?shareToken=`).

New documents that match the filter show up on the next list call. Documents that do not match return 403.

---

## 5. Dashboard share (new)

Dashboard must already be saved (`POST /api/dashboard/schema/save`).  
Saved schema now includes `id` — store it if you have it. You can also share by repository (and optional workflow).

```http
POST /api/dashboard/share
Authorization: Bearer <sharer-jwt>
X-Tenant-Id: <tenant>
```

```json
{
  "email": "guest@example.com",
  "repository_id": "a6169a5c-1468-4fb5-90a9-220082a89f2a",
  "message": "Testing Command Center",
  "action": 0
}
```

Also accepted: `workflow_id`, `dashboard_id` (or camelCase `repositoryId`, `workflowId`, `dashboardId`).

**Response (201):** `shareKind` = `"Dashboard"`, plus `shareUrl`, `shareToken`, `sourceRepositoryId`, `sourceDashboardId`, `sourceWorkflowId`, `sourceTenantId`.

### Guest

Same sign-in as filter share (`preview` → set-password / login). Preview `fileName` is `"Dashboard"`.

Then:

**Dashboard HTML** (same API the app already uses):

```http
POST /api/dashboard/data?shareToken={token}
Authorization: Bearer <guest-jwt>
X-Tenant-Id: {sourceTenantId}
Content-Type: application/json

{
  "repository_id": "{sourceRepositoryId}",
  "workflow_id": "{sourceWorkflowId or omit}"
}
```

The API locks tenant/repository to the share. Do not send another repository.

**Documents of that dashboard’s repository:**

```http
GET /api/repositories/{sourceRepositoryId}/items?shareToken={token}&Page=1&PageSize=50
Authorization: Bearer <guest-jwt>
X-Tenant-Id: {sourceTenantId}
```

Guest sees current files and new files in **that repository only**.

---

## 6. Shared with me / revoke

```http
GET /api/repositories/shared-with-me
```

Each row has `shareKind`, `shareToken`, `sourceRepositoryId`, `sourceItemId` (null for filter/dashboard), `filtersJson`, `sourceDashboardId`.

```http
DELETE /api/repositories/share/{shareId}
```

Sharer only. 204 when revoked.

---

## 7. Email OTP login

Used when the user has 2FA on and MFA method is **`Email OTP`** (not the authenticator app).

### Step 1 — login

```http
POST /api/auth/ezofis/login
X-Tenant-Id: <tenant>
Content-Type: application/json

{ "email": "user@example.com", "password": "..." }
```

If 2FA is required, HTTP 200 body (no access token yet):

```json
{
  "tempToken": "....",
  "tenantId": "....",
  "userId": "....",
  "expiresInSeconds": 300,
  "method": "Email OTP",
  "message": "OTP sent to email"
}
```

| `method` | UI |
|----------|----|
| `Email OTP` | Show “OTP sent to email” and a code box. API already emailed the code. |
| `Authenticator OTP` | Existing authenticator screen. `message` is null. |

Do **not** treat `tempToken` as a failed password. Password was accepted; 2FA is next.

### Step 2 — verify

```http
POST /api/auth/2fa/complete
X-Tenant-Id: <tenant>
Content-Type: application/json

{ "tempToken": "<from login>", "code": "123456" }
```

Success: same login success as today (`accessToken`, `tokenType`, `expiresIn`).  
Wrong/expired code: 401.

`tempToken` is short-lived. If it expires, call login again (a new email OTP is sent).

Share-invite set-password / social-login is unchanged and does not use this OTP step unless that account also has 2FA on the normal login path.

---

## 8. Frontend checklist

- [ ] File share still opens one document
- [ ] Filter share sends the current `Filters` object to `POST .../share-filter`
- [ ] Guest filter page lists with `shareToken` and does not offer other repositories or other filter values
- [ ] Dashboard share calls `POST /api/dashboard/share` with the dashboard repository id
- [ ] Guest dashboard calls `POST /api/dashboard/data` and repository items with the same `shareToken`
- [ ] Login: if `method` is `Email OTP`, show `message` (“OTP sent to email”) and complete with `tempToken` + code
- [ ] Login: if `method` is `Authenticator OTP`, keep the existing authenticator UI
