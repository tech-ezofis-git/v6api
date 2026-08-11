# PostgreSQL Migration — Phase-by-Phase Prompts

Copy one phase's prompt at a time into Claude Code (or Cursor) in the `ezSaaSApi` repo.
Work in order — each phase assumes the previous one's exit criteria are met. Each prompt
is self-contained (a fresh coding-agent session has no memory of the earlier phases), so
it repeats the essentials on purpose.

**Before Phase 0:** decide Decisions 1, 2, and 6 from `docs/PostgreSQL_Migration_Execution_Plan.docx`
(identifier casing, temporal-table replacement, cutover style) — or explicitly accept the
recommended defaults. Say which, in your own words, at the top of the Phase 0 prompt if
you're overriding anything.

All docs referenced below live in `docs/`:
- `SQLSERVER_VS_POSTGRESQL_MIGRATION_PREP.md`
- `POSTGRESQL_MIGRATION_ENGINEERING_PLAN.md`
- `PostgreSQL_Migration_Execution_Plan.docx` (Section 3.1 is a rolling execution log — read it for what's already changed since the original plan)
- `PostgreSQL_Migration_Task_Tracker.xlsx` (Tasks tab — update `Status`/`Owner`/`Notes` as you go, don't reformat)

**Standing rule confirmed in Phase 1 — NuGet packages are additive, not a replacement,
through Phases 1–8.** Npgsql/Npgsql.EntityFrameworkCore.PostgreSQL/Hangfire.PostgreSql
get added alongside the existing Microsoft.Data.SqlClient/EF.SqlServer/Hangfire.SqlServer
packages, never instead of them, until Phase 9. The app can't build otherwise — Phase 4
(raw ADO.NET, ~650 call sites) and Phase 5 (Hangfire storage) haven't ported their usages
off the SQL Server packages yet. Only *usage* switches per phase (UseSqlServer→UseNpgsql,
SqlConnection→NpgsqlConnection, etc.); package removal is Phase 9's job alone.

---

## Phase 0 — Decisions & Cleanup

**Tracker rows:** Task IDs 1–9 · **Est. duration:** ~1 week

```
You're starting Phase 0 of the SQL Server → PostgreSQL migration for ezSaaSApi
(SaaSApp.sln). Read docs/POSTGRESQL_MIGRATION_ENGINEERING_PLAN.md and
docs/PostgreSQL_Migration_Execution_Plan.docx first — don't write any code before you
have both in context.

This phase has no SQL/C# migration work yet — it's decisions and cleanup:

1. Confirm Billing.Infrastructure has no live DbContext usage (grep for `DbContext`
   and `: DbContext`). Report what you find.
2. Confirm Reporting.Infrastructure has no live DbContext usage, same way.
3. Archive or delete v6Api/ at the repo root — it's a stale nested clone, not
   referenced by SaaSApp.sln, last touched weeks ago. Confirm it's unreferenced
   before removing it.
4. Reconcile the 3 scripts that exist only under src/Api/scripts/
   (AddCreditMaster.sql, AddRepositoryFolderDocumentSecurity.sql,
   AddRepositoryFolderDocumentSecurityAllTenants.sql) into the canonical scripts/
   folder so there's one copy going forward.
5. Remove the unreferenced `eztapicontext` connection string key from
   appsettings*.json (confirm via grep that nothing reads it first).
6. Stand up a local/scratch Postgres instance (Docker is fine) that the rest of this
   migration will run against. Don't point anything at a real tenant database.

Decisions 1 (identifier casing → snake_case), 2 (temporal tables → trigger-based
history table), and 6 (cutover → phased per-tenant) are already resolved per
docs/PostgreSQL_Migration_Execution_Plan.docx Section 3 — treat them as settled
unless I've told you otherwise above.

Update the Tasks tab of docs/PostgreSQL_Migration_Task_Tracker.xlsx (IDs 1–9) as you
complete each item. Report back before starting Phase 1.
```

---

## Phase 1 — Connection & Provisioning Layer

**Tracker rows:** Task IDs 10–22 · **Est. duration:** ~2 weeks · **Depends on:** Phase 0

```
Phase 1 of the ezSaaSApi SQL Server → PostgreSQL migration. Phase 0 is done: decisions
are locked in, a scratch Postgres instance exists, dead files/config are cleaned up.
Read docs/POSTGRESQL_MIGRATION_ENGINEERING_PLAN.md §1.2, §2, and §7.1–7.2 before
starting — that's the exact inventory of packages and connection-string locations
this phase touches.

Carried forward from Phase 0's findings (see docs/PostgreSQL_Migration_Execution_Plan.docx
§3.1 and the tracker's Decisions tab): Billing.Infrastructure is NOT dead code —
CreditService.cs uses IDbContextFactory<CatalogDbContext> plus 3 raw SqlConnection/
SqlCommand sites — so it gets the full treatment below like every other module.
Reporting.Infrastructure is confirmed dead; skip it and just drop its EF SqlServer
package reference. The scripts/ folder has already been fully reconciled (src/Api/scripts/
is gone) — don't redo that here.

Add the Postgres packages (Npgsql, Npgsql.EntityFrameworkCore.PostgreSQL,
Hangfire.PostgreSql — pin the EF provider to the 8.0.x line) **alongside** the existing
SQL Server packages — do NOT remove Microsoft.Data.SqlClient / EF.SqlServer /
Hangfire.SqlServer yet. They have to coexist through Phases 1–8: Phase 4 (~650 raw
ADO.NET call sites) and Phase 5 (Hangfire storage) haven't ported their usages off the
SQL Server packages yet, so a hard replace now breaks the build for weeks. Removing the
SQL Server packages is Phase 9's job, once every tenant is migrated. Do this in all
affected projects: src/Api/SaaSApp.Api.csproj, src/BuildingBlocks/Catalog/
SaaSApp.Catalog.csproj, Users.Infrastructure, Workflow.Infrastructure,
Repository.Infrastructure, Dms.Infrastructure, ActivityLog.Infrastructure,
Billing.Infrastructure (do not skip — confirmed live), and src/Workers/HangfireWorker.
Also check each project for packages that turn out to be dead weight once you're in
there (e.g. a Hangfire.SqlServer reference with no actual storage wiring) and drop
those outright rather than swap them. Build the full solution afterward and confirm
0 errors before moving on.

Then:
- Rewrite `DefaultConnection` in src/Api/appsettings.json, appsettings.Development.json,
  and appsettings.example.json to Postgres connection-string format
  (Host=;Port=5432;Database=;Username=;Password=;...).
- Same for src/Workers/HangfireWorker/appsettings.json and appsettings.Development.json
  — keep it in sync with the API's.
- Rework TenantDatabaseCreator.cs: connect to Postgres's `postgres` maintenance database
  instead of `master`, query `pg_database` instead of `sys.databases`, and issue
  `CREATE DATABASE` **outside any open transaction** — Postgres rejects CREATE DATABASE
  inside a transaction block, so check how the connection/transaction is currently
  scoped and change that, not just the SQL text. Also guard against Postgres's 63-byte
  identifier limit (SQL Server's is 128) and use double-quoted identifiers, not brackets.
- Audit TenantSignupService.cs's connection-string builder for SQL-Server-only options
  (Encrypt, TrustServerCertificate, MultipleActiveResultSets) that have no Postgres
  analog — drop them rather than mistranslate them. (This turned out to be 2 call
  sites, not 3 — don't confuse it with the separate 3 ad-hoc UseSqlServer EF wiring
  sites in this same file, which are Phase 2's job.)

Remember: tenant connection strings are also stored as *data* in
Catalog.dbo.Tenants.ConnectionString, not just config — don't touch that table yet,
that's Phase 7. This phase is app config + provisioning code only.

Update Tasks tab IDs 10–22 in the tracker as you go. A full `dotnet run` boot is
**not** a realistic exit criterion for this phase alone — Hangfire's SqlServerStorage
still initializes synchronously at startup and won't stop failing until Phase 5 swaps
its storage, and the EF DbContexts won't point at Postgres until Phase 2. Instead,
verify TenantDatabaseCreator.cs's create/exists-check/re-create logic works end-to-end
against the scratch Postgres instance in isolation (a small standalone harness is fine
for this), and confirm the full solution builds with 0 errors, before calling this
phase done.
```

---

## Phase 2 — EF Core ORM Layer

**Tracker rows:** Task IDs 23–32 · **Est. duration:** ~2–3 weeks (overlaps Phase 1 tail) · **Depends on:** Phase 1

```
Phase 2 of the ezSaaSApi SQL Server → PostgreSQL migration. Phases 0–1 are done: NuGet
packages are swapped, connection strings and TenantDatabaseCreator.cs work against
Postgres. Read docs/POSTGRESQL_MIGRATION_ENGINEERING_PLAN.md §6 and §7.5 before
starting — it lays out exactly why migrations get regenerated, not hand-translated.

1. Swap `UseSqlServer` → `UseNpgsql` in every DI/wiring site:
   CatalogServiceCollectionExtensions.cs, UsersInfrastructureServiceCollectionExtensions.cs,
   UsersDbContextFactory.cs, WorkflowInfrastructureServiceCollectionExtensions.cs,
   and the ad-hoc `new DbContextOptionsBuilder<UsersDbContext>().UseSqlServer(...)`
   call sites in UsersPermissionSchemaEnsuringMiddleware.cs, TenantSignupService.cs
   (3 places), and ShareGuestUserProvisioningService.cs.

2. For CatalogDbContext and UsersDbContext: delete the existing Migrations/ folders'
   generated files (keep the entity/model classes — those are provider-agnostic), then
   run `dotnet ef migrations add InitialPostgres` fresh against the scratch Postgres DB
   for each context, so EF's Npgsql provider infers native Postgres types (uuid, text,
   timestamptz, boolean) instead of carrying over `HasColumnType("nvarchar...")`
   annotations. Do NOT hand-edit the old SQL-Server-targeted migration files — the
   HasColumnType calls don't have a clean 1:1 Postgres translation.

3. Diff the generated Postgres schema against the current SQL Server schema
   column-by-column — check defaults, nullability, and any indexes/unique constraints
   defined via Fluent API weren't silently dropped. UsersDbContext has ~85
   HasColumnType calls to review, so budget real time there.

4. WorkflowDbContext has no migrations folder today — its schema comes from
   CreateWorkflowSchemaComplete.sql, not `dotnet ef database update`. Per the resolved
   decision, stay script-first for it: don't introduce EF migrations for this context
   now, that's handled in Phase 3.

5. Once connection strings and DbContexts are pointed at Postgres, exercise
   TenantConnectionStringResolver.cs and TenantConnectionStringCache.cs to make sure
   nothing assumed the old SQL Server connection-string shape.

Update Tasks tab IDs 23–32. Exit criteria: Catalog and Users schemas provision cleanly
on Postgres via `dotnet ef database update`, and the schema diff is clean (or every
difference is explained).
```

---

## Phase 3 — Static SQL Script Porting

**Tracker rows:** Task IDs 33–47 · **Est. duration:** ~4–5 weeks · **Depends on:** Phase 0 (Decision 2), Phase 1

```
Phase 3 of the ezSaaSApi SQL Server → PostgreSQL migration. This phase ports all 55
SQL scripts under scripts/ (plus the 3 reconciled in Phase 0) so a brand-new tenant
database can be fully provisioned on Postgres from scripts alone — no application
code involved yet. Read docs/POSTGRESQL_MIGRATION_ENGINEERING_PLAN.md §3.2, §4, §5,
and §7.4 before starting; §4 is the full T-SQL-construct-by-construct rewrite table
and §7.4 is the data-type mapping table — use them, don't improvise conversions.

Before touching any script, read docs/PostgreSQL_Migration_Execution_Plan.docx §3.1's
Phase 2 entries — Phase 2 (already done) left two forward-flags that land squarely in
this phase:

- **Menu/RoleMenu seeding:** 02_CreateTenantDatabase.sql currently pre-seeds these two
  tables and fakes an `__EFMigrationsHistory` row for the old SQL-Server-era `AddMenus`
  migration ID so EF skips re-creating them on startup. That trick doesn't work anymore
  — Postgres gets one consolidated `InitialPostgres` migration with no per-table ID to
  fake. Don't port this part of the script as-is; seed Menu/RoleMenu *after*
  `MigrateAsync()` runs instead (e.g. a small idempotent seeding step in the tenant
  provisioning flow), and update 02_CreateTenantDatabase.sql accordingly.

- **Exact table-name/schema matching for 3 EF-excluded Catalog tables:** Phase 2 added
  `.ExcludeFromMigrations()` to `ConnectorProvider`, `CreditMaster`, and
  `RepositoryItemShare` in `CatalogDbContext.cs`, because they were always script-owned,
  never EF-owned. That means **this phase's ported scripts are now the only thing that
  creates those 3 tables**, and they must match the exact name/case/schema already
  hardcoded in `CatalogDbContext.cs`'s `ToTable()` calls — `catalog."ConnectorProviders"`,
  `dbo."creditMaster"` (mixed-case, not snake_case), `catalog."RepositoryItemShares"` —
  or `CatalogDbContext` throws "relation does not exist" the first time it queries one.
  This is a direct, deliberate exception to Decision 1 (snake_case) for these three
  tables. Pick one consciously when you port Create-Connector-Table.sql,
  Create_RepositoryItemShares.sql, and AddCreditMaster.sql: either (a) keep the
  mixed-case/dbo-schema names as-is (fastest, documented exception), or (b) rename to
  snake_case and update those three `ToTable()` calls in `CatalogDbContext.cs` in the
  same change. Don't let this default to snake_case on the SQL side while the C# side
  still expects the old names — that's a silent runtime break, not a compile error.

Port in this dependency order (don't skip ahead — later scripts assume earlier ones
already ran):

1. **Bootstrap:** 01a/b/c_*.sql and 02_CreateTenantDatabase.sql (including the
   Menu/RoleMenu reseeding fix above). This is where the temporal-table replacement
   lives — workflow.WorkflowInstances is system-versioned in SQL Server with a
   WorkflowInstancesHistory table; per the resolved decision, replace it with a
   trigger-based history table (AFTER UPDATE/DELETE trigger copies the prior row into
   the history table). Do NOT hand-port 01c_InstallHangfire.sql — Hangfire.PostgreSql
   installs its own schema.

2. **Schema-complete scripts** (run by the app at tenant-signup/schema-ensure time):
   CreateWorkflowSchemaComplete.sql, CreateDmsSchema.sql, CreateRepositorySchema.sql,
   Create-Connector-Table.sql (exact-match requirement above),
   CreatePlaygroundApiKey*.sql, Create_ApAgentJobProgress.sql,
   Create_RepositoryItemShares.sql (exact-match requirement above),
   CreateActivityLogSchema.sql, CreateLegacyTransactionTable_Manual.sql (this last one
   has the one legacy `INT IDENTITY` column — use `GENERATED ALWAYS AS IDENTITY`).

3. **Incremental scripts** — this category is bigger than the original ~25-file
   Add*/Alter*/Drop* estimate. That estimate came from the engineering plan's naming
   pattern and missed everything that doesn't start with Add/Alter/Drop; a checkpoint
   during this phase cross-checked the full scripts/ folder programmatically and found
   it's actually ~32 files. Port all of them, applying the same treatment (dependency
   order where determinable from filenames/dates, exact-match table names where
   relevant):
   - The ~27 Add*/Alter*/Drop* files, several literally named ...AllTenants.sql,
     including AddCreditMaster.sql (exact-match requirement above).
   - Plus these 7, which don't match that naming pattern but are the same kind of
     incremental schema change: Create_WorkflowAttachments_OnWorkflowCreate.sql,
     CreateCatalogConnectorProviders.sql (check first whether this is superseded by
     01b_CreateCatalogTables.sql's ConnectorProviders block — don't port it blindly if
     it's dead), CreateDmsRepositoryItemsTable.sql, Ensure_LegacyMailbox_Indexes.sql,
     MigrateRolePermissionsToCategories.sql, SetUserConfigurationDefaultOneAllTenants.sql,
     WorkflowAttachments_RepositoryItem_Guid.sql.
   - Two more need a conscious decision, not a blind port — don't touch either without
     resolving this first: **MigrateToPerWorkflowInstances.sql** may be a one-time
     historical utility that's already run against every live SQL Server tenant (dead
     going forward), or may still be needed for Phase 7's tenant migration if any
     tenant is still on the legacy single-table WorkflowInstances design — check which
     before deciding whether/how to port it. **Update-TenantConnectionStrings-SqlServer.sql**
     looks like a Catalog-side data-migration script (rewrites Tenants.ConnectionString
     values), which is Phase 7's concern, not a Phase 3 schema script — confirm that
     read and leave it for Phase 7 to build a Postgres-format equivalent, rather than
     porting it here.
   These are applied via PowerShell loops today (Apply-SchemaUpdates.ps1 etc., which is
   Phase 6's job, not this one) — just port the SQL for now.

4. **Seed/manual scripts:** Insert_MSP_MF_ezfb_items.sql, Seed*.sql, and
   ManualInsert_MSP_MF_MasterForm.sql — the last one also has the one #temp table
   (→ CREATE TEMP TABLE) and SCOPE_IDENTITY() usage (→ RETURNING id on the INSERT).

Apply these dialect rewrites consistently everywhere: TOP(n)→LIMIT n, ISNULL→COALESCE,
CROSS/OUTER APPLY→LATERAL joins, bracket-qualified names→double-quoted per the
snake_case decision (except the 3 documented exceptions above), MERGE→INSERT...ON
CONFLICT DO UPDATE, uniqueidentifier→uuid, nvarchar(n)→varchar(n)/nvarchar(max)→text,
datetime2→timestamptz, bit→boolean, tinyint→smallint.

Update Tasks tab IDs 33–47 **and 122–125** (the gap-fill rows added mid-phase for the
9 scripts the original category list missed — don't let the closing update skip these
just because they're out of numeric sequence). Exit criteria: a fresh tenant database
can be fully
provisioned end-to-end on Postgres from these scripts alone, AND CatalogDbContext can
successfully query ConnectorProviders/creditMaster/RepositoryItemShares against it
(don't skip this check — it's the one this phase is most likely to silently get wrong).
```

---

## Phase 4 — Raw ADO.NET & Dynamic DDL Layer

**Tracker rows:** Task IDs 48–89 (42 files) · **Est. duration:** ~8–9 weeks (largest phase) · **Depends on:** Phase 3

```
Phase 4 of the ezSaaSApi SQL Server → PostgreSQL migration — the largest phase. Phase 3
means a fresh Postgres tenant database now provisions correctly from scripts alone.
This phase ports the 77 raw-ADO.NET files (~655 SqlConnection/SqlCommand sites) that
query and write to it. Read docs/POSTGRESQL_MIGRATION_ENGINEERING_PLAN.md §3.1, §4,
and §7.3-C before starting.

Work in this priority order — don't jump to later files before the dynamic DDL engine
is solid, since everything else depends on the tables it creates:

**1. Dynamic DDL engine (highest risk — do this first):**
- WorkflowTableCreator.cs (1,021 lines, creates 17 tables per published workflow) —
  rewrite off `sys.tables`/`sys.schemas` existence checks onto
  `information_schema`/`pg_catalog`, and off `EXEC(N'CREATE SCHEMA workflow')`-style
  dynamic DDL strings onto Postgres equivalents. Apply the snake_case identifier
  convention consistently here — this is the file where inconsistent casing will do
  the most damage. **Do not add temporal/history tracking to the tables this file
  creates.** Phase 3 confirmed (via MigrateToPerWorkflowInstances.sql's own comments
  and a zero-match grep for temporal/versioning code in this exact file) that the
  single-table `workflow.WorkflowInstances` + trigger-based history design is legacy
  — new tenants use the per-workflow tables this file creates instead, and that
  feature never existed there on SQL Server either. Keep it that way; this is scope
  the migration should not silently add.
- RepositorySqlHelper.cs (dynamic column type mapper: NVARCHAR(MAX)/DECIMAL(18,2)/
  BIT/INT literals baked into C# switch expressions → Postgres types)
- RepositoryItemTableColumns.cs (dynamic per-repository columns, same treatment)

**2. Heaviest query files** (CROSS/OUTER APPLY, ISNULL, TOP concentration):
FormService.cs, FormService.Queries.cs, FormService.Controls.cs,
FormService.UpdateDelete.cs, EmailIngestService.cs (also the heaviest SqlDbType→
NpgsqlDbType enum-mapping pass), WorkflowLegacyMailboxQueryService.cs,
WorkflowLegacyMailboxSyncService.cs (enum mapping too), WorkflowTicketSearchService.cs.

**3. The one MERGE:** ConnectorProviderCatalog.cs → rewrite as
INSERT ... ON CONFLICT (...) DO UPDATE SET ...

**4. Everything else**, module by module: remaining Workflow.Infrastructure files
(ConnectorService.cs, DynamicTableRepository.cs, WorkflowInstanceStore.cs,
ApDashboardQueryService.cs, ApDashboardBuilder.cs, plus the rest of the ~35-file
total); Repository.Infrastructure (RepositoryItemQueryService.cs,
RepositorySecurityService.cs, RepositorySignRequestService.cs,
StaticRepositoryProvisioner.cs, RepositoryItemActivityService.cs,
RepositoryItemMetadataUpdateHelper.cs — SqlDbType.TinyInt→NpgsqlDbType.Smallint —
plus the rest of the ~20-file total); ActivityLog.Infrastructure (6 files);
Dms.Infrastructure (DmsFolderService.cs); Billing.Infrastructure (CreditService.cs —
confirmed live in Phase 0, do not skip); and the src/Api layer (TenantSignupService.cs's raw
SQL portions, PlaygroundApiKeyService.cs, SupportTicketStore.cs,
LegacyWorkflowTransactionService.cs, WorkflowAttachmentArchiveService.cs, the 6
schema-ensuring middleware files, WorkflowsController.cs, RepositoriesController.cs,
DmsController.cs).

For every file: `using Microsoft.Data.SqlClient` → `using Npgsql`; SqlConnection→
NpgsqlConnection; SqlCommand→NpgsqlCommand; SqlParameter→NpgsqlParameter;
SqlDbType.X→NpgsqlDbType.X. That part is mechanical. The SQL strings inside are not —
read each one, don't blind find/replace.

Update Tasks tab IDs 48–89 as you land each file. This phase is big enough to work in
batches — commit and update the tracker after each named file or logical group, don't
try to land all 42 in one pass.
```

---

## Phase 5 — Hangfire Migration

**Tracker rows:** Task IDs 90–92 · **Est. duration:** ~1 week (can run parallel with Phase 4's start) · **Depends on:** Phase 1

```
Phase 5 of the ezSaaSApi SQL Server → PostgreSQL migration — isolated from the rest,
can run in parallel with Phase 4.

In src/Api/Program.cs and src/Workers/HangfireWorker/Program.cs, swap
`.UseSqlServerStorage(...)` → `.UsePostgreSqlStorage(...)`. Hangfire.PostgreSql
installs its own schema — don't hand-port 01c_InstallHangfire.sql.

Validate: job storage, enqueue/dequeue, and recurring jobs all work against the
scratch Postgres instance. Specifically exercise the job types this app actually
uses — email ingest, OCR, archive, AP agent — even with dummy data, so you're not
just testing that Hangfire boots.

Update Tasks tab IDs 90–92.
```

---

## Phase 6 — PowerShell Tooling

**Tracker rows:** Task IDs 93–99 · **Est. duration:** ~1–2 weeks · **Depends on:** Phase 3

```
Phase 6 of the ezSaaSApi SQL Server → PostgreSQL migration. Phase 3 means the SQL
scripts these tools wrap are already ported to Postgres syntax.

Rewrite the sqlcmd invocations to psql in: Apply-SchemaUpdates.ps1,
RunCatalogScripts.ps1, ApplyWorkflowSchemaToTenant.ps1, Verify-Tables.ps1 (also
rewrite any `sys.*` catalog-view checks it does directly), RunTenantSchema.ps1,
ResetCatalog.ps1, and the Test-E2E*.ps1 suite.

Watch for: sqlcmd's `GO` batch separator has no psql equivalent — either split the
script into separate psql invocations at those boundaries, or restructure with
`DO $$ ... $$` blocks where the SQL scripts already support it from Phase 3.

Update Tasks tab IDs 93–99. Exit criteria: these scripts run against Postgres tenants
without needing sqlcmd installed anywhere.
```

---

## Phase 7 — Data Migration Tooling & Rehearsal

**Tracker rows:** Task IDs 100–104 · **Est. duration:** ~3–4 weeks · **Depends on:** Phases 2–6

```
Phase 7 of the ezSaaSApi SQL Server → PostgreSQL migration. Everything up through
Phase 6 is done and verified against the scratch Postgres instance. This phase builds
the actual per-tenant migration mechanism — do not run this against a real production
tenant without an explicit go-ahead.

Build a per-tenant migration script that, for one tenant:
1. Provisions that tenant's schema on Postgres (reusing Phase 3's scripts)
2. Copies that tenant's data from SQL Server to Postgres
3. Rewrites that tenant's row in Catalog.dbo.Tenants.ConnectionString to the new
   Postgres connection string

Sequence these three steps so no request window ever sees a half-migrated tenant —
that's flagged as a real risk in docs/POSTGRESQL_MIGRATION_ENGINEERING_PLAN.md §7.6:
"any bug in the data-migration step that rewrites [connection strings] leaves some
tenants pointed at SQL Server and others at Postgres simultaneously with no
compile-time signal."

Then:
- Rehearse the full script against a **copy** of a real tenant database (never the
  live one).
- Measure downtime for that rehearsal — this number drives the Phase 9 cutover
  schedule.
- Build the post-migration verification step: after migrating a tenant, query its
  Catalog.dbo.Tenants row and confirm the new connection string is actually live and
  reachable, not just written.
- Build and test a rollback path for a single tenant, in case a migration needs to be
  undone.

Update Tasks tab IDs 100–104. Exit criteria: a rehearsed, timed, one-tenant migration
runbook with a working rollback path.
```

---

## Phase 8 — Testing & QA

**Tracker rows:** Task IDs 105–113 · **Est. duration:** ~6–7 weeks, overlapping Phases 4–7 · **Depends on:** rolling, as each phase lands

```
Phase 8 of the ezSaaSApi SQL Server → PostgreSQL migration — this runs alongside
Phases 4 through 7, not strictly after them. Start regression-testing each area as
soon as its underlying phase lands, don't wait for everything to be done.

Run this functional checklist against a Postgres-backed tenant (from
docs/PostgreSQL_Migration_Execution_Plan.docx Section 6 / the prep doc's §6.3):

- Tenant signup / DB provisioning
- Login / JWT / guest-share / sign-request invites
- Repository list / workspace / security / share / sign
- Workflow start / inbox / approve / history / comments
- Credits / billing
- Activity / event logs
- Hangfire jobs: email ingest, OCR, archive, AP agent
- The temporal-table replacement specifically — confirm the trigger-based history
  table actually captures every UPDATE/DELETE on workflow.WorkflowInstances the way
  the old system-versioned table did
- Every dynamically created workflow/repository table shape — spin up a few
  representative workflows and repositories with varied field configurations, not
  just one simple case

Update Tasks tab IDs 105–113. Exit criteria: this checklist passes clean against at
least one full pilot tenant on Postgres.
```

---

## Phase 9 — Cutover & Decommission

**Tracker rows:** Task IDs 114–121 · **Est. duration:** ~4+ weeks, depends on tenant count · **Depends on:** Phase 7, Phase 8 sign-off

```
Phase 9 of the ezSaaSApi SQL Server → PostgreSQL migration — the final phase. Only
start this once Phase 7's migration runbook is rehearsed and Phase 8's regression
checklist passes clean. This phase touches real tenant data — confirm business
sign-off on the cutover plan (downtime window, tenant order) before running anything
here.

1. Select 1–2 low-traffic pilot tenants for the first real cutover.
2. Execute the Phase 7 migration script against those pilot tenants.
3. Run the Phase 7 post-migration verification for each — confirm their
   Catalog.dbo.Tenants.ConnectionString row is live and reachable, and run the
   Phase 8 regression checklist against them specifically.
4. Once pilots are stable for an agreed observation period, widen the rollout to
   the remaining tenants in batches — pace this based on what the pilot cutover
   actually took, not the original estimate.
5. Only after every tenant is confirmed migrated: remove
   Microsoft.Data.SqlClient, Microsoft.EntityFrameworkCore.SqlServer, and
   Hangfire.SqlServer references from the codebase entirely, and decommission the
   SQL Server infrastructure.

Update Tasks tab IDs 114–121 as each batch completes. This is the only phase where
"done" means production state, not just code state — don't mark it complete until
every tenant is verified live on Postgres.
```
