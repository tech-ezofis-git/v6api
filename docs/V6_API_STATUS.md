# ezSaaS API v6 — Completed APIs & Remaining Work

**Last updated:** June 8, 2026  
**Codebase:** `src/Api` (ASP.NET Core modular monolith)  
**Auth:** JWT (Ezofis / Azure AD / Auth0) + `X-Tenant-Id` header for multi-tenancy  
**Architecture:** Clean Architecture + CQRS (MediatR)

---

## Summary

| Area | Status | Endpoints |
|------|--------|-----------|
| Auth & tenancy | **Complete** | 12 |
| Users | **Complete** | 6 |
| Forms (designer) | **Complete** (v5-compatible) | 6 |
| Form entry (ezfb) | **Not started** | 0 |
| Upload & Index | **Not started** | 0 |
| Connectors | **Mostly complete** | 5 (no delete) |
| Workflows | **Mostly complete** | 35 |
| Repositories (STATIC) | **Mostly complete** | 24 (no delete) |
| Groups | **Not started** | 0 |
| DMS | **Basic** | 3 |
| Billing | **Not started** (skeleton module) | 0 |
| Reporting | **Not started** (skeleton module) | 0 |
| Infrastructure | **Complete** | 1 (`/health`) |

**Total in-scope HTTP endpoints: ~92** (3 legacy endpoints exist in code but are out of scope)

### Out of scope (not required for v6)

The following exist in the codebase but are **not part of the v6 target**. No further work planned:

- `POST /api/workflow/transaction`
- `POST /api/workflows/instances/{instanceId}/steps/{stepInstanceId}/approve`
- `POST /api/workflows/instances/{instanceId}/steps/{stepInstanceId}/reject`
- Related: approve/reject branching, all/any approval policy, full legacy transaction parity

Use **`move-next`** and **`actions`** for workflow progression instead.

---

## 1. Completed APIs (by module)

### 1.1 Authentication & tenancy

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| `POST` | `/api/signup` | None | Create tenant DB, catalog entry, optional admin user |
| `POST` | `/api/auth/ezofis/login` | None + `X-Tenant-Id` | Email/password login; returns JWT or 2FA temp token |
| `POST` | `/api/auth/2fa/complete` | None + `X-Tenant-Id` | Complete login after TOTP |
| `GET` | `/api/auth/tenants?email=` | None | Org picker before login |
| `POST` | `/api/auth/2fa/setup` | JWT | Start 2FA setup (QR code) |
| `POST` | `/api/auth/2fa/enable` | JWT | Enable 2FA with verification code |
| `POST` | `/api/auth/2fa/disable` | JWT | Disable 2FA with verification code |
| `GET` | `/api/me/tenants` | JWT | Organizations for logged-in user |
| `POST` | `/api/admin/tenants` | Admin | Register tenant in catalog |
| `GET` | `/api/admin/tenants/{id}` | Admin | Get tenant from catalog |
| `POST` | `/api/tenant/checkAuthenticate` | None | Legacy OTP flow — check email |
| `POST` | `/api/tenant/validateOTP` | None | Legacy OTP flow — validate code |
| `GET` | `/health` | None | Health check |

---

### 1.2 Users

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| `POST` | `/api/users` | Admin | Create user (optional password for Ezofis login) |
| `GET` | `/api/users` | TenantUser | List users |
| `GET` | `/api/users/{id}` | TenantUser | Get user by ID |
| `PUT` | `/api/users/{id}` | Admin | Update user profile |
| `DELETE` | `/api/users/{id}` | Admin | Soft-delete user |
| `GET` | `/api/usersession` | TenantUser | Current user session/profile |

---

### 1.3 Forms (v5-compatible designer API)

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| `POST` | `/api/form` | TenantUser | Create form from designer JSON |
| `GET` | `/api/form/all` | TenantUser | List forms (id + name) |
| `POST` | `/api/form/all` | TenantUser | Filter/sort/group/paginate forms |
| `GET` | `/api/form/{id}` | TenantUser | Get form with `formJson` from storage |
| `PUT` | `/api/form/{id}` | TenantUser | Update form from designer JSON |
| `DELETE` | `/api/form/{id}` | TenantUser | Soft-delete form |

---

### 1.4 Connectors

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| `POST` | `/api/connector` | TenantUser | Create connector |
| `PUT` | `/api/connector/{id}` | TenantUser | Update connector |
| `GET` | `/api/connector/all` | TenantUser | List all active connectors |
| `POST` | `/api/connector/all` | TenantUser | List with filters |
| `GET` | `/api/connector/{id}` | TenantUser | Get connector by ID |

