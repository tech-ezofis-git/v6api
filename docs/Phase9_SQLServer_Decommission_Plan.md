# Phase 9 — SQL Server Infrastructure Decommission Plan

Companion to `PostgreSQL_Migration_Task_Tracker.xlsx` (Tasks row 122, Overview row 21) and
`Phase9_Pilot_Cutover_Plan.md`. Read the cutover plan first — this document picks up *after*
every tenant (not just the pilots) has finished cutting over per that plan.

**Status of this document**: planning/runbook only, same honesty standard as the cutover plan.
This sandboxed dev environment has no real SQL Server instance, no real infrastructure
inventory, and no billing/licensing access — nothing here has been executed. It is written so a
human with that access can follow it directly. Do not mark any step below "Done" until it has
actually happened.

---

## 1. Scope

"Decommission" here means fully retiring the SQL Server side of this system: stopping the
service(s), deprovisioning the compute/storage, revoking credentials and network access,
closing out monitoring/alerting and licensing, and cleaning up the residual SQL-Server-specific
references left in this repository and its supporting tooling. It does **not** mean deleting the
final backups (§5) or the historical `.sql` scripts kept for audit trail (§4.6) — those are
explicitly retained, not decommissioned.

---

## 2. Governing constraint — this is the *last* step, not an early one

Decommissioning SQL Server infrastructure has a hard ordering dependency on everything else in
this migration, for two concrete reasons found while building this migration's own tooling:

1. **The migration tooling itself still needs SQL Server reachable.**
   `scripts/Migrate-TenantData.ps1` and `scripts/Verify-TenantMigration.ps1` — the exact tools
   the pilot cutover plan's runbook depends on for every tenant, not just the pilots — both shell
   out to `sqlcmd`/`Invoke-Sqlcmd` against the SQL Server side for every single tenant migration.
   So do `scripts/Apply-RepositorySchema.ps1` and `scripts/SetupEverything.ps1`. If SQL Server is
   decommissioned before the *last* tenant has cut over, the tooling needed to cut over that last
   tenant stops working. **Do not start any step in this document until every tenant's
   `catalog.Tenants."ConnectionString"` points at Postgres and has cleared its observation
   window** (`Phase9_Pilot_Cutover_Plan.md` §7–§9, for every tenant — the pilot plan's sign-off
   criteria apply per-tenant, and this document's gate is "all of them, not just the pilots").

2. **The catalog database's own cutover.** As noted in the cutover plan §2b, the catalog
   database is not per-tenant and needs its own one-time, all-or-nothing cutover. Confirm the
   catalog database itself has been fully on Postgres (not SQL Server) for a meaningful period
   before touching SQL Server infrastructure — the catalog is read on every single request via
   `TenantConnectionMiddleware`, so it's the one database whose SQL Server copy going away
   affects every tenant at once, not just one.

If either of those isn't true yet, stop here and go finish the cutover plan first.

---

## 3. Pre-decommission gate checklist

All of the following must be true before Stage A (§6) begins:

- [ ] Every tenant in `catalog.Tenants` has `"ConnectionString"` pointing at Postgres (query the
      catalog database directly to confirm — don't rely on a spreadsheet tracking this
      separately, since the connection string row is the single source of truth for where a
      tenant is actually served from).
- [ ] Every tenant has cleared its observation window (`Phase9_Pilot_Cutover_Plan.md` §7) with no
      open rollback.
- [ ] The catalog database itself has been running on Postgres for at least as long as the
      longest tenant observation window used during rollout, with no catalog-level incident.
- [ ] **Independent confirmation of zero live traffic to SQL Server** — don't trust the
      connection-string audit alone. Turn on (or review existing) connection/query monitoring on
      the SQL Server instance(s) themselves for a full week and confirm nothing is still
      connecting. This catches the class of problem the app-level cutover can't see by
      construction: a BI tool, reporting export, ETL job, support script, or another internal
      service that was given a direct SQL Server connection string at some point and was never
      routed through the app at all. If anything is still connecting, find out what it is and
      migrate or retire it *before* proceeding — it will break silently and immediately when
      SQL Server goes away, with no application-level error to alert anyone.
- [ ] Rows 119–121 of the Tasks tab (application-side `Microsoft.Data.SqlClient` /
      `Microsoft.EntityFrameworkCore.SqlServer` / `Hangfire.SqlServer` package removal) are
      confirmed deployed to production, not just merged — a build older than that deploy still
      has the SQL Server code paths compiled in, even if nothing calls them anymore.

---

## 4. Infrastructure and reference inventory

Audit and record each item below. This environment can't populate the infrastructure-side items
(no real access), but every repo-level item was actually checked against this codebase and is
listed with what was found.

### 4.1 SQL Server instance(s) and databases

- [ ] List every SQL Server instance/server hosting this system's databases (catalog +
      per-tenant databases). Confirm the list against `catalog.Tenants` (pre-cutover) rather than
      assuming — a stale or orphaned tenant database that was never in active use is easy to miss
      if the inventory comes from memory instead of a query.
