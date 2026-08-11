# Phase 9 — Pilot Tenant Cutover Plan (SQL Server → PostgreSQL)

Companion to `PostgreSQL_Migration_Task_Tracker.xlsx` (Tasks rows 115–122, Overview row 21),
`POSTGRESQL_MIGRATION_ENGINEERING_PLAN.md`, and `SQLSERVER_VS_POSTGRESQL_MIGRATION_PREP.md`.

**Status of this document**: planning/runbook only. It was written in a sandboxed dev
environment with no real production/staging tenant infrastructure and no SQL Server instance
to rehearse against — every step below is designed to be *executable* by a human with that
access, but none of it has been run for real. Treat every checklist item as unverified until
someone with real tenant access has walked it once. This is the same honesty standard applied
to Phase 7 (see tracker rows 102/104) and Phase 8 (row 20 Overview note) — do not mark anything
here "Done" until it has actually happened against a real tenant.

---

## 1. Governing decision

Tracker `Decisions` tab, #6, **Approved**:

> Phased, per-tenant — rewrite `catalog.Tenants."ConnectionString"` one tenant at a time,
> starting with 1–2 low-traffic pilot tenants. The database-per-tenant architecture already
> makes each tenant's cutover independent.

This plan implements that decision. Nothing here proposes a big-bang cutover, and nothing here
should be read as overriding that decision without going back to the Decisions tab first.

Why per-tenant cutover is low-risk by construction: the app resolves every tenant's database
connection from a single row — `catalog.Tenants."ConnectionString"` — read once per request by
`TenantConnectionMiddleware`. Flipping that one row is the entire cutover for that tenant, and
flipping it back is the entire rollback. No other tenant is touched, and the SQL Server catalog
row for every tenant *not* yet cut over is completely unaffected.

---

## 2. Pre-cutover prerequisites (do these before touching any real tenant)

These are blockers, not nice-to-haves. Some are infrastructure the sandboxed environment
couldn't provide; some are open findings from Phase 8 live testing that should be resolved (or
explicitly accepted as known risk) before the *first* pilot, since the first pilot is where
they'd surface.

### 2a. Infrastructure (blocked in this environment, needed before pilot #1)

- [ ] A reachable Postgres instance sized for production load (the `ezsaas-postgres-scratch`
      Docker container used for Phase 4/8 testing is a scratch instance, not production-grade —
      do not point real tenant traffic at it).
- [ ] Network path from the API host(s) to that Postgres instance, with the same firewall/VPC
      posture as the SQL Server instance it replaces.
- [ ] Backup/PITR configured on the Postgres instance *before* the first real tenant lands on
      it — Postgres has no first-class equivalent to a SQL Server temporal table's "just query
      history" for anything **other** than the one trigger-based table covered by Decision #2
      (see 2c below); ordinary point-in-time recovery is the safety net for everything else.
- [ ] Connection pooling / max-connections sizing reviewed. Every tenant is a separate Postgres
      *database* on (presumably) one Postgres *server* — confirm the server's
      `max_connections` and the app's Npgsql pool settings tolerate the tenant count you intend
      to reach, not just the 1–2 pilots.
- [ ] `psql` and the PowerShell `SqlServer` module (`Install-Module SqlServer`, for
      `Invoke-Sqlcmd`) installed wherever `Migrate-TenantData.ps1` /
      `Verify-TenantMigration.ps1` will actually run.

### 2b. Application build

- [ ] Deploy a build from **after** the Phase 8/9 fixes landed this session (MSBuild script-copy
      fix, the four Postgres parameter-typing fixes, the repository custom-field type-coercion
      fix, the SQL Server package removal). Confirm via `dotnet build SaaSApp.sln` → 0 errors,
      and via the full `scripts/Test-E2E*.ps1` suite passing against whatever Postgres instance
      that build is pointed at.
- [ ] Confirm `appsettings.{Environment}.json` (or whatever config the target deploy uses) has
      the *catalog* connection string already pointed at the Postgres catalog database
      (`ezofis_catalog_new` or its production-name equivalent). The catalog itself is not
      per-tenant, so it either fully cuts over or it doesn't — there is no phased option for the
      catalog database the way there is for tenant databases. In practice this likely means the
      catalog database is Postgres from day one of Phase 9, and only the *tenant* databases cut
      over gradually.

