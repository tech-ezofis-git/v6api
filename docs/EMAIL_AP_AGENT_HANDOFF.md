# Email AP Agent + Connector Hub — UI & Service Handoff

**Auth:** `Authorization: Bearer <JWT>` + `X-Tenant-Id: <tenant-guid>`

## Architecture

| Piece | Role |
|-------|------|
| `catalog.ConnectorProviders` | Global OAuth apps (GMAIL, OUTLOOK, QUICKBOOKS, SAP, …) |
| `dbo.connector` | Per-tenant OAuth tokens |
| `dbo.EmailIngestMailbox` | Which mailbox → which AP workflow + master source |
| `dbo.EmailIngestProcessed` | Dedup (message + attachment already started) |
| Hangfire `email-ingest-poll` | Every minute; respects each mailbox `pollIntervalMinutes` |
| Existing AP Agent | Multipart workflow start still enqueues Python AP Agent |

## UI setup

1. Connect Gmail and/or Outlook: `POST /api/connector/oauth/authorize` (`providerCode`: `GMAIL` / `OUTLOOK`).
2. Optionally connect QuickBooks or SAP for PO masters.
3. **Preferred:** create/update an EMAIL workflow with `emailConnectorId` (auto-creates `EmailIngestMailbox`):

```http
POST /api/workflows
{
  "name": "AP Email Invoice",
  "triggerType": 0,
  "publishImmediately": true,
  "emailConnectorId": "<gmail-or-outlook-connector-guid>",
  "emailIsEnabled": true,
  "emailPollIntervalMinutes": 5,
  "emailQueryFilter": "has:attachment subject:invoice",
  "masterSource": "InternalForm",
  "masterFormId": "<vendor-form-id>",
  "workflowJson": {
    "Settings": {
      "General": {
        "Name": "AP Email Invoice",
        "InitiateUsing": { "Type": "EMAIL" }
      },
      "Publish": { "PublishOption": "PUBLISHED" }
    },
    "Blocks": [
      {
        "Id": "start-1",
        "Type": "START",
        "Settings": {
          "InitiateBy": ["EMAIL"],
          "MailInitiate": { "ConnectorId": "<same-guid-optional>" }
        }
      }
    ]
  }
}
```

Response includes `emailIngestMailboxId`, `emailConnectorId`, `emailIngestEnabled`.  
`PUT /api/workflows/{id}` accepts the same email fields (returns 200 with those fields).  
`GET /api/workflows/{id}` also returns the linked mailbox fields.

Legacy int `MailInitiate.ConnectorId` is rejected — use OAuth Guid only.

4. **Alternative:** create mailbox manually:

```http
POST /api/email-ingest/mailboxes
{
  "connectorId": "<gmail-or-outlook-connector-guid>",
  "workflowId": "<ap-workflow-guid>",
  "isEnabled": true,
  "pollIntervalMinutes": 5,
  "queryFilter": "has:attachment subject:invoice",
  "masterSource": "InternalForm",
  "masterFormId": "<vendor-form-id>",
  "attachmentExtensions": ".pdf,.tif,.tiff"
}
```

For QuickBooks masters use `"masterSource": "QuickBooks"` + `"masterConnectorId": "<qbo-connector-guid>"`.  
For SAP PO master use `"masterSource": "SAP"` + `"masterConnectorId": "<sap-connector-guid>"` (see [PHASE4_SAP_PO_MASTER_START_PAYLOAD.md](./PHASE4_SAP_PO_MASTER_START_PAYLOAD.md)).

5. Manual test: `POST /api/email-ingest/mailboxes/{id}/poll`
6. List/status: `GET /api/email-ingest/mailboxes`

**Hangfire:** recurring job `email-ingest-poll` enqueues **one job per active tenant**. In `/hangfire` you will see:
- `Email ingest · schedule all tenants` (orchestrator)
- `Email ingest · {TenantName}` per tenant (args include `tenantId` + `tenantName`; also job parameters `TenantId` / `TenantName`)

Other Hangfire jobs also show tenant name in the dashboard title:
- `AP Agent · {TenantName}`
- `Master file import · {TenantName}`
- `Archive stage · {TenantName}`
- `Welcome email · {TenantName}`

## Mail ops (Gmail + Outlook, same routes)

| Method | Path |
|--------|------|
| GET | `/api/connector/{id}/mail/summary` → `{ totalCount, unreadCount }` |
| GET | `/api/connector/{id}/mail/messages?unreadOnly=&maxResults=&query=` |
| GET | `/api/connector/{id}/mail/messages/top?unreadOnly=true` |
| GET | `/api/connector/{id}/mail/messages/{messageId}` |
| POST | `/api/connector/{id}/mail/messages/{messageId}/read` |
| GET | `/api/connector/{id}/mail/messages/{messageId}/attachments/{attachmentId}` |

**Re-authorize required** after scope bump: Gmail `gmail.modify`, Outlook `Mail.ReadWrite`.

## Master resolve (UI + Python)

```http
GET /api/master/resolve?type=Vendor|Customer|Item&q=&maxResults=50&mailboxId=
GET /api/master/resolve?type=Vendor&source=InternalForm&formId=...
GET /api/master/resolve?type=Vendor&source=QuickBooks&connectorId=...
```

Response items: `{ id, type, displayName, email, source, externalId, raw }`.

Do **not** store Google/Microsoft/Intuit tokens in Python — call V6 with tenant JWT.

## Poller behavior

1. List unread INBOX (optional `queryFilter`).
2. As soon as a message is claimed: **mark as read** + insert `EmailIngestProcessed` message sentinel (`__message_handled__`) so Hangfire never starts it again even if StartWorkflow fails.
3. Prefer real invoice attachments (PDF/TIFF); skip signature/inline badge images. Attachment dedup uses stable `filename+size` (Outlook attachment ids are unstable).
4. For each new attachment → claim attachment row → `StartWorkflow` + `TriggerApAgentPythonJob`.
5. Context JSON includes `messageId`, `from`, `subject`, `masterSource`, etc.
6. If mark-as-read fails (often missing `gmail.modify` / `Mail.ReadWrite`), error is written to `EmailIngestMailbox.LastError` and logged — **dedup still blocks re-start**. Re-authorize the mail connector.
7. Already-handled unread messages: skip start, retry mark-as-read only.

## SQL

- Tenant tables: `scripts/Create-EmailIngest-Tables.sql` (also auto-created on first API use).
- Catalog scopes: re-run seed / `CreateCatalogConnectorProviders.sql` MERGE updates GMAIL/OUTLOOK scopes.