---

### 1.5 Workflows

#### Schema & definition (admin)

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| `POST` | `/api/workflows/setup-schema` | Admin | Apply workflow schema to tenant DB |
| `POST` | `/api/workflows` | Admin | Create workflow (simple or full designer JSON) |
| `GET` | `/api/workflows` | TenantUser | List workflows |
| `GET` | `/api/workflows/{id}` | TenantUser | Get workflow + steps + designer JSON |
| `PUT` | `/api/workflows/{id}` | Admin | Update workflow definition |
| `DELETE` | `/api/workflows/{id}` | Admin | Soft-delete workflow |
| `POST` | `/api/workflows/{id}/sync-steps` | Admin | Re-sync steps from blob JSON |
| `POST` | `/api/workflows/{id}/steps` | Admin | Add step to draft workflow |
| `POST` | `/api/workflows/{id}/publish` | Admin | Publish workflow (creates per-workflow tables) |
| `POST` | `/api/workflows/{id}/sla` | Admin | Set SLA policy |

#### v5-compatible list & inbox APIs

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| `POST` | `/api/workflow/all` | TenantUser | Filter/sort/group/paginate workflows |
| `GET` | `/api/workflow/listByUserId/{wId?}` | TenantUser | Workflows for user with inbox/sent/completed counts |
| `POST` | `/api/workflow/inboxList/{id}` | TenantUser | Inbox transactions with filter/sort/group |
| `GET` | `/api/workflows/{workflowId}/filter-fields` | TenantUser | Form filter schema (`dataType` + operators; date/number include `between`) |
| `GET` | `/api/workflows/{workflowId}/control-values/{controlName}` | TenantUser | Distinct values for a form control |
| `POST` | `/api/workflows/{workflowId}/filter/search` | TenantUser | Form-field filtered ticket search (see filterBy notes below) |

**`filter/search` `filterBy`:** each clause is `{ criteria, condition, value, valueTo?, dataType? }`. For `dataType: "date"`, `value` is a string and ranges use `condition: "between"` with `valueTo`. For other types, `value` may be a string/number or a JSON array (e.g. `in`). Legacy string-only `value` without `dataType`/`valueTo` still works.

#### Instance lifecycle

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| `POST` | `/api/workflows/{id}/start` | TenantUser | Start instance (multipart + optional file; triggers AP Agent) |
| `POST` | `/api/workflows/{id}/start/json` | TenantUser | Start instance (JSON + optional base64 attachment) |
| `GET` | `/api/workflows/{id}/instances` | TenantUser | List instances for workflow |
| `GET` | `/api/workflows/{workflowId}/instances/{instanceId}` | TenantUser | Get instance + step instances |
| `GET` | `/api/workflows/{workflowId}/instances/{instanceId}/history` | TenantUser | Instance history from transaction table |

#### Inbox / counts (legacy mailbox)

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| `GET` | `/api/workflows/counts` | TenantUser | Inbox / sent / completed counts |
| `GET` | `/api/workflows/inbox/counts-by-workflow` | TenantUser | Inbox count per workflow |
| `GET` | `/api/workflows/instance-count?workflowId=` | TenantUser | Per-workflow mailbox counts |
| `GET` | `/api/workflows/inbox?workflowId=` | TenantUser | Inbox list (legacy mailbox) |
| `GET` | `/api/workflows/sent?workflowId=` | TenantUser | Sent list |
| `GET` | `/api/workflows/completed?workflowId=` | TenantUser | Completed list |

#### Instance actions

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| `POST` | `/api/workflows/instances/{instanceId}/comments` | TenantUser | Add comment |
| `GET` | `/api/workflows/instances/{instanceId}/comments` | TenantUser | List comments |
| `POST` | `/api/workflows/instances/{instanceId}/attachments` | TenantUser | Add attachment metadata |
| `GET` | `/api/workflows/instances/{instanceId}/attachments` | TenantUser | List attachments |
| `POST` | `/api/workflows/instances/{instanceId}/move-next` | TenantUser | Move to next stage (incl. AP Agent payload) |
| `POST` | `/api/workflows/instances/{instanceId}/actions` | TenantUser | Custom actions (hold, resume, cancel, etc.) |
| `PATCH` | `/api/workflows/{workflowId}/instances/{instanceId}/ap-agent/metadata` | TenantUser | Apply AP Agent invoice metadata to repo + form |

#### SLA

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| `GET` | `/api/workflows/instances/{instanceId}/sla` | TenantUser | SLA status for instance |
| `GET` | `/api/workflows/sla/breaches` | TenantUser | List SLA breaches / at-risk |

