# Repository Folder & Document Security — Frontend Guide

**Audience:** Frontend team (Folder Security / Document Security wizards)  
**Last updated:** July 2026  
**Base URL example:** `https://localhost:44311` or `https://your-host/V6API`

---

## 1. Overview

Two security layers on a **repository** (in this product, **repository = folder** for security):

| Layer | UI screen | Purpose |
|-------|-----------|---------|
| **Folder Security** | Folder Security Policy | Grant users/groups permission flags on the whole repository |
| **Document Security** | Document Security Rule | Override visibility for documents matching metadata (Hide / Grant) |

| Role | Behavior |
|------|----------|
| **Admin** | Full access; ACL not applied on list/get/items |
| **TenantUser** | Access filtered by folder policies + document rules |

### Default behavior (important)

| Situation | TenantUser sees |
|-----------|-----------------|
| Repository has **no** folder policies | **All** repositories open (same as before security) |
| Repository **has** folder policies | Only users/groups in those policies (with matching flags) |
| Document **hide** rule matches | Those files hidden for target users |
| Document **grant** rule matches | Those files shown to target users (even without folder View) |

---

## 2. Common headers

| Header | Required | Notes |
|--------|----------|-------|
| `Authorization: Bearer <JWT>` | Yes | Admin for `PUT`; Admin or TenantUser for `GET` |
| `X-Tenant-Id: <tenant-guid>` | Yes | Tenant context |
| `Content-Type: application/json` | Yes for `PUT` | |

---

## 3. Folder Security API

Maps to UI: **Users → Permissions → Review**

### 3.1 Save folder security (replace all policies)

```http
PUT /api/repositories/{repositoryId}/security/folder
Authorization: Bearer <admin-jwt>
X-Tenant-Id: <tenant-guid>
Content-Type: application/json
```

**Auth:** Admin only (`403` if TenantUser).

**Input body**

```json
{
  "folderId": null,
  "policies": [
    {
      "userIds": ["9705c8cb-caff-4d37-90ab-41bf20183437"],
      "groupIds": [],
      "permissions": {
        "view": true,
        "upload": false,
        "download": true,
        "print": false,
        "delete": false,
        "editMetadata": false,
        "editDocument": false,
        "checkOut": false,
        "checkIn": false,
        "sendForSignature": false
      }
    }
  ]
}
```

#### Input field possibilities

| Field | Type | Required | Allowed / notes |
|-------|------|----------|-----------------|
| `folderId` | `guid` \| `null` | No | **Always send `null`**. Repository **is** the folder. Do **not** put repository id here. API ignores/normalizes it. |
| `policies` | array | Yes | Full replace. `[]` clears all policies → repo becomes open again for TenantUsers. |
| `policies[].userIds` | `guid[]` | At least one of userIds/groupIds | Tenant user ids from Users API. Valid GUID strings, **no** `{ }` wrappers. |
| `policies[].groupIds` | `guid[]` | At least one of userIds/groupIds | Group ids from `/api/users/groups`. |
| `policies[].permissions` | object | Yes | See permission flags below. |
| `policies[].folderId` | ignored | No | Optional on policy object; not used (repo-scoped). |

#### Permission flags (UI toggles)

| JSON key | UI label | Effect when `true` |
|----------|----------|---------------------|
| `view` | View | List/open repository, browse, list items |
| `upload` | Upload | Upload / create item |
| `download` | Download | Download / file stream (`view` also allows download fallback) |
| `print` | Print | Print permission (stored; enforce when UI uses it) |
| `delete` | Delete | Delete files/folders (stored for future delete APIs) |
| `editMetadata` | Edit Metadata | `PATCH .../metadata` |
| `editDocument` | Edit Document | Edit document content |
| `checkOut` | Check Out | Lock for editing |
| `checkIn` | Check In | Complete editing session |
| `sendForSignature` | Send for Signature | Create signature request |

**Notes**

