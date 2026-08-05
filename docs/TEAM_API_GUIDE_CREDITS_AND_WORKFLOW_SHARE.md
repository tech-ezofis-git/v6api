# V6 API — Credits & Workflow File Share (Team Guide)

**Audience:** Frontend, QA, and integration team  
**Last updated:** July 2026  
**Base URL example:** `https://localhost:44311` or `https://your-host/V6API`

---

## Common headers

| Header | Required | Notes |
|--------|----------|-------|
| `Authorization: Bearer <JWT>` | Yes (except anonymous share preview) | Tenant user JWT |
| `X-Tenant-Id: <tenant-guid>` | Yes (most routes) | Tenant context |
| `Content-Type: application/json` | POST bodies | |

**Exceptions:**
- `POST /api/auth/share/set-password` — **no** `X-Tenant-Id` (tenant resolved from share token)
- `GET /api/repositories/share/{shareToken}/preview` — **anonymous**

---

## Table of contents

1. [Credit billing APIs](#1-credit-billing-apis)
2. [Workflow inbox file share (verify concept)](#2-workflow-inbox-file-share-verify-concept)
3. [Quick reference](#3-quick-reference)
4. [Database / deployment notes](#4-database--deployment-notes)

---

## 1. Credit billing APIs

Credit data lives in the **catalog database**:
- `dbo.creditMaster` — monthly allocation per tenant
- `dbo.creditTransaction_MMyy` — monthly transaction ledger (auto-created on first credit update)

### 1.1 When credits are created

| Event | What happens |
|-------|----------------|
| **New tenant signup** | API auto-inserts a default `creditMaster` row for the current IST month (configurable via `TenantDefaultCredit` in appsettings) |
| **Existing tenants** | Run `src/Api/scripts/AddCreditMaster.sql` against catalog DB — creates table + backfills default rows from `catalog.Tenants` |

Default allocation (unless changed in config / SQL script):
- `initialCredit` / `balanceCredit`: **1000**
- `creditType`: **Standard**
- `subscriptionType`: **Trial**
- `status`: **Active**

---

### 1.2 Consume credits (write transaction)

Called by AP Agent, OCR, document summary, etc. when a billable action runs.

```http
POST /api/billing/credits/update
Authorization: Bearer <jwt>
X-Tenant-Id: <tenant-guid>
Content-Type: application/json
```

**Request body:**

```json
{
  "activityType": "OCR Agent",
  "subActivity": "AI OCR",
  "identify": "Document",
  "identifyId": 101,
  "remarks": "Invoice extraction for doc 101",
  "credit": 1,
  "env": "live",
  "inputTokens": 1200,
  "outputTokens": 300,
  "totalTokens": 1500
}
```

| Field | Required | Notes |
|-------|----------|-------|
| `activityType` | Yes | e.g. `AP Agent`, `OCR Agent`, `Document Summary` |
| `subActivity` | Yes | e.g. `AI OCR`, `PO Line Matching`, `DocumentSummary` |
| `identify` | Yes | Table/entity name (stored as `identifyTable`) |
| `identifyId` | Yes | Related record id |
| `remarks` | No | Free text; used in usage breakdown |
| `credit` | No | Default `1` |
| `env` | No | e.g. `live`, `test` |
| `inputTokens` / `outputTokens` / `totalTokens` | No | Optional token metrics |

**Response `200`:**

```json
{
  "id": 1,
  "output": "credits updated"
}
```

| `id` | Meaning |
|------|---------|
| `1` | Success — balance reduced, transaction logged |
| `2` | Credit limit exceeded |
| `0` | Failed — no `creditMaster` row for this tenant/month (or other failure) |

**Special rule:** When `subActivity` is `DocumentSummary` (any spacing), the API looks up `creditMaster` where `creditType = 'DocumentSummary'`.

---

### 1.3 Get current credit master

```http
GET /api/billing/credits/master
Authorization: Bearer <jwt>
X-Tenant-Id: <tenant-guid>
```

**Optional query params:** `allocationMonth`, `allocationYear`, `creditType`  
Defaults to **current IST month/year** if omitted.

**Response `200`:**

```json
{
  "id": 12,
  "tenantId": "a1b2c3d4-...",
  "allocationMonth": 7,
  "allocationYear": 2026,
  "creditType": "Standard",
  "initialCredit": 1000,
  "balanceCredit": 742,
  "remarks": "Default allocation on tenant signup",
  "status": "Active",
  "overallConsumedCredit": 258,
  "validFromDate": "2026-07-01T00:00:00Z",
  "validToDate": null,
  "carryForwardCredit": 0,
  "topUpBalanceCredit": 0,
  "extraConsumedCredit": null,
  "monthlyBalance": 742
}
```

`monthlyBalance` is calculated on master as:

`initialCredit + carryForwardCredit + topUpBalanceCredit − overallConsumedCredit`

**Response `404`:** No row for tenant + period (+ optional creditType).

---

### 1.3.1 Monthly balances (year list from credit master)

```http
GET /api/billing/credits/master/monthly?year=2026
Authorization: Bearer <jwt>
X-Tenant-Id: <tenant-guid>
```

**Optional query params:** `year` (defaults to current IST year), `creditType`

**Response `200`:** array of months with calculated `monthlyBalance`:

```json
[
  {
    "id": 12,
    "tenantId": "a1b2c3d4-...",
    "allocationMonth": 7,
    "allocationYear": 2026,
    "label": "Jul 2026",
    "creditType": "Standard",
    "initialCredit": 1000,
    "carryForwardCredit": 0,
    "topUpBalanceCredit": 0,
    "overallConsumedCredit": 258,
    "extraConsumedCredit": null,
    "balanceCredit": 742,
    "monthlyBalance": 742,
    "status": "Active"
  }
]
```

---

### 1.4 Credit usage dashboard (single POST API)

One endpoint for **all** dashboard widgets (tables, pie chart, trend, monthly list).

```http
POST /api/billing/credits/usage
Authorization: Bearer <jwt>
X-Tenant-Id: <tenant-guid>
Content-Type: application/json
```

**Request body:**

```json
{
  "period": "monthly",
  "year": 2026,
  "month": 6
}
```

| `period` value | Range |
|----------------|-------|
| `today` | Current IST day |
| `yesterday` | Previous IST day |
| `monthly` | Selected or current month (use `year` + `month`) |
| `quarterly` | Current IST quarter |
| `yearly` | Current IST year |

`period` defaults to `monthly` if omitted.  
`year` / `month` only apply when `period = monthly`.

**Example requests:**

```json
{ "period": "today" }
```

```json
{ "period": "monthly", "year": 2026, "month": 6 }
```

```json
{ "period": "yearly" }
```

**Response `200`:**

```json
{
  "period": "Monthly",
  "periodLabel": "June 2026",
  "rangeStartUtc": "2026-05-31T18:30:00Z",
  "rangeEndUtc": "2026-06-30T18:30:00Z",
  "totalCreditsConsumed": 12580,
  "transactionCount": 142,
  "highestConsumption": [
    { "type": "AP Agent", "creditsUsed": 4850 },
    { "type": "OCR Agent", "creditsUsed": 3920 },
    { "type": "Document Summary", "creditsUsed": 1680 },
    { "type": "Total", "creditsUsed": 12580 }
  ],
  "distributionReport": [
    { "type": "Invoice OCR Extraction", "creditsUsed": 3120 },
    { "type": "PO Line Matching", "creditsUsed": 2260 },
    { "type": "Document Summary", "creditsUsed": 1680 },
    { "type": "Supplier Master Validation", "creditsUsed": 980 },
    { "type": "Duplicate Invoice Check", "creditsUsed": 760 },
    { "type": "Total", "creditsUsed": 12580 }
  ],
  "overallCreditSplit": [
    { "type": "AP Agent", "creditsUsed": 4850 },
    { "type": "OCR Agent", "creditsUsed": 3920 },
    { "type": "Document Summary", "creditsUsed": 1680 },
    { "type": "Supplier Validation", "creditsUsed": 980 },
    { "type": "Duplicate Detection", "creditsUsed": 760 },
    { "type": "Back Order Detection", "creditsUsed": 390 }
  ],
  "timeline": [
    { "label": "Week 1", "creditsUsed": 2100, "bucketStartUtc": null },
    { "label": "Week 2", "creditsUsed": 2850, "bucketStartUtc": null },
    { "label": "Week 3", "creditsUsed": 3350, "bucketStartUtc": null },
    { "label": "Week 4", "creditsUsed": 4280, "bucketStartUtc": null }
  ],
  "monthlyConsumption": null,
  "transactions": [
    {
      "id": 1,
      "agent": "OCR Agent",
      "subActivityType": "AI OCR",
      "fileName": "INV-2026-5004.pdf",
      "identifyId": 101,
      "remarks": "Invoice extraction",
      "credit": 1,
      "inputTokens": 1200,
      "outputTokens": 300,
      "totalTokens": 1500,
      "createdAt": "2026-06-15T10:30:00Z"
    }
  ]
}
```

### 1.5 Dashboard widget → API field mapping

| UI widget | Response field | Notes |
|-----------|----------------|-------|
| **Highest Credit Consumption** (bar table) | `highestConsumption` | Broad groups: AP Agent, OCR Agent, Document Summary. Includes **Total** row. |
| **Credit Distribution Report** (detailed table) | `distributionReport` | Granular tasks: Invoice OCR Extraction, PO Line Matching, etc. Includes **Total** row. |
| **Overall Credit Split** (pie chart) | `overallCreditSplit` | Service-level slices for pie chart. **No Total row** — use `totalCreditsConsumed` for center label. |
| **Credit trend** (line chart) | `timeline` | Buckets vary by period (see below). |
| **Monthly consumption list** (yearly/quarterly view) | `monthlyConsumption` | `null` for today/yesterday/monthly. |
| **Transaction Activity** | `transactions` | Newest first. Columns: `agent` (Agent), `fileName` (Filename), `subActivityType`, `credit`, `createdAt`. |

**Timeline buckets by period:**

| `period` | `timeline` labels |
|----------|-------------------|
| `monthly` | Week 1 – Week 4 (Week 5 if needed) |
| `quarterly`, `yearly` | Month abbreviations (Jan, Feb, …) |
| `today` | Hourly (`00:00`, `01:00`, …) |
| `yesterday` | Single bucket `"Yesterday"` |

### 1.6 Recommended `activityType` / `subActivity` values (for consistent charts)

Use these when calling `POST /api/billing/credits/update` so dashboard grouping is correct:

| Pie / summary label | Suggested `activityType` | Suggested `subActivity` / `remarks` |
|---------------------|--------------------------|-------------------------------------|
| AP Agent | `AP Agent` | PO matching, general AP tasks |
| OCR Agent | `OCR Agent` | `AI OCR` |
| Document Summary | `Document Summary` | `DocumentSummary` |
| Supplier Validation | `AP Agent` | remarks/subActivity containing `supplier` |
| Duplicate Detection | `AP Agent` | remarks/subActivity containing `duplicate` |
| Back Order Detection | `AP Agent` | remarks/subActivity containing `back order` |

---

## 2. Workflow inbox file share (verify concept)

Share a workflow inbox document with an **external verifier** by email. The verifier:
1. Receives a link (email when mail settings are configured)
2. Sets a password on first visit (guest user in the **same tenant**)
3. Views the shared file (read-only)
4. Sees the workflow task in **their inbox** and can verify/approve via existing move-next APIs

This extends the repository cross-tenant share pattern with **guest provisioning + inbox reassignment**.

### 2.1 Flow overview

```
Internal user (Tenant)                         External verifier (Guest)
        |                                              |
        | POST .../instances/{id}/share-file           |
        | { email, repositoryId, itemId }            |
        |<-- shareToken, shareUrl, guestUserId -------|
        |                                              |
        | email link --------------------------------->| opens sign-in URL
        |                                              | GET share preview (optional)
        |                                              | POST /api/auth/share/set-password
        |                                              |<-- JWT
        |                                              | GET inbox (assigned task)
        |                                              | GET file with shareToken
        |                                              | POST move-next (verify/approve)
```

### 2.2 Create workflow inbox share

```http
POST /api/workflows/instances/{instanceId}/share-file
Authorization: Bearer <jwt>
X-Tenant-Id: <tenant-guid>
Content-Type: application/json
```

**Path:** `instanceId` = workflow instance GUID (the open inbox ticket).

**Request body:**

```json
{
  "email": "verifier@external.com",
  "repositoryId": "repo-guid",
  "itemId": "item-guid",
  "message": "Please verify this invoice"
}
```

| Field | Required | Notes |
|-------|----------|-------|
| `email` | Yes | External verifier email |
| `repositoryId` | Yes | Repository containing the workflow attachment |
| `itemId` | Yes | Repository item (file) to share |
| `message` | No | Included in share email |

**Response `201`:**

```json
{
  "share": {
    "shareId": "guid",
    "shareToken": "abc123...",
    "sourceRepositoryId": "repo-guid",
    "sourceItemId": "item-guid",
    "recipientEmail": "verifier@external.com",
    "expiresAtUtc": "2026-08-07T10:00:00Z",
    "shareUrl": "https://demoapp.ezofis.com/sign-in?shareToken=abc123...&email=verifier%40external.com"
  },
  "inboxAssignment": {
    "workflowId": "workflow-guid",
    "workflowInstanceId": "instance-guid",
    "transactionId": 42,
    "activityId": "VERIFY_STEP",
    "guestUserId": "guest-user-guid",
    "inboxAssigned": true
  },
  "guestUserId": "guest-user-guid"
}
```

**What the API does internally:**
1. Creates or finds a **guest user** (`TenantUser` role, no password yet) in the tenant
2. Creates a **repository item share** with `autoProvisionGuest = true` and `workflowInstanceId` set
3. Reassigns the **open inbox transaction** to the guest user so they can act on the workflow

**Errors:**

| Status | Cause |
|--------|-------|
| `400` | Invalid email, missing fields |
| `401` | Not authenticated |
| `404` | Workflow instance / repository item not found |

---

### 2.3 Guest login — preview decides what to show (you don't choose at share time)

**At share time** the internal user only enters an **email** — we do **not** know if the recipient will use password, Google, or Microsoft.

**What happens on share:**
1. A placeholder guest user is created in the tenant (needed to assign the inbox task)
2. No password is set and no social provider is locked yet

**When the recipient opens the link**, call preview — the API **looks up that email** in the tenant and returns what they are allowed to use:

```http
GET /api/repositories/share/{shareToken}/preview
```

#### Preview outcomes

| Situation | `allowedAuthMethods` | What to show |
|-----------|----------------------|--------------|
| **New share guest** (first visit, auth not chosen) | `["password_setup", "google", "microsoft"]` | Set password **OR** Google **OR** Microsoft — recipient picks |
| **Already chose Google before** | `["google"]` | Google only |
| **Already chose Microsoft before** | `["microsoft"]` | Microsoft only |
| **Already set EZOFIS password** | `["password_login"]` | Normal email + password login |

**Example — new guest (most common after share):**

```json
{
  "shareToken": "abc123...",
  "sourceTenantId": "tenant-guid",
  "sourceRepositoryId": "repo-guid",
  "sourceItemId": "item-guid",
  "fileName": "invoice.pdf",
  "recipientEmail": "verifier@external.com",
  "requiresLogin": true,
  "requiresPasswordSetup": true,
  "requiredSocialProvider": null,
  "allowedAuthMethods": ["password_setup", "google", "microsoft"],
  "loginType": "EZOFIS",
  "autoProvisionGuest": true,
  "workflowInstanceId": "instance-guid"
}
```

**Example — return visit after they chose Google:**

```json
{
  "requiresPasswordSetup": false,
  "requiredSocialProvider": "google",
  "allowedAuthMethods": ["google"],
  "loginType": "GOOGLE"
}
```

#### Frontend rule (simple)

```javascript
const { allowedAuthMethods, requiredSocialProvider } = preview;

if (allowedAuthMethods.includes("password_setup")) {
  // First visit — show all three options
  showSetPasswordForm();
  showGoogleButton();
  showMicrosoftButton();
} else if (requiredSocialProvider === "google") {
  showGoogleButtonOnly();
} else if (requiredSocialProvider === "microsoft") {
  showMicrosoftButtonOnly();
} else if (allowedAuthMethods.includes("password_login")) {
  showNormalLoginForm();
}
```

**First choice locks the account:**
- Set password → EZOFIS account (future visits: password login)
- Google OAuth → account locked to Google
- Microsoft OAuth → account locked to Microsoft

---

#### Option A — Set password (EZOFIS guests only)

```http
POST /api/auth/share/set-password
Content-Type: application/json
```

```json
{
  "shareToken": "abc123...",
  "email": "verifier@external.com",
  "password": "SecurePass123!"
}
```

Returns `401` if the account is Google/Microsoft — use social login instead.

---

#### Option B — Social login (Google / Microsoft) — uses existing social login

Same as normal app social login, but **share-specific endpoint** (no `X-Tenant-Id` — tenant resolved from share token).

**Flow:**
1. User clicks Google or Microsoft on sign-in page (your existing OAuth UI)
2. Client OAuth completes → you have verified `email` from provider
3. Call share social-login API:

```http
POST /api/auth/share/social-login
Content-Type: application/json
```

```json
{
  "shareToken": "abc123...",
  "email": "verifier@external.com",
  "provider": "google"
}
```

| `provider` | Accepted values |
|------------|-----------------|
| Google | `google`, `GOOGLE` |
| Microsoft | `microsoft`, `MICROSOFT`, `office365` |

**Response `200` (same as normal login / set-password):**

```json
{
  "userId": "guest-user-guid",
  "accessToken": "eyJhbG...",
  "tokenType": "Bearer",
  "expiresIn": 86400
}
```

**Normal social login (return visit, already knows tenant):**

```http
POST /api/auth/social/login
X-Tenant-Id: {sourceTenantId}
Content-Type: application/json
```

```json
{
  "email": "verifier@external.com",
  "provider": "google"
}
```

Use **share/social-login** on first visit from email link (no tenant header).  
Use **social/login** on return visits when tenant is already known.

---

**After login (password or social):** use `Authorization: Bearer <accessToken>` + `X-Tenant-Id: <sourceTenantId>` for all API calls.

**Important:** `sourceTenantId` comes from the **share preview** — not from login response. Call preview first and store it.

**Alternative (EZOFIS only):** `POST /api/auth/ezofis/login` may return `LoginRequiresPasswordSetup` when password not set yet.

---

### 2.4 After share login — complete flow (frontend)

This is the step-by-step sequence **after** `POST /api/auth/share/set-password` **or** `POST /api/auth/share/social-login` succeeds.

#### What to store in app state / session

| Key | Source | Used for |
|-----|--------|----------|
| `accessToken` | set-password response | `Authorization: Bearer` on all APIs |
| `tenantId` | share preview → `sourceTenantId` | `X-Tenant-Id` header |
| `shareToken` | sign-in URL query `shareToken` | Repository file APIs (`?sharedtoken=`) |
| `repositoryId` | share preview → `sourceRepositoryId` | File view routes |
| `itemId` | share preview → `sourceItemId` | File view routes |
| `workflowInstanceId` | share preview → `workflowInstanceId` | Inbox filter + move-next |
| `activityId` | inbox row `activityId` (after inbox load) | move-next body |

```javascript
// Example: persist after successful set-password
sessionStorage.setItem("accessToken", login.accessToken);
sessionStorage.setItem("tenantId", preview.sourceTenantId);
sessionStorage.setItem("shareToken", shareTokenFromUrl);
sessionStorage.setItem("repositoryId", preview.sourceRepositoryId);
sessionStorage.setItem("itemId", preview.sourceItemId);
sessionStorage.setItem("workflowInstanceId", preview.workflowInstanceId);
```

#### Step-by-step API sequence

```
1. GET  /api/repositories/share/{shareToken}/preview     (anonymous — decide password vs social UI)
2a. POST /api/auth/share/set-password                    (EZOFIS guest — no X-Tenant-Id)
2b. POST /api/auth/share/social-login                    (Google/Microsoft — no X-Tenant-Id)
3. GET  /api/repositories/{repoId}/items/{itemId}/workspace?sharedtoken={token}
4. GET  /api/workflow/listByUserId/                      (find workflow with inbox count > 0)
5. GET  /api/workflows/inbox?workflowId={id}&instanceId={workflowInstanceId}
6. POST /api/workflows/instances/{instanceId}/move-next  (verify / approve)
```

**Step 1 — Preview (before set-password UI)**

Already covered in [2.3](#23-guest-first-time-login-set-password). Keep the full preview object in memory.

**Step 2a — Set password (EZOFIS only)** or **Step 2b — Social login (Google/Microsoft)**

See [section 2.3](#23-guest-login--password-or-social-google--microsoft).

**Step 3 — Open shared file (same page as normal file view)**

```http
GET /api/repositories/{sourceRepositoryId}/items/{sourceItemId}/workspace?sharedtoken={shareToken}
Authorization: Bearer {accessToken}
X-Tenant-Id: {sourceTenantId}
```

Also call item, file, timeline, comments with the same `sharedtoken` param if your file view page needs them.

**Suggested navigation after set-password:**

```
/repository/{sourceRepositoryId}/items/{sourceItemId}?shareToken={shareToken}
```

On page load: read `shareToken` from URL/state and pass to all existing repository fetch functions.

**Step 4 — Discover workflow (guest only has `workflowInstanceId` from preview)**

```http
GET /api/workflow/listByUserId/
Authorization: Bearer {accessToken}
X-Tenant-Id: {sourceTenantId}
```

Pick the workflow where `inboxCount > 0` (or match by name). Use its `id` as `workflowId` for the inbox call.

**Step 5 — Load assigned inbox task**

```http
GET /api/workflows/inbox?workflowId={workflowId}&instanceId={workflowInstanceId}
Authorization: Bearer {accessToken}
X-Tenant-Id: {sourceTenantId}
```

The guest should see exactly one open task (reassigned during share). Read `activityId` from the inbox row for move-next.

**Step 6 — Verify / approve**

```http
POST /api/workflows/instances/{workflowInstanceId}/move-next
Authorization: Bearer {accessToken}
X-Tenant-Id: {sourceTenantId}
Content-Type: application/json
```

```json
{
  "activityId": "VERIFY_STEP",
  "review": "Approve",
  "comments": "Verified externally"
}
```

| Field | Value source |
|-------|----------------|
| `instanceId` (path) | `workflowInstanceId` from share preview |
| `activityId` | inbox row `activityId` (or `inboxAssignment.activityId` if sharer stored it) |
| `review` | `"Approve"` or `"Reject"` per your workflow UI |

**Response `200`:** Standard move-next result — refresh inbox / redirect to confirmation.

#### Return visit (guest already has password)

Guest opens sign-in URL again or logs in normally:

```http
POST /api/auth/ezofis/login
X-Tenant-Id: {sourceTenantId}
Content-Type: application/json
```

```json
{
  "email": "verifier@external.com",
  "password": "SecurePass123!"
}
```

Then either:

**Option A — Shared with me list (file access without email link):**

```http
GET /api/repositories/shared-with-me
Authorization: Bearer {accessToken}
X-Tenant-Id: {sourceTenantId}
```

Use `shareToken`, `sourceRepositoryId`, `sourceItemId` from each row.

**Option B — Workflow inbox (pending verify task):**

```http
GET /api/workflows/inbox?workflowId={workflowId}
Authorization: Bearer {accessToken}
X-Tenant-Id: {sourceTenantId}
```

#### Frontend page flow (recommended UX)

```
Sign-in URL (?shareToken & email)
    → Share preview API
    → if requiresPasswordSetup → Set Password form → POST share/set-password
    → if requiredSocialProvider → Google/Microsoft OAuth → POST share/social-login
    → JWT + redirect to file view page
    → Load workspace/file with sharedtoken
    → Show Verify / Reject buttons → POST move-next
```

#### Errors after set-password

| Status | Cause | Action |
|--------|-------|--------|
| `401` | JWT expired | Re-login with `POST /api/auth/ezofis/login` |
| `403` | Wrong recipient, expired share, or write on shared file | Show error; sharer may need to re-share |
| `404` | Repository/item not found without share token | Ensure `sharedtoken` is on every repo API call |

---

### 2.5 Guest views shared file (reuse existing repository APIs)

Pass `shareToken` on **existing** repository routes — no new file-view APIs.

```http
GET /api/repositories/{sourceRepositoryId}/items/{sourceItemId}?sharedtoken={shareToken}
GET /api/repositories/{sourceRepositoryId}/items/{sourceItemId}/workspace?sharedtoken={shareToken}
GET /api/repositories/{sourceRepositoryId}/items/{sourceItemId}/file?sharedtoken={shareToken}&disposition=inline
Authorization: Bearer <jwt>
X-Tenant-Id: <sourceTenantId>
```

Also supported: query param `shareToken` (camelCase) or header `X-Share-Token`.

**Read-only:** POST/PUT/DELETE on shared items return `403`.

---

### 2.6 Guest workflow inbox & verify/approve

After login, the guest sees the reassigned task in the normal inbox API.

**List inbox (filter by workflow):**

```http
GET /api/workflows/inbox?workflowId={workflowId}
Authorization: Bearer <jwt>
X-Tenant-Id: <tenant-guid>
```

Use `workflowId` from `inboxAssignment.workflowId` in the share response (or from share preview `workflowInstanceId` + workflow context).

**Verify / approve (move to next step):**

```http
POST /api/workflows/instances/{instanceId}/move-next
Authorization: Bearer <jwt>
X-Tenant-Id: <tenant-guid>
Content-Type: application/json
```

```json
{
  "activityId": "VERIFY_STEP",
  "review": "Approve",
  "comments": "Verified externally"
}
```

Use `activityId` from `inboxAssignment.activityId` when available.

---

### 2.7 Related repository share APIs (non-workflow)

For **cross-tenant** repository sharing (no inbox assignment), see the main guide:

| Action | Method | Endpoint |
|--------|--------|----------|
| Create share | `POST` | `/api/repositories/{repositoryId}/items/{itemId}/share` |
| Shared with me | `GET` | `/api/repositories/shared-with-me` |
| Share preview | `GET` | `/api/repositories/share/{shareToken}/preview` |
| Revoke share | `DELETE` | `/api/repositories/share/{shareId}` |

Full repository share details: `docs/FRONTEND_TEAM_API_GUIDE.md` (section 1).

**Difference:**

| Feature | Repository share | Workflow inbox share |
|---------|------------------|---------------------|
| Endpoint | `POST .../items/{itemId}/share` | `POST .../instances/{instanceId}/share-file` |
| Guest user | Optional | Always provisioned |
| Inbox assignment | No | Yes — open task reassigned to guest |
| Workflow link | No | `workflowInstanceId` stored on share |

---

### 2.8 Frontend checklist (workflow verify concept)

- [ ] **Share button** on workflow inbox/detail → `POST /api/workflows/instances/{instanceId}/share-file`
- [ ] Preview API → branch on `requiresPasswordSetup` vs `requiredSocialProvider`
- [ ] EZOFIS guest → set-password form → `POST /api/auth/share/set-password`
- [ ] Google/Microsoft user → existing OAuth UI → `POST /api/auth/share/social-login`
- [ ] Redirect to **same file view page** with `shareToken` in URL/state
- [ ] Load file via existing repo APIs + `?sharedtoken=`
- [ ] `GET /api/workflow/listByUserId/` → find workflow → `GET /api/workflows/inbox?workflowId=&instanceId=`
- [ ] Verify/approve: `POST /api/workflows/instances/{instanceId}/move-next`
- [ ] **Return visit:** normal `POST /api/auth/ezofis/login` + `GET shared-with-me` or inbox

---

## 3. Quick reference

### Credits

| Action | Method | Endpoint |
|--------|--------|----------|
| Consume credits | `POST` | `/api/billing/credits/update` |
| Current master row | `GET` | `/api/billing/credits/master` |
| Usage dashboard | `POST` | `/api/billing/credits/usage` |

### Workflow file share (verify)

| Action | Method | Endpoint |
|--------|--------|----------|
| Share inbox file + assign guest | `POST` | `/api/workflows/instances/{instanceId}/share-file` |
| Share preview | `GET` | `/api/repositories/share/{shareToken}/preview` |
| Guest set password (EZOFIS) | `POST` | `/api/auth/share/set-password` |
| Guest social login (share link) | `POST` | `/api/auth/share/social-login` |
| Social login (return visit) | `POST` | `/api/auth/social/login` |
| Workflow list (find workflowId) | `GET` | `/api/workflow/listByUserId/` |
| Normal login (return visit) | `POST` | `/api/auth/ezofis/login` |
| Shared with me (return visit) | `GET` | `/api/repositories/shared-with-me` |
| Guest inbox | `GET` | `/api/workflows/inbox?workflowId={id}` |
| Verify / approve | `POST` | `/api/workflows/instances/{instanceId}/move-next` |
| View shared file | `GET` | `/api/repositories/{id}/items/{itemId}/file?sharedtoken=...` |

---

## 4. Database / deployment notes

### Credits

1. Run against **catalog DB**: `src/Api/scripts/AddCreditMaster.sql`
   - Creates `dbo.creditMaster` if missing
   - Backfills default rows for all tenants in `catalog.Tenants`
2. Configure default signup allocation in `appsettings` → `TenantDefaultCredit` section
3. Transaction tables `dbo.creditTransaction_MMyy` are created automatically on first credit update

### Workflow share

1. Catalog table `catalog.RepositoryItemShares` is created on first share (or run catalog migration scripts)
2. Configure share link base URL in `appsettings` → `RepositoryShare`:

```json
"RepositoryShare": {
  "FrontendBaseUrl": "https://demoapp.ezofis.com",
  "SignInPath": "/sign-in",
  "DefaultExpiryDays": 30
}
```

3. Email delivery requires catalog `mailsettings` with `Preference = 1`

---

## Related docs

- `docs/FRONTEND_TEAM_API_GUIDE.md` — repository cross-tenant share, inbox/sent, AP Agent, bulk move-next
- `src/Api/scripts/AddCreditMaster.sql` — credit table + existing tenant backfill