### 2c. Open findings from Phase 8 — resolve or explicitly accept before pilot #1

- [ ] **`workflow.WorkflowInstances` / `WorkflowInstancesHistory` trigger (Decision #2) is
      unreachable from the live signup path.** Confirmed via live testing (Task tracker row
      113): `TenantSignupService.cs` never applies `scripts/postgres/02_CreateTenantDatabase.sql`
      (only `CreateWorkflowSchemaComplete.sql`/`CreateDmsSchema.sql`/connector/email-ingest
      scripts), so the static `workflow."WorkflowInstances"` table and its history trigger never
      get created for any tenant provisioned via the API — including, presumably, every tenant
      that would be migrated here. Real `WorkflowInstance` persistence goes through
      `WorkflowInstanceStore.cs` to the dynamic `workflow.workflow_instances_{suffix}` tables and
      the static `workflow."WorkflowInstanceLookup"` table instead (both live-verified working).
      **Decide before pilot #1**: is Decision #2's trigger-based history a live requirement that
      needs wiring in, or is it dead code inherited from a pre-existing (possibly also SQL
      Server-era) design that predates the per-suffix-table/Lookup architecture? If it's a live
      requirement, wire the trigger creation into the script `TenantSignupService.cs` actually
      runs before cutting over any tenant that needs it.
- [ ] **`RepositoriesController.IsCurrentUserAdmin()` role-claim check** appears not to match
      the signup-created admin user's actual role claim in at least one code path
      (`items/query` returned 0 rows for a repository with a real, correctly-typed item — see
      tracker row 114). This is C# claims/authorization logic, not something the SQL migration
      touched, but it's a real, reproducible bug that would affect real users post-cutover.
      Investigate the JWT claim the signup/login flow actually issues versus what this check
      expects (`"Admin"`/`"Administrator"` role claim) before it reaches a pilot tenant's users.
- [ ] Confirm Hangfire recurring jobs that touch tenant data (email ingest, OCR, archive, AP
      agent — tracker row 112, only indirectly verified) have been exercised against at least
      one Postgres tenant with real fixtures, not just confirmed "the server starts."
- [ ] Confirm activity/event log content has been spot-checked (row 111 was only indirectly
      verified — the middleware never threw, but no test read the rows back and asserted
      content).

None of the above blocks *writing* this plan or *rehearsing* the copy mechanics in a sandbox —
but all of them should be closed out, or consciously accepted as risk with a named owner, before
the first real tenant's `catalog.Tenants."ConnectionString"` is flipped.

---

## 3. Pilot tenant selection

### 3.1 Selection criteria (apply in this order)

1. **Low traffic** — fewest active users / lowest request volume of any candidate, so a
   cutover mistake affects the smallest number of people and is easiest to notice quickly.
2. **Low business criticality** — internal/test/trial tenants preferred over paying customers
   with SLAs, if any exist. An internal ezofis tenant (if one is used for dogfooding) is an
   ideal pilot #1.
3. **Representative feature usage** — the pilot should actually exercise the paths this
   migration touched: at least one workflow with steps that have run to completion, at least
   one repository with custom fields, ideally at least one email-ingest-enabled mailbox (to
   exercise the Hangfire path). A tenant that has *never* created a workflow is a weak pilot —
   it won't catch anything Phase 8's live E2E testing didn't already catch in the scratch
   environment.
4. **Not on a legacy schema variant** — if `MigrateToPerWorkflowInstances.sql`'s disposition
   (tracker row 125, still an open question) turns out to mean some tenants are on an older
   single-table `WorkflowInstances` design, exclude those from the first pilot wave; they need
   that disposition question resolved first.
5. **Reachable owner/contact** — someone who will actually look at the tenant and confirm "yes,
   this still works" within a few hours of cutover, not days.

### 3.2 Candidate shortlist (fill in with real tenant data — cannot be populated in this
sandboxed environment, which has no real tenant list)

| # | Tenant Id | Organization Name | Why chosen | Owner/Contact | Cutover date |
|---|-----------|-------------------|------------|----------------|--------------|
| 1 | _(fill in)_ | | | | |
| 2 | _(fill in)_ | | | | |