---

### 1.6 Repositories (STATIC document repositories)

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| `POST` | `/api/repositories/storage-providers/seed` | Admin | Seed EZOFIS / GCP / ONEDRIVE providers |
| `GET` | `/api/repositories/storage-providers` | TenantUser | List storage providers |
| `POST` | `/api/repositories` | Admin | Create repository + field schema |
| `GET` | `/api/repositories` | TenantUser | List repositories |
| `GET` | `/api/repositories/{id}` | TenantUser | Get repository definition |
| `PUT` | `/api/repositories/{id}` | Admin | Update repository |
| `POST` | `/api/repositories/{id}/provision-tables` | Admin | Ensure per-repo tables exist |
| `GET` | `/api/repositories/{id}/browse/structure` | TenantUser | Folder browse structure |
| `GET` | `/api/repositories/{id}/browse/children` | TenantUser | Next tree level |
| `GET` | `/api/repositories/{id}/browse/groups/{fieldName}` | TenantUser | Group by folder field |
| `GET` | `/api/repositories/{id}/items/filter-fields` | TenantUser | Allowed filter keys |
| `GET` | `/api/repositories/{id}/items` | TenantUser | Paged item list (cursor support) |
| `GET` | `/api/repositories/{id}/items/facets/{fieldName}` | TenantUser | Facet values for filtering |
| `GET` | `/api/repositories/{id}/items/{itemId}` | TenantUser | Get item |
| `GET` | `/api/repositories/{id}/items/{itemId}/workspace` | TenantUser | Document workspace (panels + line items) |
| `GET` | `/api/repositories/{id}/items/{itemId}/timeline` | TenantUser | Item activity timeline |
| `POST` | `/api/repositories/{id}/items/{itemId}/timeline` | TenantUser | Add timeline event |
| `GET` | `/api/repositories/{id}/items/{itemId}/comments` | TenantUser | Item comments |
| `POST` | `/api/repositories/{id}/items/{itemId}/comments` | TenantUser | Add item comment |
| `POST` | `/api/repositories/{id}/items` | TenantUser | Create item (metadata) |
| `PATCH` | `/api/repositories/{id}/items/{itemId}/metadata` | TenantUser | Update item metadata |
| `POST` | `/api/repositories/{id}/items/upload` | TenantUser | Upload file (flat path) |
| `POST` | `/api/repositories/{id}/items/upload-archive` | TenantUser | Upload with archive folder layout |
| `GET` | `/api/repositories/{id}/items/{itemId}/file` | TenantUser | Download / inline view file |

---

### 1.7 DMS (Document Management System)

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| `POST` | `/api/dms/setup-schema` | Admin | Apply DMS schema |
| `GET` | `/api/dms/repositories/{repositoryId}/folders/children` | TenantUser | Folder tree children |
| `GET` | `/api/dms/repositories/{repositoryId}/documents` | TenantUser | Documents in folder (paged) |

---

## 2. Partially complete (works but limited)

| Feature | What works | Gap |
|---------|------------|-----|
| Per-workflow instance tables | `WorkflowInstances_{suffix}` created on publish; `WorkflowInstanceStore` writes to them | Shared lookup table still used for some queries |
| Read/unread | `WorkflowInstanceUserState_{suffix}` table created on publish | **No API** to mark read or return `isRead` / `isActioned` in inbox |
| Workflow superuser | Superuser list stored in workflow JSON on create | **No cross-tenant superuser role** or tenant override APIs |
| AP Agent | Start with file, metadata patch, move-next integration | Depends on external Python service configuration |
| DMS | Basic folder/document browse | Uses sample table name by default; not fully aligned with STATIC repositories |

---

## 3. Needed APIs (not implemented)

