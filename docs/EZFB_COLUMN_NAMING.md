# ezfb column naming: Label vs jsonId

Every published form gets a `dbo.ezfb_{formId}_items` table with one column per field. As of
this change, **new forms** name those columns from the field **Label**; forms created before this
change keep the column names they already have (**jsonId**). Nothing is migrated -- both eras are
read and written through the same shared resolver, forever, with no forced cutover.

## Answer first

- New forms: columns are named from the Label, sanitized -- `PO Number` → `PO_Number`,
  `G/L Account` → `GL_Account`.
- Old forms (their `ezfb_*_items` table already exists): unchanged. Columns stay named from the
  designer's `jsonId` (e.g. `a1b2c3`). No `ALTER TABLE`, no data rewrite, ever.
- `wFormControl.jsonId` is still written for every form, old and new -- the designer still needs a
  stable per-field id regardless of which naming era the table uses.
- Reading and writing form data (`FormEntry` upsert/get/list, inbox/start `formData` JSON,
  move-next ezfb sync, ticket search filters) all resolve through one shared helper,
  `EzfbColumnNaming.TryResolveEzfbColumn(name, jsonId, ezfbColumns, out column)`, that tries the
  sanitized Label first, then the sanitized jsonId, then the legacy `F_`-prefixed jsonId. So a
  request can use either a Label or a jsonId as a field key and it resolves correctly regardless
  of which era the form belongs to.

## Sanitizer rule (Label → column)

Letters and digits are kept as-is. Any run of whitespace becomes a single underscore. Everything
else (`/`, `-`, other punctuation, unicode symbols) is dropped, not converted. A result that would
start with a digit gets an `F_` prefix (same convention as the legacy jsonId path).

| Label | Column |
|---|---|
| `PO Number` | `PO_Number` |
| `G/L Account` | `GL_Account` |
| `2nd Approver` | `F_2nd_Approver` |

Two fields whose labels sanitize to the same column (or a label that happens to collide with a
reserved system column: `item_id`, `created_at`, `modified_at`, `created_by`, `modified_by`,

## Form entry primary key (item_id)

New `dbo.ezfb_{formId}_items` tables use **`item_id uuid PRIMARY KEY DEFAULT gen_random_uuid()`**
(aligned with repository `item_id` and workflow GUIDs). API `formEntryId` is a **GUID** everywhere
(create with `00000000-0000-0000-0000-000000000000` on `POST /api/form/{id}/entry/{entryId}`).

Existing tenants with integer `item_id` must run the one-time admin migration:

`POST /api/admin/tenants/{tenantId}/migrate-ezfb-entry-ids`

before relying on Guid form entry ids on live data.
`is_deleted`, `today_task`, `is_marked`) get a numeric suffix: `_2`, `_3`, ...

## What the frontend can send

For a **new** form, `formData` / entry `fields` / `filterBy.criteria` can use either the field's
Label or its jsonId as the key -- both resolve to the same column. For an **old** form, keep using
jsonId as before; nothing about that contract changed.

**Inbox / start / entry-get JSON** (server → client): for a new-form column, the JSON key is the
field's Label, with the jsonId also included as a cheap alias key on the same object. For an
old-form column, the JSON key is the jsonId, exactly as before -- old frontends do not need to
change anything.

## Where this lives in code

`EzfbColumnNaming.cs` holds both sanitizers (`ToColumnName` for jsonId, `ToColumnNameFromLabel`
for Label) and the shared resolver `TryResolveEzfbColumn`. `FormService.cs`'s
`EnsureFormEntryTableAsync` is the only place that decides the naming era for a form -- it only
runs when the form's `ezfb_*_items` table doesn't exist yet, so an old form's table is never
touched. `FormEntryService.cs`, `WorkflowEzfbFormDataLoader.cs`,
`WorkflowApAgentMoveNextService.cs`, and `WorkflowTicketSearchService.cs` all call the shared
resolver instead of hand-rolling jsonId-only lookups.