Query to help build this list once real SQL Server access exists (adjust table/column names if
production diverges from this migration's assumed schema):

```sql
-- Run against the SQL Server catalog database. Ranks tenants by a rough activity signal;
-- review manually against criteria 2-5 above before finalizing -- this query only helps
-- with criterion 1 (traffic volume).
SELECT TOP 20
    t.Id, t.OrganizationName, t.ConnectionString,
    (SELECT COUNT(*) FROM Users.dbo.Users u WHERE u.TenantId = t.Id) AS UserCount
FROM Catalog.dbo.Tenants t
ORDER BY UserCount ASC;
-- Then manually cross-check candidates against workflow/repository usage before picking.
```

---

## 4. Cutover runbook (per tenant)

Run every step below for **one tenant at a time**. Do not start tenant N+1 until tenant N has
passed its post-cutover verification (§6) and soaked for the observation window (§7).

### Step 0 — Freeze window (optional but recommended for pilot #1)

For the very first pilot, consider a short (15–30 min) write-freeze on that tenant if the
business allows it — pause any inbound email-ingest polling for that tenant and ask the
tenant's users not to submit new workflow instances during the copy. This isn't required by the
architecture (the copy is non-destructive to the source), but it eliminates "did the app write
something to SQL Server *after* the copy started, that the copy therefore missed" as a variable
while the process is still unproven. Later pilots can skip this once the timing characteristics
are understood (see §5, downtime measurement).

### Step 1 — Provision the Postgres schema for this tenant

The Postgres tenant database must exist with the current schema *before* copying data into it —
same as what happens automatically for a brand-new signup, just done manually here since this
tenant already exists on SQL Server.

```powershell
# Create the empty database, then apply schema (mirrors what TenantSignupService.cs does
# automatically for new signups):
psql -h <pg-host> -p <pg-port> -U postgres -d postgres -c "CREATE DATABASE ""ezofis_tenant_<name>"";"
psql -h <pg-host> -p <pg-port> -U postgres -d "ezofis_tenant_<name>" -f scripts\postgres\CreateWorkflowSchemaComplete.sql
psql -h <pg-host> -p <pg-port> -U postgres -d "ezofis_tenant_<name>" -f scripts\postgres\CreateDmsSchema.sql
psql -h <pg-host> -p <pg-port> -U postgres -d "ezofis_tenant_<name>" -f scripts\postgres\Create-Connector-Table.sql
psql -h <pg-host> -p <pg-port> -U postgres -d "ezofis_tenant_<name>" -f scripts\postgres\Create-EmailIngest-Tables.sql
```

Alternatively, if it's simpler operationally: temporarily point a throwaway signup at this
Postgres server with `-ActivateInCatalog` *not* set, let the app's own `TenantSignupService.cs`
provision an empty schema the same way it would for a new tenant, then discard that throwaway
catalog row and reuse the resulting empty database for the real tenant's data. Either approach
produces the same schema — pick whichever is easier to script safely in your environment.

### Step 2 — Dry-run the data copy (`-WhatIf`)

```powershell
.\scripts\Migrate-TenantData.ps1 `
    -SqlServerConnectionString "Server=<sql-host>;Database=<tenant-db>;User Id=<user>;Password=<pw>;" `
    -PgHost <pg-host> -PgPort <pg-port> -PgDatabase "ezofis_tenant_<name>" `
    -PgUser postgres -PgPassword <pw> `
    -WhatIf
```

Review the printed table list and row counts. Confirm every table you expect to see is listed,
and note the row counts — this is your baseline for the real copy's verification step and for
estimating downtime (§5).

### Step 3 — Run the real data copy (no `-ActivateInCatalog` yet)

```powershell
.\scripts\Migrate-TenantData.ps1 `
    -SqlServerConnectionString "Server=<sql-host>;Database=<tenant-db>;User Id=<user>;Password=<pw>;" `
    -PgHost <pg-host> -PgPort <pg-port> -PgDatabase "ezofis_tenant_<name>" `
    -PgUser postgres -PgPassword <pw>
```

This copies every table and verifies row counts inline (`Migrate-TenantData.ps1`'s own
summary). **Do not proceed to Step 4 if any row-count mismatch is reported** — the script
itself refuses to activate in that case if you pass `-ActivateInCatalog` in the same run, but
running it as two separate steps (as above) means you get a chance to investigate a mismatch by
hand first.

### Step 4 — Independent post-migration verification

Run `Verify-TenantMigration.ps1` as a *second*, independently-derived check (it re-queries both
databases from scratch rather than trusting `Migrate-TenantData.ps1`'s own in-process summary):

```powershell
.\scripts\Verify-TenantMigration.ps1 `
    -SqlServerConnectionString "Server=<sql-host>;Database=<tenant-db>;User Id=<user>;Password=<pw>;" `
    -PgHost <pg-host> -PgPort <pg-port> -PgDatabase "ezofis_tenant_<name>" `
    -DeepCheck -SampleSize 50
```

Confirm:
- All 4 marker tables exist (`workflow.Workflows`, `dms.Repository`, `repository.Repositories`,
  `users.Users`).
- Every table passes row-count verification.
- `-DeepCheck`'s sampled rows look right on manual spot-check (it prints samples for you to
  compare; it does not auto-diff column values — read the output).