> **v5 reference:** `D:\Controllers\TRIAL_14_05_26\v5APIMain\v5APIMain\Controllers\`  
> Key files: `RepositoryController.cs`, `FormController.cs`, `UserController.cs`, `UploadAndIndexController.cs`, `FileController.cs`, `GroupController.cs`

### 3.1 High priority (v5 parity / UI blockers)

| Priority | v5 endpoint | v6 target | Status |
|----------|-------------|-----------|--------|
| **P0** | `POST /api/form/{id}/entry/{entryId}` | `POST /api/form/{id}/entry` | ❌ Form entry create/update (ezfb) |
| **P0** | `POST /api/form/{id}/entry/all` | `POST /api/form/{id}/entry/all` | ❌ List/filter form entries |
| **P0** | `GET /api/form/{id}/entry/{entryId}` | `GET /api/form/{id}/entry/{entryId}` | ❌ Get single form entry |
| **P0** | `POST /api/uploadAndIndex/upload` | Upload queue module | ❌ Stage file before index/archive |
| **P0** | `POST /api/uploadAndIndex/upload/all` | Upload list with filter/pagination | ❌ Upload inbox list |
| **P0** | `POST /api/uploadAndIndex/index/all` | Index queue list | ❌ Index inbox list |
| **P0** | `PUT /api/uploadAndIndex/index/{id}` | Index metadata + archive | ❌ Index file into repository |
| **P0** | `POST /api/repository/all` | `POST /api/repositories/all` | ❌ Repository list with filter/sort/group/security |
| **P0** | `POST /api/user/all` | `POST /api/users/all` | ❌ User list with filter/sort/group |
| **P0** | `POST /api/group/all` | `POST /api/groups/all` | ❌ Group list with filter |
| **P0** | `POST /api/group` | `POST /api/groups` | ❌ Create group |
| **P0** | `GET /api/group/{id}` | `GET /api/groups/{id}` | ❌ Get group |
| **P0** | `PUT /api/group/{id}` | `PUT /api/groups/{id}` | ❌ Update group |
| **P1** | `POST /api/form/{id}/deleteEntry` | Form entry soft-delete | ❌ |
| **P1** | `POST /api/form/{id}/restoreEntry` | Form entry restore | ❌ |
| **P1** | `GET /api/uploadAndIndex/upload/{id}` | Get upload batch/file | ❌ |
| **P1** | `PUT /api/uploadAndIndex/upload/{id}` | Update upload record | ❌ |
| **P1** | `POST /api/uploadAndIndex/upload/setStatus` | Upload status workflow | ❌ |
| **P1** | `POST /api/uploadAndIndex/index/setStatus` | Index status workflow | ❌ |
| **P1** | `GET /api/uploadAndIndex/index/{id}` | Get index record | ❌ |
| **P1** | `GET /api/uploadAndIndex/indexByUpload/{id}/{type}` | Index by upload id | ❌ |
| **P1** | `POST /api/uploadAndIndex/uploadforStaticMetadata` | Static metadata upload | ⚠️ Partial — use `items/upload` + metadata |
| **P1** | `GET /api/uploadAndIndex/getMetaDataForStatic/{tId}/{fileId}` | Static file metadata | ⚠️ Partial — `GET .../items/{itemId}` |
| **P1** | `POST /api/repository/list` | Simple repository name list | ❌ |
| **P1** | `POST /api/repository/controlList` | Repository field controls | ❌ |
| **P1** | `POST /api/repository/uniqueColumnValues` | Distinct column values | ⚠️ Partial — `items/facets/{fieldName}` |
| **P1** | `GET /api/repository/history/{id}` | Repository change history | ❌ |
| **P1** | `POST /api/user/list` | Simple user picker list | ❌ |
| **P1** | `POST /api/user/workflowUser/list/{wId}` | Users for workflow step | ❌ |
| **P1** | `POST /api/user/importUser` | Bulk user import | ❌ |
| **P1** | `POST /api/user/avatar` | Upload user avatar | ❌ |
| **P1** | `GET /api/user/avatar/{tId}/{id}` | Get user avatar | ❌ |
| **P1** | `DELETE /api/repositories/{id}` | Soft-delete repository | ❌ |
| **P1** | `DELETE /api/repositories/{id}/items/{itemId}` | Remove repository item | ❌ |
| **P1** | `DELETE /api/connector/{id}` | Soft-delete connector | ❌ |
| **P0** | `POST /api/workflows/instances/{id}/mark-read` | Read/unread inbox | ❌ Table exists, API missing |
| **P0** | Inbox `isRead` / `isActioned` fields | Mailbox list responses | ❌ |

### 3.2 Medium priority (platform & admin)

| Priority | API | Why needed |
|----------|-----|------------|
| **P2** | Superuser: `GET /api/admin/tenants` (list all) | Cross-tenant admin view per requirements spec |
| **P2** | Superuser tenant context override | `?tenantId=` or `X-Tenant-Id` override for superuser role |
| **P2** | `POST /api/auth/forgot-password` / `reset-password` | No password recovery flow |
| **P2** | `PUT /api/users/{id}/password` | Admin or self-service password change |
| **P2** | Workflow security APIs | Manage `WorkflowUsers` / `WorkflowSecurity` assignees outside designer JSON |
| **P2** | `GET /api/workflows/{id}/designer` or export | Dedicated export endpoint (today: included in GET by id) |
| **P2** | Repository item version APIs | File versioning columns exist; no version list/restore API |
| **P2** | Webhook / notification APIs | SLA breach notifications configured but no outbound webhook API |

### 3.3 Low priority / future modules

| Priority | API | Why needed |
|----------|-----|------------|
| **P3** | Billing module APIs | Module skeleton only — subscriptions, invoices, usage |
| **P3** | Reporting module APIs | Module skeleton only — dashboards, exports |
| **P3** | `GET /api/dms/...` expanded | Full DMS parity with STATIC repositories |
| **P3** | Connector test/run endpoint | Execute connector action from API |
| **P3** | Bulk operations | Bulk upload, bulk metadata update |
| **P3** | Search API | Cross-repository / cross-workflow global search |

---

## 4. Module implementation status

```
✅ BuildingBlocks   Multi-tenancy, Security, Logging, Blob Storage
✅ Users            Full CRUD + session
✅ Workflow         Create, publish, start, inbox, move-next, SLA, legacy mailbox
✅ Repository       STATIC repos, browse, upload, workspace, timeline, comments
✅ Forms            Designer CRUD (v5-compatible)
❌ Form entry       ezfb submit/list/get — v5 parity needed
❌ Upload & Index   Staging upload → index → archive pipeline
❌ Groups           Group CRUD + list/all
✅ Connectors       CRUD minus delete
✅ DMS              Basic schema + folder browse
✅ Auth             Ezofis login, 2FA, signup, tenant registry
➖ Legacy bridge    transaction / approve / reject — out of scope for v6
❌ Billing          Project files only — no endpoints
❌ Reporting        Project files only — no endpoints
```

---

## 5. How to verify completed APIs

1. Start API: `dotnet run --project src/Api`
2. Open Swagger: `https://localhost:5001/swagger`
3. Health: `GET https://localhost:5001/health`
4. Test scripts: `TEST_WORKFLOW_API.md`, `QUICK_TEST.md`

