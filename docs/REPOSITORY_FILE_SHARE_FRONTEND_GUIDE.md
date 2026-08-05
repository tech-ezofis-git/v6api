# Repository File Share (Invite People) — Frontend Guide

**Audience:** Frontend team  
**Last updated:** July 2026  
**Base URL example:** `https://localhost:44311` or `https://your-host/V6API`

This is **file-level** share (one document), same invite/login concept as **workflow share**.  
It is **not** a whole-repository share, and it does **not** create a separate repository.

---

## Table of contents

1. [What this feature does](#1-what-this-feature-does)
2. [Common headers](#2-common-headers)
3. [Sharer flow — Invite People UI](#3-sharer-flow--invite-people-ui)
4. [Guest flow — open email link](#4-guest-flow--open-email-link)
5. [After login — open shared file](#5-after-login--open-shared-file)
6. [Can View vs Can Edit](#6-can-view-vs-can-edit)
7. [Shared with me](#7-shared-with-me)
8. [Revoke share](#8-revoke-share)
9. [Security rules (what guest sees)](#9-security-rules-what-guest-sees)
10. [Quick API reference](#10-quick-api-reference)
11. [FE checklist](#11-fe-checklist)
12. [Common mistakes](#12-common-mistakes)

---

## 1. What this feature does

When a user invites someone to a **file**:

1. API creates/finds a **TenantUser** guest for that email  
2. Sends an invite **URL** (email + returns `shareUrl` in response)  
3. Guest opens URL → **set password** (new) or **login** (existing) — same as workflow share  
4. Guest can open that **repository** but only sees:
   - the **shared file**
   - files **they uploaded** (only if **Can Edit**)
5. Other files stay hidden  
6. **Admin** still sees all files  

Document security grants are created automatically on share (`Source = Share`).

---

## 2. Common headers

| Header | Required | Notes |
|--------|----------|-------|
| `Authorization: Bearer <JWT>` | Yes (except preview / set-password / social-login) | |
| `X-Tenant-Id: <tenant-guid>` | Yes for tenant APIs | After login use `sourceTenantId` from **preview** |
| `Content-Type: application/json` | POST bodies | |

**No `X-Tenant-Id` needed for:**

- `GET /api/repositories/share/{shareToken}/preview`
- `POST /api/auth/share/set-password`
- `POST /api/auth/share/social-login`

---

## 3. Sharer flow — Invite People UI

### UI (same pattern as workflow invite)

- Email input  
- Permission dropdown: **Can View** / **Can Edit**  
- Invite button  

### Step 1 — Call share API

```http
POST /api/repositories/{repositoryId}/items/{itemId}/share
Authorization: Bearer <sharer-jwt>
X-Tenant-Id: <tenant-guid>
Content-Type: application/json
```

**Request body:**

```json
{
  "email": "guest@example.com",
  "message": "Please review this file",
  "action": 0
}
```

| Field | Required | Notes |
|-------|----------|-------|
| `email` | Yes | Recipient email |
| `message` | No | Included in invite email |
| `action` | No (default `0`) | See mapping below |
| `provisionGuestUser` | No (default `true`) | Keep `true` for Invite People |

### Action mapping (important)

| UI label | Send `action` | Guest can |
|----------|---------------|-----------|
| **Can View** | `0` | View / download shared file only |
| **Can Edit** | `1` | View + **upload** into that repository; also sees their own uploads |

```javascript
const action = permissionLabel === "Can Edit" ? 1 : 0;

await api.post(
  `/api/repositories/${repositoryId}/items/${itemId}/share`,
  { email, message, action }
);
```

### Step 2 — Use response

**Response `201`:**

```json
{
  "shareId": "guid",
  "shareToken": "abc123...",
  "sourceRepositoryId": "repo-guid",
  "sourceItemId": "item-guid",
  "sourceTenantId": "tenant-guid",
  "recipientEmail": "guest@example.com",
  "expiresAtUtc": "2026-08-28T10:00:00Z",
  "shareUrl": "https://demoapp.ezofis.com/sign-in?shareToken=abc123...&email=guest%40example.com&isnew=true",
  "guestUserId": "user-guid",
  "action": 0,
  "permission": "Can View",
  "isNew": true,
  "requiresPasswordSetup": true,
  "allowedAuthMethods": ["password_setup", "google", "microsoft"]
}
```

| Field | Use |
|-------|-----|
| `shareUrl` | Show “link sent” / copy link (API also emails it) |
| `shareToken` | Optional debug / support |
| `action` / `permission` | Confirm what was granted |
| `isNew` | `true` = recipient needs first-time setup |

**Errors:**

| Status | Cause |
|--------|-------|
| `400` | Invalid email |
| `401` | Not logged in |
| `404` | Repository item not found |

---

## 4. Guest flow — open email link

Invite URL looks like:

```text
https://demoapp.ezofis.com/sign-in?shareToken=abc123...&email=guest%40example.com&isnew=true
```

| Query | Meaning |
|-------|---------|
| `shareToken` | Share grant token — **keep this** |
| `email` | Recipient email |
| `isnew` | `true` = new guest; `false` = existing user |

### Step 1 — Read URL params

```javascript
const params = new URLSearchParams(window.location.search);
const shareToken = params.get("shareToken");
const email = params.get("email");
const isNew = params.get("isnew") === "true";

sessionStorage.setItem("shareToken", shareToken);
```

### Step 2 — Preview (anonymous)

```http
GET /api/repositories/share/{shareToken}/preview
```

**Example response:**

```json
{
  "shareToken": "abc123...",
  "sourceTenantId": "tenant-guid",
  "sourceRepositoryId": "repo-guid",
  "sourceItemId": "item-guid",
  "fileName": "invoice.pdf",
  "sourceOrganizationName": "Acme Corp",
  "recipientEmail": "guest@example.com",
  "expiresAtUtc": "2026-08-28T10:00:00Z",
  "requiresLogin": true,
  "requiresPasswordSetup": true,
  "requiredSocialProvider": null,
  "allowedAuthMethods": ["password_setup", "google", "microsoft"],
  "loginType": "EZOFIS",
  "autoProvisionGuest": true,
  "workflowInstanceId": null,
  "action": 1,
  "permission": "Can Edit"
}
```

### Step 3 — Choose auth UI from `allowedAuthMethods`

| `allowedAuthMethods` | Show |
|----------------------|------|
| includes `password_setup` | Set-password form **+** Google **+** Microsoft |
| `["google"]` | Google only |
| `["microsoft"]` | Microsoft only |
| includes `password_login` | Normal email + password login |

```javascript
const { allowedAuthMethods, requiredSocialProvider, sourceTenantId,
        sourceRepositoryId, sourceItemId, action, permission } = preview;

sessionStorage.setItem("tenantId", sourceTenantId);
sessionStorage.setItem("repositoryId", sourceRepositoryId);
sessionStorage.setItem("itemId", sourceItemId);
sessionStorage.setItem("shareAction", String(action));
sessionStorage.setItem("sharePermission", permission);

if (allowedAuthMethods.includes("password_setup")) {
  showSetPasswordForm();
  showGoogleButton();
  showMicrosoftButton();
} else if (requiredSocialProvider === "google") {
  showGoogleButtonOnly();
} else if (requiredSocialProvider === "microsoft") {
  showMicrosoftButtonOnly();
} else {
  showNormalLoginForm();
}
```

### Step 4a — New user: set password

```http
POST /api/auth/share/set-password
Content-Type: application/json
```

```json
{
  "shareToken": "abc123...",
  "email": "guest@example.com",
  "password": "SecurePass123!"
}
```

Returns JWT (`accessToken`). **No `X-Tenant-Id` header.**

### Step 4b — New user: Google / Microsoft

After client OAuth (verified email):

```http
POST /api/auth/share/social-login
Content-Type: application/json
```

```json
{
  "shareToken": "abc123...",
  "email": "guest@example.com",
  "provider": "google"
}
```

`provider`: `google` | `microsoft` | `office365`

### Step 4c — Existing user

Use normal login (or `password_login` path). Then continue with `shareToken` + `sourceTenantId` from preview.

---

## 5. After login — open shared file

### Store after login

| Key | Source |
|-----|--------|
| `accessToken` | set-password / social-login / login response |
| `tenantId` | preview.`sourceTenantId` |
| `shareToken` | URL query |
| `repositoryId` | preview.`sourceRepositoryId` |
| `itemId` | preview.`sourceItemId` |
| `shareAction` / `permission` | preview.`action` / `permission` |

### Open file (share-token)

Reuse the **same file viewer** as normal repository view. Pass `sharedtoken`:

```http
GET /api/repositories/{sourceRepositoryId}/items/{sourceItemId}/workspace?sharedtoken={shareToken}
Authorization: Bearer {accessToken}
X-Tenant-Id: {sourceTenantId}
```

Also for item / file / timeline / comments:

```text
?sharedtoken={shareToken}
```

or header:

```http
X-Share-Token: {shareToken}
```

**Suggested navigation:**

```text
/repository/{sourceRepositoryId}/items/{sourceItemId}?shareToken={shareToken}
```

### Browse repository (JWT — scoped list)

After login, guest can list the repository with normal JWT headers (no share token required for list filter):

```http
GET /api/repositories/{sourceRepositoryId}/items
Authorization: Bearer {accessToken}
X-Tenant-Id: {sourceTenantId}
```

API returns **only** shared file (+ their uploads if Can Edit). Other files are filtered out.

### Upload (Can Edit only)

Show upload UI only when `action === 1` / `permission === "Can Edit"`.

```http
POST /api/repositories/{sourceRepositoryId}/items
Authorization: Bearer {accessToken}
X-Tenant-Id: {sourceTenantId}
```

(Use your existing upload endpoint/body.)

Share-token alone is **read-only** — uploads must use JWT + tenant (not write via `sharedtoken`).

---

## 6. Can View vs Can Edit

| | Can View (`action: 0`) | Can Edit (`action: 1`) |
|--|------------------------|-------------------------|
| Open repository | Yes | Yes |
| See shared file | Yes | Yes |
| Download shared file | Yes | Yes |
| Upload new files | No | Yes |
| See own uploaded files | N/A | Yes |
| See other users’ files | No | No |

Where FE reads permission:

| When | Field |
|------|-------|
| After create share | response.`action` / `permission` |
| Guest preview | preview.`action` / `permission` |
| Shared with me list | item.`action` / `permission` |

---

## 7. Shared with me

For logged-in user to reopen shares without the email link:

```http
GET /api/repositories/shared-with-me
Authorization: Bearer <jwt>
```

**Response item:**

```json
{
  "shareId": "guid",
  "shareToken": "abc123...",
  "sourceRepositoryId": "repo-guid",
  "sourceItemId": "item-guid",
  "fileName": "invoice.pdf",
  "sourceOrganizationName": "Acme Corp",
  "sharedAtUtc": "2026-07-29T10:00:00Z",
  "expiresAtUtc": "2026-08-28T10:00:00Z",
  "action": 1,
  "permission": "Can Edit"
}
```

On click: store `shareToken`, open workspace with `?sharedtoken=...` (and use preview if you need `sourceTenantId`).

---

## 8. Revoke share

Sharer only:

```http
DELETE /api/repositories/share/{shareId}
Authorization: Bearer <jwt>
X-Tenant-Id: <tenant-guid>
```

`204` = revoked.

---

## 9. Security rules (what guest sees)

Created automatically on invite — **FE does not call document-security PUT** for this.

| Saved | Purpose |
|-------|---------|
| Document security grant `ItemId` = shared file | Guest can see that file |
| Document security grant `CreatedBy` = guest user id | Guest can see files they upload |
| `ShareRecipients` row | Guest is scoped (not open-all); `CanUpload` if Can Edit |

| Role | Sees |
|------|------|
| Guest (share) | Shared file + own uploads (if Can Edit) |
| Normal TenantUser (no share scope) | Unchanged open/ACL rules |
| Admin | All files |

---

## 10. Quick API reference

| Step | Method | Route | Auth |
|------|--------|-------|------|
| Invite | `POST` | `/api/repositories/{id}/items/{itemId}/share` | JWT + tenant |
| Preview | `GET` | `/api/repositories/share/{shareToken}/preview` | Anonymous |
| Set password | `POST` | `/api/auth/share/set-password` | Anonymous (token in body) |
| Social login | `POST` | `/api/auth/share/social-login` | Anonymous (token in body) |
| Open file | `GET` | `/api/repositories/{id}/items/{itemId}/workspace?sharedtoken=` | JWT + tenant |
| List items | `GET` | `/api/repositories/{id}/items` | JWT + tenant |
| Upload | existing upload route | JWT + tenant | Can Edit only |
| Shared with me | `GET` | `/api/repositories/shared-with-me` | JWT |
| Revoke | `DELETE` | `/api/repositories/share/{shareId}` | JWT + tenant |

**Reuse workflow share sign-in page** — same preview / set-password / social-login endpoints.

---

## 11. FE checklist

### Sharer (Invite People)

- [ ] Email + Can View / Can Edit dropdown + Invite  
- [ ] Map UI → `action` `0` / `1`  
- [ ] Call `POST .../items/{itemId}/share`  
- [ ] Show success using `shareUrl` / `permission`  
- [ ] Do **not** create a new repository for share  

### Guest sign-in

- [ ] Parse `shareToken`, `email`, `isnew` from URL  
- [ ] Call preview  
- [ ] Branch UI on `allowedAuthMethods`  
- [ ] set-password **or** social-login **or** normal login  
- [ ] Persist `accessToken`, `sourceTenantId`, `shareToken`, repo/item ids, `action`  

### Guest after login

- [ ] Open file with `?sharedtoken=`  
- [ ] If `action === 1`, show upload  
- [ ] If `action === 0`, hide upload  
- [ ] List API shows only allowed files (no extra FE filter required)  

### Shared with me

- [ ] List shares; show `permission` badge  
- [ ] Reopen with stored `shareToken`  

---

## 12. Common mistakes

| Wrong | Right |
|-------|-------|
| Create a new repository to share | Share the **existing file** via `.../items/{itemId}/share` |
| Send `"Can View"` as string | Send `"action": 0` or `1` |
| Forget `shareToken` after login | Keep in session; pass `sharedtoken` on file GETs |
| Upload using only share token | Upload with JWT + `X-Tenant-Id` (Can Edit) |
| Use wrong tenant after login | Use preview.`sourceTenantId` |
| Expect guest to see all repo files | They only see shared + own uploads |
| Build a separate set-password API | Use existing `/api/auth/share/*` (same as workflow) |

---

## Related docs

- Folder / document security (Admin ACL wizards): `docs/REPOSITORY_FOLDER_DOCUMENT_SECURITY.md`  
- Workflow inbox share (same guest login): `docs/TEAM_API_GUIDE_CREDITS_AND_WORKFLOW_SHARE.md` §2  