If anything fails here, **stop**. Do not proceed to Step 5. The SQL Server tenant database is
still untouched and fully authoritative — nothing about the app's behavior has changed yet.

### Step 5 — Activate (the actual cutover moment)

Only after Steps 3–4 both pass cleanly:

```powershell
.\scripts\Migrate-TenantData.ps1 `
    -SqlServerConnectionString "Server=<sql-host>;Database=<tenant-db>;User Id=<user>;Password=<pw>;" `
    -PgHost <pg-host> -PgPort <pg-port> -PgDatabase "ezofis_tenant_<name>" `
    -PgUser postgres -PgPassword <pw> `
    -TenantId "<tenant-guid>" -ActivateInCatalog
```

This re-runs the copy+verify (idempotent — safe to re-run) and, only if it passes, flips
`catalog.Tenants."ConnectionString"` for this tenant to the Postgres connection string. **This
one `UPDATE` statement is the entire cutover.** From this instant, every new request for this
tenant is served from Postgres.

Record the exact timestamp this ran — it's your reference point for §5 (downtime measurement)
and for "what changed in SQL Server after this moment is now orphaned and must not be trusted."

### Step 6 — Immediate smoke test

Within minutes of Step 5, run the Phase 8 E2E suite's login/read-path portions against this
*specific* tenant (not a fresh signup — use `-SkipSignup -TenantId <this tenant's guid>` where
the scripts support it) to confirm the app is actually serving this tenant from Postgres and
basic auth/read paths work:

```powershell
# Login + a read-only smoke pass. Do NOT run the full Test-E2EWorkflow.ps1 (it signs up a new
# tenant) -- use it as a template for a targeted script against this specific tenant instead.
```

If this fails, immediately proceed to §6 (rollback) rather than debugging in place — figure out
what went wrong against a rolled-back, still-safe tenant, not a live-broken one.

---

## 5. Downtime measurement

Phase 7's downtime-measurement task (tracker row 103) couldn't be executed in this sandboxed
environment (no real tenant, no real data volume to time). For the real pilot, measure and
record:

- **Copy duration** (Step 3 wall-clock time) — scales with the tenant's actual data volume;
  the pilot tenants chosen per §3 are deliberately low-traffic, so this number will
  under-represent larger tenants. Extrapolate cautiously, don't assume linearity (dynamic
  per-workflow tables mean the *number of distinct workflows*, not just total row count, drives
  the number of `\copy` invocations).
- **User-visible downtime**, if any freeze window was used (Step 0) — the gap between when
  writes were paused and when Step 5's `UPDATE` completed and the app started serving Postgres
  successfully.
- **Time-to-detect** if Step 6's smoke test fails — how long between activation and someone
  noticing something's wrong, if it happens.

Record these for pilot #1 and #2 before committing to a batch size/cadence for §8 (wider
rollout) — they're the actual data this sandboxed environment couldn't produce.

---

## 6. Rollback procedure

Because this is a copy, not a move, rollback is always available and always cheap up until you
decide to decommission the SQL Server side (Phase 9's last step, tracker row 122 — not before
then):

```powershell
$oldConnString = "Server=<sql-host>;Database=<tenant-db>;User Id=<user>;Password=<pw>;"
$updateSql = "UPDATE catalog.""Tenants"" SET ""ConnectionString"" = '$oldConnString' WHERE ""Id"" = '<tenant-guid>';"
$updateSql | psql -h <pg-host> -p <pg-port> -U postgres -d ezofis_catalog_new -v ON_ERROR_STOP=1
```

