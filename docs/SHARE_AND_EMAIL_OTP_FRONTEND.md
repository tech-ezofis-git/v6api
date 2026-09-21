# Share + Email OTP — Frontend guide

**Audience:** Frontend  
**Date:** 21 Sep 2026  
**Base path:** `https://cloud.ezofis.com/api`

Filter share and dashboard share use the **same guest invite login**. Email OTP is a separate login step when MFA method is `Email OTP`.

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

## 2. What is new

| Kind | API | Guest sees |
|------|-----|------------|
| `Filter` | `POST /api/repositories/{repositoryId}/share-filter` | Live documents matching the shared filter in that repository. New matching files appear automatically. |
| `Dashboard` | `POST /api/dashboard/share` | That dashboard + documents in that dashboard’s repository (live). |

Both return `shareKind` (`Filter` or `Dashboard`).

---

## 3. Filter share

Sharer is already looking at a filtered list, for example:

```http
GET https://cloud.ezofis.com/api/repositories/{repositoryId}/items?Filters={"Supplier":"APC-T001"}
```

Invite with the **same filter JSON**:

```http
POST https://cloud.ezofis.com/api/repositories/{repositoryId}/share-filter
Authorization: Bearer <sharer-jwt>
X-Tenant-Id: <tenant>
Content-Type: application/json
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

### Guest flow

1. Preview (anonymous):

```http
GET https://cloud.ezofis.com/api/repositories/share/{shareToken}/preview
```

`shareKind` is `Filter`. `sourceItemId` is null. Show repository name + filters.

2. If `isnew=true`:

```http
POST https://cloud.ezofis.com/api/auth/share/set-password
```

```json
{ "shareToken": "...", "email": "guest@example.com", "password": "..." }
```

or social:

```http
POST https://cloud.ezofis.com/api/auth/share/social-login
```

```json
{ "shareToken": "...", "email": "guest@example.com", "provider": "google" }
```

3. If `isnew=false`: normal login with `X-Tenant-Id` = `sourceTenantId`.

4. List items (filters are **forced** by the share; guest must not widen them):

```http
GET https://cloud.ezofis.com/api/repositories/{sourceRepositoryId}/items?shareToken={token}&Page=1&PageSize=50
Authorization: Bearer <guest-jwt>
X-Tenant-Id: {sourceTenantId}
```

5. Open / download a row from that list:

```http
GET https://cloud.ezofis.com/api/repositories/{sourceRepositoryId}/items/{itemId}/file?shareToken={token}
```

New documents that match the filter show up on the next list call. Documents that do not match return 403.

---

## 4. Dashboard share

Dashboard must already be saved. Saved schema includes `id`. You can share by `repository_id` (and optional `workflow_id` / `dashboard_id`).

```http
POST https://cloud.ezofis.com/api/dashboard/share
Authorization: Bearer <sharer-jwt>
X-Tenant-Id: <tenant>
Content-Type: application/json
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

### Guest flow

Same sign-in as filter share (`preview` → set-password / login). Preview `fileName` is `"Dashboard"`.

**Dashboard HTML:**

```http
POST https://cloud.ezofis.com/api/dashboard/data?shareToken={token}
Authorization: Bearer <guest-jwt>
X-Tenant-Id: {sourceTenantId}
Content-Type: application/json
```

```json
{
  "repository_id": "{sourceRepositoryId}",
  "workflow_id": "{sourceWorkflowId or omit}"
}
```

The API locks tenant/repository to the share. Do not send another repository.

**Documents of that dashboard’s repository:**

```http
GET https://cloud.ezofis.com/api/repositories/{sourceRepositoryId}/items?shareToken={token}&Page=1&PageSize=50
Authorization: Bearer <guest-jwt>
X-Tenant-Id: {sourceTenantId}
```

Guest sees current files and new files in **that repository only**.

---

## 5. Email OTP login

Used when the user has 2FA on and MFA method is **`Email OTP`**.

### Step 1 — login

```http
POST https://cloud.ezofis.com/api/auth/ezofis/login
X-Tenant-Id: <tenant>
Content-Type: application/json
```

```json
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

When `method` is `Email OTP`, show “OTP sent to email” and a code box. The API already emailed the code.

Do **not** treat `tempToken` as a failed password. Password was accepted; 2FA is next.

### Step 2 — verify

```http
POST https://cloud.ezofis.com/api/auth/2fa/complete
X-Tenant-Id: <tenant>
Content-Type: application/json
```

```json
{ "tempToken": "<from login>", "code": "123456" }
```

Success: `accessToken`, `tokenType`, `expiresIn`.  
Wrong/expired code: 401.

`tempToken` is short-lived. If it expires, call login again (a new email OTP is sent).

---

## 6. Frontend checklist

- [ ] Filter share sends the current `Filters` object to `POST .../share-filter`
- [ ] Guest filter page lists with `shareToken` and does not offer other repositories or other filter values
- [ ] Dashboard share calls `POST /api/dashboard/share` with the dashboard repository id
- [ ] Guest dashboard calls `POST /api/dashboard/data` and repository items with the same `shareToken`
- [ ] Login: if `method` is `Email OTP`, show `message` (“OTP sent to email”) and complete with `tempToken` + code