- Each flag is boolean: `true` \| `false`.
- `view: false` **is allowed** and enforced (user does not get View from that policy).
- Multiple policies in one `PUT` = multiple user/group sets with different permission sets.
- `PUT` **replaces** all existing policies for that repository.

**Clear all policies (open repo again)**

```json
{
  "folderId": null,
  "policies": []
}
```

### 3.2 Get folder security

```http
GET /api/repositories/{repositoryId}/security/folder
Authorization: Bearer <jwt>
X-Tenant-Id: <tenant-guid>
```

Optional query: `?folderId=` (ignored for matching; repo-scoped).

**Output `200`**

```json
{
  "repositoryId": "d90c7392-a4a6-4b0c-834f-5268b5365272",
  "policies": [
    {
      "userIds": ["9705c8cb-caff-4d37-90ab-41bf20183437"],
      "groupIds": [],
      "permissions": {
        "view": true,
        "upload": false,
        "download": true,
        "print": false,
        "delete": false,
        "editMetadata": false,
        "editDocument": false,
        "checkOut": false,
        "checkIn": false,
        "sendForSignature": false
      },
      "folderId": null
    }
  ]
}
```

| Output field | Meaning |
|--------------|---------|
| `repositoryId` | Repository GUID |
| `policies` | Current policies (empty array = open for TenantUsers) |
| `policies[].userIds` / `groupIds` | Principals |
| `policies[].permissions` | Flags as saved |
| `policies[].folderId` | Always `null` in responses |

**Errors**

| Code | When |
|------|------|
| `401` | Missing/invalid JWT |
| `403` | `PUT` as non-Admin |
| `404` | Repository not found |

---

## 4. Document Security API

Maps to UI: **Build Rule → Users & Groups → Review**  
(Grant Access / Show Documents **or** Hide Documents + metadata conditions)

### 4.1 Save document security (replace all rules)

```http
PUT /api/repositories/{repositoryId}/security/documents
Authorization: Bearer <admin-jwt>
X-Tenant-Id: <tenant-guid>
Content-Type: application/json
```

**Auth:** Admin only.

**Input body — Hide example (Supplier filter)**

```json
{
  "rules": [
    {
      "action": "hide",
      "match": "all",
      "conditions": [
        {
          "field": "Supplier",
          "op": "equals",
          "value": "Gerrie Logistics Services Ltd"
        }
      ],
      "userIds": ["9705c8cb-caff-4d37-90ab-41bf20183437"],
      "groupIds": []
    }
  ]
}
```

**Input body — Grant example**

```json
{
  "rules": [
    {
      "action": "grant",
      "match": "all",
      "conditions": [
        {
          "field": "Supplier",
          "op": "equals",
          "value": "Gerrie Logistics Services Ltd"
        }
      ],
      "userIds": ["9705c8cb-caff-4d37-90ab-41bf20183437"],
      "groupIds": []
    }
  ]
}
```

**Multiple rules + multiple conditions**

```json
{
  "rules": [
    {
      "action": "hide",
      "match": "all",
      "conditions": [
        { "field": "Supplier", "op": "equals", "value": "Gerrie Logistics Services Ltd" },
        { "field": "Department", "op": "equals", "value": "HR" }
      ],
      "userIds": ["9705c8cb-caff-4d37-90ab-41bf20183437"],
      "groupIds": []
    },
    {
      "action": "grant",
      "match": "any",
      "conditions": [
        { "field": "Status", "op": "equals", "value": "Approved" },
        { "field": "Supplier", "op": "contains", "value": "Logistics" }
      ],
      "userIds": [],
      "groupIds": ["aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"]
    }
  ]
}
```

#### Input field possibilities