This is the exact reverse of Step 5. The tenant immediately goes back to being served from SQL
Server. **Important**: any writes the tenant made *while on Postgres* (between Step 5 and this
rollback) are on the Postgres side only and are not automatically replayed back to SQL Server —
decide case-by-case whether those writes need manual reconciliation, or whether the freeze
window (Step 0) and a fast Step 6 smoke test make this an acceptably rare/small-window problem
for pilot tenants specifically. This is the real cost of skipping the freeze window on later,
higher-traffic tenants — weigh it before doing so.

Rollback trigger conditions (roll back immediately, don't try to hotfix in place first):
- Step 6 smoke test fails.
- Any user-reported error for this tenant within the observation window (§7) that traces to a
  Postgres-specific cause.
- Any data-integrity concern (a value that looks wrong, not just an error) discovered during the
  observation window.

---

## 7. Observation window

After Step 6 passes, keep this tenant under closer-than-normal watch for a defined window before
calling the pilot cutover "done" and moving to the next tenant:

- **Recommended window**: 24–48 hours for pilot #1 and #2 specifically (shorten for later
  batches once the process is proven — see §8).
- **What to watch**: application error logs/exception rates for this tenant's requests,
  Hangfire job success/failure for this tenant's scheduled jobs (email ingest etc. — the first
  real cycle after cutover), and direct confirmation from the tenant's contact (§3.1 criterion
  5) that things look normal.
- **Exit criteria**: no Postgres-attributable error in the window, and the contact has actively
  confirmed (not just "no news") that the tenant looks fine.

---

## 8. Widening rollout beyond the pilot

Tracker row 118 ("Widen rollout to remaining tenants in batches"). Only start this once both
pilot tenants have cleared §7 cleanly.

- **Batch sizing**: start small (e.g. batches of 3–5 tenants) and grow batch size only after
  several consecutive clean batches — this is a judgment call informed by §5's real timing data
  and §7's real observation results, not a number this plan can prescribe in advance.
- **Batch cadence**: leave enough time between batches to actually review the previous batch's
  observation window before starting the next one. Don't let batches overlap during the pilot
  phase; overlapping is fine once the process has enough of a track record that a single
  tenant's failure is unambiguous to diagnose without confusion from a concurrent batch.
- **Same runbook, every batch**: §4's steps don't change for later batches — only the freeze
  window (§4 Step 0) and observation window (§7) durations should shrink as confidence grows.
- **Stop-the-line rule**: if any tenant in any batch needs a rollback (§6) for a reason that
  isn't already a known, understood, tenant-specific issue, pause the *entire* rollout — not
  just that tenant — until the root cause is understood. A second unexplained rollback in the
  same batch is a hard stop.
- **Legacy-schema tenants**: if `MigrateToPerWorkflowInstances.sql`'s disposition (tracker row
  125) turns out to mean some tenants need that migration applied first, route them into a
  separate, later batch — don't let them block or get silently mixed into the main rollout.

---

## 9. Sign-off criteria (per tenant)

A tenant's cutover is complete, and the SQL Server side of *that tenant* can be considered for
eventual decommission (tracker row 122 — infrastructure decommission is still a separate,
later, all-tenants step), when:

- [ ] §4 Steps 1–5 completed with no unresolved verification failures.
- [ ] §4 Step 6 smoke test passed.
- [ ] §7 observation window completed with no Postgres-attributable issue.
- [ ] Tenant contact has actively confirmed things look normal.
- [ ] `catalog.Tenants."ConnectionString"` for this tenant has stayed pointed at Postgres for
      the full observation window without a rollback.

---

## 10. What this plan deliberately does not cover

- **Actual execution.** Nothing in this document has been run against a real tenant. It is a
  runbook for someone who has the infrastructure this sandboxed environment lacks.
- **The catalog database's own cutover.** As noted in §2b, the catalog database is not
  per-tenant and likely needs its own (separate, one-time, all-or-nothing) cutover before any
  tenant-level work in this plan can begin. That is out of scope for this per-tenant plan and
  should be its own short runbook if it isn't already covered elsewhere.
- **SQL Server infrastructure decommission** (tracker row 122). That is a later, separate step
  — after every tenant (not just the pilots) has cut over and soaked, not before.