**Typical test flow:**
1. `POST /api/signup` → get `tenantId`
2. `POST /api/auth/ezofis/login` with `X-Tenant-Id`
3. Use JWT + `X-Tenant-Id` for all tenant-scoped calls

---

## 6. Related documentation

| Document | Purpose |
|----------|---------|
| `README.md` | Project setup and run instructions |
| `docs/WORKFLOW_REQUIREMENTS_SPEC.md` | Workflow design notes (read/unread, superuser; branching not in v6 scope) |
| `docs/WORKFLOW_CREATE_PAYLOAD_EXAMPLE.md` | Sample create-workflow payload |
| `TEST_WORKFLOW_API.md` | Workflow API testing guide |
| `PPT_DIAGRAMS.md` | Architecture diagrams |
| `D:\Controllers\TRIAL_14_05_26\v5APIMain` | v5 API reference for migration gap (Section 8) |

---

## 7. Recommended implementation order (remaining work)

1. **Form entry APIs** — `POST/GET entry`, `entry/all`, delete/restore (v5 `FormController` parity)
2. **Upload & Index module** — upload queue, index queue, status, link to repository archive
3. **Repository `POST /all`** — filter/sort/group + security (v5 `repository/all`)
4. **User `POST /all`** + avatar + workflow user list + import
5. **Group CRUD** — `POST/GET/PUT /api/groups`, `POST /all`, `POST /list`
6. **Mark-read API** + inbox `isRead`/`isActioned`
7. **Delete APIs** — connector, repository, item
8. **Superuser / cross-tenant admin**
9. **Billing & Reporting** modules

---

## 8. v5 → v6 migration gap (detailed)