| Field | Type | Required | Allowed values / notes |
|-------|------|----------|-------------------------|
| `rules` | array | Yes | Full replace. `[]` clears all document rules. |
| `rules[].action` | string | Yes | `"hide"` \| `"grant"` (case-insensitive; stored lowercase) |
| `rules[].match` | string | Yes | `"all"` \| `"any"` |
| `rules[].conditions` | array | Yes (≥1) | Metadata conditions; empty rule skipped |
| `rules[].conditions[].field` | string | Yes | Metadata / list field name (see field list below) |
| `rules[].conditions[].op` | string | No | `"equals"` (default), `"notequals"`, `"contains"` (also `ne`, `!=`) |
| `rules[].conditions[].value` | string \| null | No | Compared case-insensitively for equals/notequals |
| `rules[].userIds` | `guid[]` | At least one of userIds/groupIds | Target users |
| `rules[].groupIds` | `guid[]` | At least one of userIds/groupIds | Target groups |

#### `action` possibilities

| Value | UI | Meaning |
|-------|-----|---------|
| `grant` | Grant Access / Show Documents | Show matching documents to targets |
| `hide` | Hide Documents | Hide matching documents from targets |

#### `match` possibilities

| Value | UI | Meaning |
|-------|-----|---------|
| `all` | Match **All** | Every condition must match |
| `any` | Match **Any** | At least one condition matches |

#### `op` possibilities

| Value | Meaning |
|-------|---------|
| `equals` | Case-insensitive equality (default) |
| `notequals` / `ne` / `!=` | Not equal |
| `contains` | Case-insensitive substring |

#### Suggested `field` names (common STATIC repo columns)

Use names that appear on item list / filter schema (`GET .../items/filter-fields`):

| Field | Typical use |
|-------|-------------|
| `Supplier` | Supplier name |
| `Department` | Department |
| `DocumentType` | Document type |
| `Status` | Status |
| `AiStatus` | AI status |
| `InvoiceNumber` | Invoice no |
| `PoNumber` | PO no |
| `FileName` | File name |
| `Currency` | Currency |
| `Buyer` | Buyer |
| `RiskLevel` | Risk |
| `Source` | Source |

Any repository metadata field name that exists on the item also works if present on the list/detail payload.

**Clear all document rules**

```json
{
  "rules": []
}
```

### 4.2 Get document security

```http
GET /api/repositories/{repositoryId}/security/documents
Authorization: Bearer <jwt>
X-Tenant-Id: <tenant-guid>
```

**Output `200`**

```json
{
  "repositoryId": "d90c7392-a4a6-4b0c-834f-5268b5365272",
  "rules": [
    {
      "action": "hide",
      "match": "all",
      "conditions": [
        {
          "field": "Supplier",
          "op": "equals",
          "value": "Gerrie Logistics Services Ltd"
        }
      ],
      "userIds": ["9705c8cb-caff-4d37-90ab-41bf20183437"],
      "groupIds": []
    }
  ]
}
```

**Errors:** same as folder (`401` / `403` on PUT / `404`).

---

## 5. How TenantUser APIs behave after security

| Endpoint | Folder check | Document check |
|----------|--------------|----------------|
| `GET /api/repositories` | Only accessible repos | — |
| `GET /api/repositories/{id}` | View (or grant-only) | — |
| `GET/POST .../items`, browse, facets | View (or grant-only) | Hide/grant filter on items |
| `GET .../items/{itemId}` | — | Item must be allowed |
| `GET .../workspace` | — | Item must be allowed |
| `GET .../file` | Download (or View) | Item must be allowed |
| `POST .../upload`, create item | Upload | — |
| `PATCH .../metadata` | EditMetadata | — |

| Response | Meaning |
|----------|---------|
| `200` | Allowed |
| `403` | `{ "error": "You do not have access to this repository." }` or `... document.` |
| `404` | Not found |

Share-token access bypasses ACL (existing share flow).

---

## 6. UI wizard → API mapping

### Folder Security

| UI step | API |
|---------|-----|
| Select users/groups | `userIds` / `groupIds` |
| Toggles View, Upload, Download, … | `permissions.*` |
| Save / Finish | `PUT .../security/folder` with full `policies` array |
| Load existing | `GET .../security/folder` |

### Document Security

| UI step | API |
|---------|-----|
| Grant Access / Hide Documents | `action`: `grant` \| `hide` |
| Match All / Any | `match`: `all` \| `any` |
| Field / Equals / Value rows | `conditions[]` |
| Target users/groups | `userIds` / `groupIds` |
| Save | `PUT .../security/documents` |
| Load | `GET .../security/documents` |