- [ ] Confirm no other, unrelated system shares any of these SQL Server instances. Decommissioning
      an instance that also hosts something else's database is a much bigger, different action
      than decommissioning one used only by this system — verify before proceeding.

### 4.2 SQL Server Agent jobs / scheduled tasks

- [ ] Inventory any SQL Server Agent jobs, maintenance plans, or scheduled backup jobs configured
      on the instance(s). These are invisible to a code-level audit of this repo (Hangfire
      recurring jobs are unrelated — those already moved to Postgres storage in Phase 5) and need
      a direct look at the SQL Server instance itself.

### 4.3 Linked servers / cross-database references

- [ ] Check for linked-server definitions or cross-database queries that might reach into these
      databases from *other* SQL Server instances not otherwise in scope.

### 4.4 Credentials, service accounts, and network access

- [ ] Inventory every login/service account with access to these instances (the application's own
      service account, any admin/DBA accounts, any third-party tool's account).
- [ ] Inventory firewall rules, VPN routes, or private-endpoint configurations that exist
      specifically to let something reach these SQL Server instances.
- [ ] Plan credential rotation/revocation as part of Stage E (§6), not before — revoking access
      too early (before the final backup in §5 is confirmed restorable) removes your own ability
      to recover from a bad final backup.

### 4.5 Monitoring, alerting, and licensing

- [ ] Inventory monitoring/alerting configured against these SQL Server instances (uptime checks,
      performance dashboards, on-call alert rules). These need to be explicitly turned off in
      Stage F (§6) — an alert rule left pointed at a decommissioned server either goes silently
      stale or starts paging on-call for a server that's supposed to be gone.
- [ ] Inventory SQL Server licensing (per-core, CAL, or cloud-managed-instance reserved capacity)
      tied to these instances — this is the actual cost-reduction outcome of this migration for
      whoever owns that budget line, and it's easy to let it slip if it isn't tracked explicitly
      as a decommission-plan line item rather than assumed to happen automatically when a VM is
      stopped.

### 4.6 Residual references found in this repository (checked directly, not assumed)

- [ ] **`scripts/*.sql` (55 files, the original SQL Server versions)** are still on disk at the
      repo root of `scripts/` (as opposed to `scripts/postgres/`), left there deliberately
      throughout this migration as the audit trail for what each ported script used to say — see
      the comment convention used throughout Phase 4's csproj changes ("SQL Server originals stay
      on disk untouched"). **Recommendation: archive, don't delete.** Move them to something like
      `scripts/archive/sqlserver-original/` (or a dedicated archive branch/tag) once
      decommission is complete, so `scripts/` itself only contains the live Postgres tooling, but
      keep them retrievable for future audit/compliance questions about what changed.
- [ ] **`scripts/Migrate-TenantData.ps1`, `Verify-TenantMigration.ps1`, `Apply-RepositorySchema.ps1`,
      `SetupEverything.ps1`** still shell out to `sqlcmd`/`Invoke-Sqlcmd` against SQL Server (this
      is *expected and required* until this decommission is complete — see §2). Once
      decommission finishes, these scripts have no remaining purpose (there's no SQL Server left
      to migrate *from*). Retire them the same way as the `.sql` originals: archive rather than
      delete, since they're the executable record of exactly how the migration was actually
      performed.
- [ ] **`_buildcheck/api/appsettings.json`, `_buildcheck/api2/appsettings.json`,
      `_run/api/appsettings.json`** (found during this audit) contain a **stale, live-looking SQL
      Server connection string with a plaintext credential**:
      `Data Source=EZOFIS_DELL_I9;...;User ID=sa;Password=123@abc;...`. This is almost certainly
      this same development machine's own old local SQL Server credential from before the
      Postgres migration began (dated 20 Jul, well before the migration's own dated docs), not a
      production secret — but it should not be left sitting in plaintext in the repo regardless.
      **Action: confirm what `_buildcheck/` and `_run/` actually are (they look like stale
      build-verification scratch copies, not part of the active `src/` tree — nothing in this
      migration's build or test process referenced them), then either delete them or scrub the
      credential, and rotate that SQL Server login's password if the instance behind
      `EZOFIS_DELL_I9` still exists and is still in scope for this decommission.** This is a
      housekeeping item independent of the main decommission timeline — it can and should be
      done now, not gated on §3's checklist.
- [ ] **`docker-compose.postgres.yml`** is the only Docker Compose file in the repo — there is no
      corresponding SQL Server compose file to retire, confirming SQL Server here is external
      infrastructure, not something this repo stands up itself.
- [ ] No CI/CD pipeline files (`azure-pipelines*.yml`, etc.) were found in the repo referencing
      SQL Server — if pipelines exist outside this repo (a separate ops/infra repo, or configured
      directly in a CI platform's UI), audit those separately; this repo-level check can't see
      them.

---

## 5. Final backup and retention

Before any stop/deprovision step (Stage D onward in §6):

- [ ] Take one final full backup of every database in scope (catalog + every tenant database),
      plus a final transaction log backup if the recovery model requires it for point-in-time
      consistency.
- [ ] **Verify the final backup actually restores** — to a scratch instance, not production.
      A backup that was never test-restored is not a verified backup.
- [ ] Archive the verified final backups per whatever data-retention/compliance policy applies
      (this varies by organization and is not something this plan can prescribe) — commonly,
      keep them for a defined period (e.g. 90 days to 1 year) in cold storage, then apply normal
      retention-expiry rules.
- [ ] Record where the final backups are archived and who has access, in whatever runbook/system
      of record this organization uses for infrastructure documentation — this document is not
      that system of record.

---

## 6. Staged decommission runbook

Decommissioning in stages, with a real gap between stages, is what keeps this reversible for as
long as possible. Don't collapse these into a single action.

### Stage A — Revoke application write access (defense-in-depth)

The application should already have stopped writing to SQL Server the moment the last tenant cut
over (§3). Explicitly revoke the application's service-account write permission on every database
in scope anyway, as a belt-and-suspenders check — if anything *is* still writing (which §3's
independent traffic check should have already caught), this makes it fail loudly and immediately
rather than silently succeeding against a database everyone believes is inert.

*Reversible*: yes, trivially — re-grant the permission.

### Stage B — Read-only freeze period

Keep the SQL Server instance(s) running, but read-only, for a defined freeze period (recommend at
least 1–2 weeks, longer for higher-stakes systems). This is the safety margin for "something
unexpected needs a quick read-only lookback at the old data" without needing a backup restore to
get it.

*Reversible*: yes — reads still work, and Stage A's revocation can be undone if a real problem
surfaces that needs write access back (which would indicate the cutover wasn't actually clean and
should trigger going back to re-examine why, not just quietly restoring write access).

### Stage C — Final backup and verified restore

Execute §5 in full. Do not proceed to Stage D until the final backup has been test-restored and
confirmed good.

*Reversible*: yes — nothing has been stopped or removed yet.

### Stage D — Stop services / deallocate compute

Stop the SQL Server service(s). If on managed cloud infrastructure, deallocate (don't yet delete)
the compute resource, so it can be quickly restarted if something in Stage D's aftermath surfaces
a problem that genuinely needs the running instance back, not just its data.

*Reversible*: yes, with some delay (restart time) — but this is the first stage where "reversible"
stops meaning "instant."

### Stage E — Revoke credentials and close network access

Revoke/rotate every credential and service account identified in §4.4. Close the firewall
rules/VPN routes/private endpoints identified in the same section.

*Reversible*: yes, but now requires re-provisioning access, not just flipping a switch — treat
crossing into this stage as the point past which "let's just go back to SQL Server" stops being a
same-day option.

### Stage F — Deprovision storage/compute and close out licensing/monitoring

Delete the compute resource and its attached storage (the final backup from §5 already lives
elsewhere, per its own retention policy — this is deleting the *live* database storage, not the
backup archive). Cancel/reduce SQL Server licensing per §4.5. Turn off monitoring/alerting per
§4.5, don't just leave it to go stale.

*Reversible*: **no** — from this point, "going back to SQL Server" means restoring the archived
final backup onto newly-provisioned infrastructure, with any data written to Postgres after the
final backup's timestamp needing manual reconciliation. This should be treated as a disaster-
recovery action, not a routine rollback, and should only be a live consideration if something
catastrophic and previously-undetected surfaces — which §3's pre-decommission gate exists
specifically to make unlikely.

### Stage G — Documentation and process cleanup

- [ ] Update any on-call runbooks, incident-response playbooks, or architecture documentation
      that reference SQL Server as part of this system's live infrastructure.
- [ ] Archive (per §4.6) the SQL-Server-specific scripts and tooling that no longer have a
      purpose once no SQL Server instance remains to target.
- [ ] Close out this Phase 9 decommission task in the tracker with the actual dates/evidence for
      each stage above — this document is the *plan*; the tracker row should record what
      *actually happened*, when it's real.

---

## 7. Sign-off criteria

- [ ] §3's pre-decommission gate checklist fully checked, with evidence (not just "should be
      fine").
- [ ] §5's final backup verified restorable.
- [ ] Stages A–G (§6) all completed in order, with no stage skipped or collapsed.
- [ ] §4.6's residual repository references archived or cleaned up.
- [ ] Licensing and monitoring closed out (§4.5), not just infrastructure stopped.

---

## 8. What this plan deliberately does not cover

- **Actual execution.** As with the cutover plan, nothing here has been run. This is a runbook
  for someone with the real infrastructure access this sandboxed environment doesn't have.
- **Which specific retention period applies to the final backups (§5)** — that's an
  organizational compliance/legal decision, not an engineering one, and this plan intentionally
  doesn't guess at it.
- **Cost/license accounting mechanics** (§4.5) — how licensing is actually tracked and reduced
  varies by organization and vendor agreement; this plan only ensures it's on the checklist so it
  doesn't get forgotten, not how to execute it.
