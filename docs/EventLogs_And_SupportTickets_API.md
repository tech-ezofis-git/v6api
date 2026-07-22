# V6 API — Event Logs & Jira Support Tickets

**Base path:** `{host}/V6API` (from `PathBase`; adjust if your deploy differs)  
**Auth:** `Authorization: Bearer {JWT}`  
**Tenant:** `X-Tenant-Id: {tenant-guid}` (required for tenant-scoped calls)

---

## 1. Event Logs API

### Overview

Paginated list of event-log rows for the **current tenant**. Used by the admin Event Log UI.

Records mutations and meaningful actions (login, create/update/delete, workflow start/approve/move-next, uploads/shares). **GET/view/list and search traffic is omitted** at write time and filtered out of this list.

| Item | Value |
|------|--------|
| **Method** | `GET` |
| **URL** | `/api/event-logs` |
| **Auth** | Admin policy (`Admin` role) |
| **Feature flag** | If `EventLog:Enabled` is `false` → **404** |

### Headers

| Header | Required | Description |
|--------|----------|-------------|
| `Authorization` | Yes | Bearer JWT (Admin) |
| `X-Tenant-Id` | Yes | Tenant GUID |

### Query parameters (inputs)

| Name | Type | Default | Description |
|------|------|---------|-------------|
| `page` | int | `1` | Page number |
| `pageSize` | int | `50` | Page size |
| `category` | string | — | Filter by category |
| `severity` | string | — | Filter by severity |
| `userEmail` | string | — | Filter by user email |
| `dateFrom` | datetime | — | From (UTC) |
| `dateTo` | datetime | — | To (UTC) |
| `search` | string | — | Free-text search |

### Example request

```http
GET /V6API/api/event-logs?page=1&pageSize=50&category=Auth&severity=Info
Authorization: Bearer {token}
X-Tenant-Id: {tenant-id}
```

### Success response — `200 OK`

```json
{
  "data": [
    {
      "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
      "eventTitle": "User login succeeded",
      "eventType": "LoginSuccess",
      "userDisplayName": "Jane Doe",
      "userEmail": "jane@example.com",
      "category": "Auth",
      "severity": "Info",
      "ipAddress": "10.0.0.12",
      "createdAtUtc": "2026-07-20T10:15:30Z"
    }
  ],
  "page": 1,
  "pageSize": 50,
  "totalCount": 120,
  "totalPages": 3
}
```

### Response fields

| Field | Type | Description |
|-------|------|-------------|
| `data` | array | Event rows |
| `data[].id` | guid | Event id |
| `data[].eventTitle` | string | Title |
| `data[].eventType` | string | Type |
| `data[].userDisplayName` | string\|null | User display name |
| `data[].userEmail` | string\|null | User email |
| `data[].category` | string | Category |
| `data[].severity` | string | Severity |
| `data[].ipAddress` | string\|null | Client IP |
| `data[].createdAtUtc` | datetime | Created (UTC) |
| `page` | int | Current page |
| `pageSize` | int | Page size |
| `totalCount` | int | Total matching rows |
| `totalPages` | int | Computed total pages |

### Error responses

| Status | When |
|--------|------|
| `400` | Tenant not resolved (`{ "error": "Tenant not resolved." }`) |
| `401` | Missing/invalid token |
| `403` | Not Admin |
| `404` | `EventLog:Enabled` is false |

---

## 2. Jira Support Ticket API

### Overview

Creates a support ticket: optionally creates a **Jira issue**, always inserts a row in the tenant DB (`support.SupportTickets`). On Jira success, sends two emails (JWT caller + `Jira:Email`).

| Item | Value |
|------|--------|
| **Method** | `POST` |
| **URL** | `/api/support-tickets` |
| **Auth** | TenantUser policy (`Admin` or `TenantUser`) |

### Headers

| Header | Required | Description |
|--------|----------|-------------|
| `Authorization` | Yes | Bearer JWT |
| `X-Tenant-Id` | Yes | Tenant GUID |
| `Content-Type` | Yes | `application/json` |

### Request body (inputs)

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `supportCategory` | string | No* | Ticket heading, e.g. `Account configuration`, `Accounts payable setup` |
| `Priorty` | string | No* | UI spelling: `Low`, `Normal`, `High`, `Urgent` |
| `PreferredContact` | string | No* | e.g. `Email`, `Phone` |
| `PhoneNO` | string | No | Phone; may be empty |
| `RequestDescription` | string | No* | Description (stored up to 1000 chars) |
| `isEmailSend` | bool | No | Consent flag (stored in DB) |

\*The API does not return 400 for missing form fields; empty values are stored as null/empty.

**From JWT (not in body):**

- Caller email → stored as `CallerEmail`; used for acknowledgment email on Jira success  
- User id (`sub`) → stored as `UserId`

### Example request

```http
POST /V6API/api/support-tickets
Authorization: Bearer {token}
X-Tenant-Id: {tenant-id}
Content-Type: application/json

{
  "supportCategory": "Account configuration",
  "Priorty": "Normal",
  "PreferredContact": "Email",
  "PhoneNO": "",
  "RequestDescription": "Need help configuring accounts.",
  "isEmailSend": true
}
```

### Success response — `201 Created`

```json
{
  "id": "95e3d375-d328-4444-b534-1fb093b212bf",
  "jiraIssueKey": "VP-12",
  "jiraIssueUrl": "https://ezofis.atlassian.net/browse/VP-12",
  "jiraSuccess": true
}
```

| Field | Type | Description |
|-------|------|-------------|
| `id` | guid | Local ticket id (`support.SupportTickets`) |
| `jiraIssueKey` | string\|null | Jira key if created |
| `jiraIssueUrl` | string\|null | Browse URL if created |
| `jiraSuccess` | bool | `true` only if Jira create succeeded |

### Behavior notes

| Condition | Result |
|-----------|--------|
| `Jira:Enabled` = false | DB insert; `jiraSuccess: false`; no emails |
| Jira create fails (SSL, permissions, etc.) | DB insert with error in `JiraRawResponse`; `jiraSuccess: false`; no emails |
| Jira create succeeds | DB insert; emails to JWT caller + `Jira:Email` (e.g. `support@ezofis.com`) |

### Appsettings (`Jira` section)

| Key | Description |
|-----|-------------|
| `Enabled` | Enable remote Jira create |
| `BaseUrl` | Site root, e.g. `https://ezofis.atlassian.net` |
| `Email` | Atlassian account email; also used as support notification recipient |
| `ApiToken` | API token (local `appsettings.json`; gitignored) |
| `ProjectKey` | Short project key only, e.g. `VP` |
| `IssueType` | e.g. `Task` |

### Error responses

| Status | When |
|--------|------|
| `400` | Tenant not resolved |
| `401` | Missing/invalid token |
| `403` | Not TenantUser/Admin |

---

## Quick reference

| API | Method | Path | Role |
|-----|--------|------|------|
| Event Logs | GET | `/api/event-logs` | Admin |
| Support Tickets | POST | `/api/support-tickets` | TenantUser |