---

## 7. Quick test checklist

1. **Admin** `PUT` folder policy with `view: true` for a TenantUser on Test Repository.  
2. **TenantUser** `GET /api/repositories` → Test Repository appears (repos with no policy still appear).  
3. **Admin** `PUT` document hide: `Supplier` = `Gerrie Logistics Services Ltd` for that user.  
4. **TenantUser** `GET .../items` → that supplier’s files hidden.  
5. **Admin** `PUT` `"rules": []` → hide cleared.  
6. **Admin** `PUT` `"policies": []` → folder ACL cleared → open again.

---

## 8. Common mistakes

| Wrong | Right |
|-------|-------|
| `"folderId": "<repository-guid>"` | `"folderId": null` |
| `"userIds": ["{guid}"]` | `"userIds": ["guid"]` (no braces) |
| Expect only secured repos when others have no policy | Unsecured repos stay **open** until you add policies |
| `PUT` as TenantUser | Must use **Admin** JWT |
| Partial update of one policy | API **replaces entire** `policies` / `rules` array |

---

## 9. File invite share (Invite People) — same as workflow share

Uses the **same shareToken invite flow** as workflow inbox share: send URL → preview → set-password / login → open file with `shareToken`.

### 9.1 Create invite

```http
POST /api/repositories/{repositoryId}/items/{itemId}/share
Authorization: Bearer <jwt>
X-Tenant-Id: <tenant-guid>
Content-Type: application/json
```

```json
{
  "email": "guest@example.com",
  "message": "optional note",
  "action": 0
}
```

| `action` | UI | Meaning |
|----------|-----|---------|
| `0` | Can View | View/download shared file only |
| `1` | Can Edit | View + upload; guest also sees files they uploaded |

**Response `201`:**

```json
{
  "shareId": "...",
  "shareToken": "abc123...",
  "sourceRepositoryId": "...",
  "sourceItemId": "...",
  "sourceTenantId": "...",
  "recipientEmail": "guest@example.com",
  "expiresAtUtc": "...",
  "shareUrl": "https://demoapp.ezofis.com/sign-in?shareToken=abc123...&email=guest%40example.com&isnew=true",
  "guestUserId": "...",
  "action": 0,
  "isNew": true,
  "requiresPasswordSetup": true,
  "allowedAuthMethods": ["password_setup", "google", "microsoft"]
}
```

API also **emails** `shareUrl` to the recipient.

| Recipient | URL flag | What they do |
|-----------|----------|--------------|
| **New user** | `isnew=true` | Open link → set password **or** Google/Microsoft |
| **Existing user** | `isnew=false` | Open link → normal login |

### 9.2 Guest opens URL (identical to workflow)

```
1. Open shareUrl  (shareToken + email + isnew)
2. GET  /api/repositories/share/{shareToken}/preview     (anonymous)
3. New user → POST /api/auth/share/set-password
            OR POST /api/auth/share/social-login
   Old user → password_login / normal login
4. Keep shareToken in sessionStorage
5. Open file: GET .../items/{id}/workspace?sharedtoken={shareToken}
6. Can Edit: after JWT login, upload with X-Tenant-Id
   (guest only sees shared file + their uploads; Admin sees all)
```

Preview / set-password / social-login are the **same endpoints** as workflow share.

On invite the API also inserts document-security grants (`Source=Share`) and share-scopes the guest so they cannot see other repo files. Admin still sees all.

---

## 10. Related

- Schema scripts for existing tenants:  
  - `src/Api/scripts/AddRepositoryFolderDocumentSecurity.sql` (one DB)  
  - `src/Api/scripts/AddRepositoryFolderDocumentSecurityAllTenants.sql` (catalog → all tenants)
- Workflow share guest login detail: `docs/TEAM_API_GUIDE_CREDITS_AND_WORKFLOW_SHARE.md` §2