**Reference path:** `D:\Controllers\TRIAL_14_05_26\v5APIMain\v5APIMain\Controllers\`

**URL note:** v5 uses singular routes (`/api/repository`, `/api/user`, `/api/form`). v6 uses plural where noted (`/api/repositories`, `/api/users`). New v6 endpoints should follow v6 naming but accept v5 request/response shapes where noted for UI compatibility.

**Legend:** ✅ Done in v6 · ⚠️ Partial · ❌ Not in v6 · ➖ Out of scope

---

### 8.1 Repository (`RepositoryController.cs` → `RepositoriesController.cs`)

| v5 endpoint | Purpose | v6 equivalent | Status |
|-------------|---------|---------------|--------|
| `POST /api/repository` | Create repository | `POST /api/repositories` | ✅ |
| `PUT /api/repository/{id}` | Update repository | `PUT /api/repositories/{id}` | ✅ |
| `GET /api/repository/{id}` | Get repository | `GET /api/repositories/{id}` | ✅ |
| `POST /api/repository/all` | List with filter/sort/group/security | `GET /api/repositories` (simple list only) | ⚠️ Need `POST /all` |
| `POST /api/repository/list` | Lightweight name/id list | — | ❌ |
| `POST /api/repository/controlList` | Field control definitions | — | ❌ |
| `POST /api/repository/uniqueColumnValues` | Distinct values for filters | `GET .../items/facets/{fieldName}` | ⚠️ |
| `POST /api/repository/{id}/uniqueColumnValues` | Per-repo distinct values | `GET .../items/facets/{fieldName}` | ⚠️ |
| `GET /api/repository/history/{id}` | Repository audit history | — | ❌ |
| `POST /api/repository/dynamicFields` | Dynamic field schema | — | ❌ (v6 STATIC focus) |
| `POST /api/repository/FieldLevelCount/{tId}/{rId}` | Counts per folder field | `browse/groups/{fieldName}` | ⚠️ |
| `GET /api/repository/getOCRTextbyitemId/{tId}/{rId}` | OCR text for item | — | ❌ |
| `POST /api/repository/fileCountbyField/{tId}/{rId}` | File counts by field | — | ❌ |
| — | Browse folder tree | `GET .../browse/structure`, `.../children`, `.../groups` | ✅ |
| — | Item list / filter | `GET .../items`, `filter-fields`, `facets` | ✅ |
| — | Upload file | `POST .../items/upload`, `upload-archive` | ✅ |
| — | Item workspace / timeline / comments | workspace, timeline, comments APIs | ✅ |
| — | Download file | `GET .../items/{itemId}/file` | ✅ |
| — | Update metadata | `PATCH .../items/{itemId}/metadata` | ✅ |
| — | Delete repository / item | — | ❌ |

**Also in v5 `FileController.cs` (repository file ops):**

| v5 endpoint | Purpose | v6 | Status |
|-------------|---------|-----|--------|
| `POST /api/file/browse` | Browse files in repo | `browse/children` + `items` | ⚠️ |
| `POST /api/file/browseFolder` | Folder drill-down | `browse/children` | ✅ |
| `POST /api/file/browseFile` | Files in folder | `GET .../items` | ✅ |
| `POST /api/file/archive` | Archive file to repo | `items/upload-archive` | ⚠️ |
| `POST /api/file/indexValues` | Save index field values | `PATCH .../metadata` | ⚠️ |
| `POST /api/file/comments/{rId}/{id}` | Item comments | `POST .../comments` | ✅ |
| `GET /api/file/comments/{rId}/{id}` | Get comments | `GET .../comments` | ✅ |
| `POST /api/file/getVersion` | File versions | — | ❌ |
| `GET /api/file/versionHistory/{rId}/{itemId}/{wId}` | Version history | — | ❌ |
| `POST /api/file/checkOut/{rId}/{itemId}` | Check-out lock | — | ❌ |
| `POST /api/file/metadata/all` | Metadata templates | — | ❌ |
| `POST /api/file/updateMetadata/{id}` | Update metadata | `PATCH .../metadata` | ✅ |
| `POST /api/file/retention/*` | Retention policies | — | ❌ |
| `POST /api/file/sharelink/` | Share link | — | ❌ |
| `GET /api/file/view/{tId}/{uId}/{rId}/{id}/{type}` | View file inline | `GET .../file?disposition=inline` | ⚠️ |

---

### 8.2 Form designer & form entry (`FormController.cs` → `FormController.cs`)

#### Form designer (done)

| v5 endpoint | v6 equivalent | Status |
|-------------|---------------|--------|
| `POST /api/form` | `POST /api/form` | ✅ |
| `PUT /api/form/{id}` | `PUT /api/form/{id}` | ✅ |
| `GET /api/form/{id}` | `GET /api/form/{id}` | ✅ |
| `POST /api/form/all` | `POST /api/form/all` | ✅ |
| `POST /api/form/list` | — | ❌ Simple list |
| `GET /api/form/uid/{uid}` | — | ❌ Lookup by UID |
| `GET /api/form/history/{id}` | — | ❌ Form audit history |
| `POST /api/form/controlList` | — | ❌ Form control list |
| `POST /api/form/uniqueColumnValues` | — | ❌ |
| `GET /api/form/designJson/{tId}/{id}` | Included in `GET /api/form/{id}` | ✅ |

#### Form entry (needed — core gap)

| v5 endpoint | Purpose | v6 | Status |
|-------------|---------|-----|--------|
| `POST /api/form/{id}/entry/{entryId}` | Create (0) or update entry | — | ❌ **P0** |
| `POST /api/form/{id}/entry/all` | List/filter entries | — | ❌ **P0** |
| `GET /api/form/{id}/entry/{entryId}` | Get one entry | — | ❌ **P0** |
| `POST /api/form/{id}/deleteEntry/{type}` | Soft-delete entry | — | ❌ |
| `POST /api/form/{id}/restoreEntry` | Restore deleted entry | — | ❌ |
| `POST /api/form/uploadFile` | Upload file for form field | — | ❌ |
| `POST /api/form/uniqueColumnValues` | Distinct entry column values | — | ❌ |
| `GET /api/form/entrylist/{wId}/{pId}` | Entries for workflow process | — | ❌ |
| `POST /api/form/taskComments/{fId}/{entryId}` | Task comments on entry | — | ❌ |
| `GET /api/form/taskComments/{fId}/{entryId}` | Get task comments | — | ❌ |
| `POST /api/form/taskAttachmentWithEntryId` | Attach file to entry | — | ❌ |
| `GET /api/form/taskAttachmentList/{fId}/{entryId}` | List entry attachments | — | ❌ |
| `POST /api/form/linkTaskItem` | Link entry to repository item | — | ❌ |
| `POST /api/form/taskWorkflow` | Start task workflow from entry | — | ❌ |
| `POST /api/form/generatePdf` | Generate PDF from entry | — | ❌ |
| `POST /api/form/documentGenerate` | Document generation | — | ❌ |

**v6 internal only today:** ezfb tables written during workflow start / AP Agent metadata — no public form-entry REST API.

---

### 8.3 Users (`UserController.cs` → `UsersController.cs`)

| v5 endpoint | Purpose | v6 equivalent | Status |
|-------------|---------|---------------|--------|
| `POST /api/user` | Create user | `POST /api/users` | ✅ |
| `PUT /api/user/{id}` | Update user | `PUT /api/users/{id}` | ✅ |
| `GET /api/user/{id}` | Get user | `GET /api/users/{id}` | ✅ |
| `POST /api/user/all` | List with filter/sort/group | `GET /api/users` (no filters) | ⚠️ Need `POST /all` |
| `POST /api/user/list` | Simple picker list | — | ❌ |
| `GET /api/authentication/userSession` | Current session | `GET /api/usersession` | ✅ |
| `POST /api/user/avatar` | Upload avatar (multipart) | — | ❌ |
| `POST /api/user/avatarBinary` | Upload avatar (base64) | — | ❌ |
| `GET /api/user/avatar/{tId}/{id}` | Get avatar image | — | ❌ |
| `DELETE /api/user/avatar/{tId}/{id}` | Remove avatar | — | ❌ |
| `POST /api/user/signatureBinary/{tId}/{uId}` | Upload signature | — | ❌ |
| `GET /api/user/getSignature/{tId}/{id}` | Get signature | — | ❌ |
| `DELETE /api/user/removeSignature/{tId}/{id}` | Remove signature | — | ❌ |
| `POST /api/user/uniqueColumnValues` | Filter distinct values | — | ❌ |
| `GET /api/user/history/{id}` | User audit history | — | ❌ |
| `POST /api/user/workflowUser/list/{wId}` | Users assignable to workflow | — | ❌ |
| `POST /api/user/importUser` | Bulk import users | — | ❌ |
| `POST /api/user/language` | Set user language | ⚠️ `PUT /api/users/{id}` has `Language` field | ⚠️ |
| — | Delete user | `DELETE /api/users/{id}` | ✅ |
| — | Password change / forgot | — | ❌ |

---

### 8.4 Upload & Index (`UploadAndIndexController.cs` → new module)

v6 has **direct repository upload** only. v5 **Upload & Index** is a separate staging pipeline (upload → index → archive). This whole area needs a v6 module.

#### Upload queue

| v5 endpoint | Purpose | v6 | Status |
|-------------|---------|-----|--------|
| `POST /api/uploadAndIndex/upload` | Upload file(s) to staging | — | ❌ **P0** |
| `POST /api/uploadAndIndex/upload/all` | List uploads (filter/pagination) | — | ❌ **P0** |
| `GET /api/uploadAndIndex/upload/{id}` | Get upload record | — | ❌ |
| `PUT /api/uploadAndIndex/upload/{id}` | Update upload | — | ❌ |
| `POST /api/uploadAndIndex/upload/setStatus` | Change upload status | — | ❌ |
| `POST /api/uploadAndIndex/upload/deleteFiles` | Delete staged files | — | ❌ |
| `POST /api/uploadAndIndex/uploadblob` | Upload to blob | — | ❌ |
| `POST /api/uploadAndIndex/upload_for_apagent` | AP Agent upload | `POST /api/workflows/{id}/start` (file) | ⚠️ |
| `POST /api/uploadAndIndex/uploadforStaticMetadata` | Upload + static metadata | `POST .../items/upload` | ⚠️ |
| `POST /api/uploadAndIndex/externalUpload` | External system upload | — | ❌ |

#### Index queue

| v5 endpoint | Purpose | v6 | Status |
|-------------|---------|-----|--------|
| `POST /api/uploadAndIndex/index/all` | List index jobs | — | ❌ **P0** |
| `GET /api/uploadAndIndex/index/{id}` | Get index record | — | ❌ |
| `PUT /api/uploadAndIndex/index/{id}` | Apply index + archive to repo | — | ❌ **P0** |
| `POST /api/uploadAndIndex/index/setStatus` | Index status | — | ❌ |
| `POST /api/uploadAndIndex/index/getStatus` | Get index status | — | ❌ |
| `POST /api/uploadAndIndex/index/deleteFiles` | Delete index files | — | ❌ |
| `GET /api/uploadAndIndex/indexByUpload/{id}/{type}` | Index rows for upload | — | ❌ |
| `POST /api/uploadAndIndex/uniqueColumnValues` | Filter values | — | ❌ |
| `POST /api/uploadAndIndex/setFieldList` | Index field mapping | — | ❌ |
| `GET /api/uploadAndIndex/getMetaDataForStatic/{tId}/{fileId}` | Static metadata | `GET .../items/{itemId}` | ⚠️ |

#### View / OCR / batch (lower priority)

| v5 endpoint | Purpose | v6 | Status |
|-------------|---------|-----|--------|
| `GET /api/uploadAndIndex/view/{tId}/{id}/{type}` | View staged file | `GET .../items/{itemId}/file` | ⚠️ |
| `GET /api/uploadAndIndex/viewThumbnail/{tId}/{id}/{type}` | Thumbnail | — | ❌ |
| `GET /api/uploadAndIndex/viewPath/{tId}/{id}/{type}` | File path | — | ❌ |
| `GET /api/uploadAndIndex/viewblob` | View from blob | — | ❌ |
| `POST /api/uploadAndIndex/extractTextFromFile` | OCR extract | — | ❌ |
| `POST /api/uploadAndIndex/OCRExtractionfromLocaltoServer` | OCR to server | — | ❌ |
| `POST /api/uploadAndIndex/batchProcess/*` | Batch processing | — | ❌ |
| `POST /api/uploadAndIndex/fileAI/*` | AI file processing | — | ❌ |
| `POST /api/uploadAndIndex/fileProcess/*` | File process records | — | ❌ |
| `POST /api/uploadAndIndex/splitFile/{fileId}/{splitType}` | Split PDF | — | ❌ |
| `POST /api/uploadAndIndex/load/{id}` | Load upload for indexing UI | — | ❌ |

**Suggested v6 Upload & Index flow:**

```
POST /api/upload-and-index/upload          → stage file (fileProcess table)
GET  /api/upload-and-index/upload          → list uploads
PUT  /api/upload-and-index/index/{id}      → set metadata + archive to repository
POST /api/repositories/{id}/items/upload   → (already exists) final archive path
```

---

### 8.5 Groups (`GroupController.cs` → needed)

| v5 endpoint | v6 target | Status |
|-------------|-----------|--------|
| `POST /api/group` | `POST /api/groups` | ❌ |
| `PUT /api/group/{id}` | `PUT /api/groups/{id}` | ❌ |
| `GET /api/group/{id}` | `GET /api/groups/{id}` | ❌ |
| `POST /api/group/all` | `POST /api/groups/all` | ❌ |
| `POST /api/group/list` | `POST /api/groups/list` | ❌ |
| `POST /api/group/uniqueColumnValues` | — | ❌ |
| `GET /api/group/history/{id}` | — | ❌ |

---

### 8.6 Out of scope (per v6 plan — no migration)

| v5 area | Notes |
|---------|-------|
| `TransactionController` / `POST /api/workflow/transaction` | Use `move-next` + `actions` |
| Approve / reject step APIs | Not required in v6 |
| `PaymentController`, `LicenseController` | Billing module later |
| `ReportController`, `DashboardController` | Reporting module later |
| `AIController`, `OCRController` (standalone) | Integrate via Upload & Index if needed |
