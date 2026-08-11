# SQL Server → PostgreSQL Migration — Engineering Analysis & Plan

**Scope:** `SaaSApp.sln` (the `src/` tree). **No code has been changed** — this is analysis + planning only.
**Companion doc:** [`SQLSERVER_VS_POSTGRESQL_MIGRATION_PREP.md`](SQLSERVER_VS_POSTGRESQL_MIGRATION_PREP.md) is the management-level feasibility memo and explicitly asks for "a full `SqlConnection` / script inventory" as the next step — this document *is* that inventory, plus the file-level execution plan.
**Out of scope / excluded from analysis:** the `v6Api/` folder at the repo root is a **stale nested clone** (last commit 2026‑06‑15, not referenced by `SaaSApp.sln`, not built). It duplicates most of `scripts/` and `src/Api`. Recommend deleting or archiving it before the migration starts so it doesn't get mistaken for a second source of truth.

---

## 1. Current data-access architecture

### 1.1 ORM / data-access approach — it's a hybrid, and raw ADO.NET is the dominant one

| Approach | Where | Scale |
|---|---|---|
| **EF Core 8.0.11** (`Microsoft.EntityFrameworkCore.SqlServer`) | 3 real `DbContext` classes: `CatalogDbContext`, `UsersDbContext`, `WorkflowDbContext` | Catalog + Users have EF **migrations**; Workflow's `DbContext` exists but its schema is created by a raw `.sql` script, not EF migrations (see §6) |
| **Raw ADO.NET** (`Microsoft.Data.SqlClient` — `SqlConnection`/`SqlCommand`, hand-built SQL strings) | **77 C# files**, ~655 `SqlConnection`/`SqlCommand` construction sites, concentrated in `Workflow.Infrastructure` (heaviest), `Repository.Infrastructure`, `ActivityLog.Infrastructure`, `Dms.Infrastructure`, and several `Api/Services`, `Api/Middleware`, `Api/Controllers` | This is the majority of the data layer by file count and by query complexity |
| **Dapper** | Not used anywhere | — |
| **Hangfire.SqlServer** | `src/Api` (in-process server) + `src/Workers/HangfireWorker` (standalone worker) | Background job storage, its own internal schema |
| **Runtime dynamic DDL** (not EF, not static scripts — DDL strings built and executed at request time) | `WorkflowTableCreator.cs` (1,021 lines — creates 17 tables per published workflow: `Comments_X`, `Attachments_X`, `Forms_X`, …), `RepositorySqlHelper.cs` + `RepositoryItemTableColumns.cs` (dynamic per-repository item columns) | **Highest-risk area** — T-SQL syntax is baked into C# string templates, not in one place |

**Key architectural fact that shapes the whole plan:** this is a **database-per-tenant** SaaS. There is one **Catalog** database (tenant registry, `dbo.Tenants`, `dbo.UserTenants`, Hangfire) and one **tenant database per customer** (`ezofis_Tenant_N`), each containing `users`, `workflow`, `repository`, `dms`, `activitylog` schemas plus the dynamically-created per-workflow/per-repository tables. Every tenant's **connection string is stored as a literal string** in `Catalog.dbo.Tenants.ConnectionString` (resolved at runtime by `TenantConnectionStringResolver` / `TenantConnectionStringCache`) — so migrating isn't just an `appsettings.json` edit, it's a **data migration** across every tenant row plus every physical tenant database.

### 1.2 NuGet packages referencing SQL Server (by project)

| Project | Packages |
|---|---|
| `src/Api/SaaSApp.Api.csproj` | `Microsoft.Data.SqlClient` 5.2.2, `Microsoft.EntityFrameworkCore` 8.0.11, `Microsoft.EntityFrameworkCore.Design` 8.0.11, `Hangfire.AspNetCore` / `Hangfire.SqlServer` 1.8.14 |
| `src/BuildingBlocks/Catalog/SaaSApp.Catalog.csproj` | `Microsoft.Data.SqlClient`, `Microsoft.EntityFrameworkCore`, `Microsoft.EntityFrameworkCore.SqlServer`, `Microsoft.EntityFrameworkCore.Tools` |
| `src/Modules/Users/Users.Infrastructure` | Same EF trio + `Microsoft.Data.SqlClient` (indirectly) + `Hangfire.Core`/`AspNetCore`/`SqlServer` |
| `src/Modules/Workflow/Workflow.Infrastructure` | `Microsoft.Data.SqlClient`, EF Core + `.SqlServer` + `.Tools`, `Hangfire.Core` |
| `src/Modules/Repository/Repository.Infrastructure` | `Microsoft.Data.SqlClient`, `Hangfire.Core` (no EF — pure ADO.NET) |
| `src/Modules/Dms/Dms.Infrastructure` | `Microsoft.Data.SqlClient` only (no EF) |
| `src/Modules/ActivityLog/ActivityLog.Infrastructure` | `Microsoft.Data.SqlClient` only (no EF) |
| `src/Modules/Billing/Billing.Infrastructure` | EF Core + `.SqlServer` **and** `Microsoft.Data.SqlClient` (mixed — `CreditService.cs` uses raw ADO.NET; no `DbContext` was found actually in use in this module despite the EF package reference — verify at implementation time whether it's dead weight) |
| `src/Modules/Reporting/Reporting.Infrastructure` | EF Core + `.SqlServer` referenced, but **no `DbContext` class exists in this module** — package reference appears unused; confirm and drop rather than migrate |
| `src/Workers/HangfireWorker` | `Hangfire.AspNetCore`, `Hangfire.SqlServer` |

---

## 2. Connection strings / DB config — every location

| Location | What's there |
|---|---|
| `src/Api/appsettings.json`, `appsettings.Development.json`, `appsettings.example.json` | `ConnectionStrings:DefaultConnection` (Catalog DB, SQL Server format: `Data Source=...;Database=...;User ID=sa;Password=...;Encrypt=True;TrustServerCertificate=True;...`) and `ConnectionStrings:eztapicontext` — **identical value, appears unreferenced in code** (dead config, confirm and remove), plus `ConnectionStrings:Redis` (not a DB migration concern) |
| `src/Api/appsettings.ActivityLog.json`, `appsettings.EventLog.json` | Feature flags only — no connection strings, no change needed |
| `src/Workers/HangfireWorker/appsettings.json` + `appsettings.Development.json` | Own copy of `DefaultConnection` for Hangfire storage — must be kept in sync with the API's |
| `src/Api/Properties/launchSettings.json` | Ports + `ASPNETCORE_ENVIRONMENT` only — no DB config, nothing to change |
| **`Catalog.dbo.Tenants.ConnectionString` column (data, not config!)** | One SQL-Server-format connection string **per tenant**, written at signup time by `TenantSignupService`. This is the connection string actually used for almost every tenant-scoped query. **Must be rewritten for every existing tenant row as part of data migration**, not just app config. |
| `TenantDatabaseCreator.cs` (`src/BuildingBlocks/Catalog`) | Builds a `master`-database connection via `SqlConnectionStringBuilder { InitialCatalog = "master" }` to run `CREATE DATABASE [name]` and query `sys.databases` — SQL-Server-specific admin pattern with no direct Postgres equivalent (see §5) |
| DI / registration sites | `Program.cs` (`src/Api`) — Hangfire `.UseSqlServerStorage(...)`; `CatalogServiceCollectionExtensions.cs` — `AddDbContextFactory<CatalogDbContext>().UseSqlServer(...)`; `UsersInfrastructureServiceCollectionExtensions.cs` — `UseSqlServer(...)`; `WorkflowInfrastructureServiceCollectionExtensions.cs` — `UseSqlServer(...)`; ad-hoc `new DbContextOptionsBuilder<UsersDbContext>().UseSqlServer(...)` also appears in `TenantSignupService.cs` (×3), `UsersDbContextFactory.cs`, `ShareGuestUserProvisioningService.cs`, `Middleware/UsersPermissionSchemaEnsuringMiddleware.cs` |

---

## 3. Raw SQL inventory

### 3.1 Inline SQL in C# (raw ADO.NET)

77 files build `SqlConnection`/`SqlCommand` directly. By module:

| Module | File count | Notable files |
|---|---|---|
| `Workflow.Infrastructure` | ~35 | `WorkflowTableCreator.cs` (dynamic DDL, 1,021 lines), `FormService.cs`/`FormService.Queries.cs`/`FormService.Controls.cs`/`FormService.UpdateDelete.cs`, `EmailIngestService.cs`, `WorkflowLegacyMailboxQueryService.cs`/`SyncService.cs`, `WorkflowTicketSearchService.cs`, `ApDashboardQueryService.cs`/`ApDashboardBuilder.cs`, `ConnectorService.cs`, `DynamicTableRepository.cs`, `WorkflowInstanceStore.cs` |
| `Repository.Infrastructure` | ~20 | `RepositorySqlHelper.cs` (dynamic column type mapper), `RepositoryItemTableColumns.cs`, `RepositoryItemQueryService.cs`, `RepositorySecurityService.cs`, `RepositorySignRequestService.cs`, `StaticRepositoryProvisioner.cs`, `RepositoryItemActivityService.cs` |
| `ActivityLog.Infrastructure` | 5 | `EventLogQueryService.cs`, `EventLogWriter.cs`, `EventLogActorLookup.cs`, `ActivityLogWriter.cs`, `ActivityLogSchemaService.cs`, `ActivityLogQueryService.cs` |
| `Dms.Infrastructure` | 1 | `DmsFolderService.cs` |
| `Billing.Infrastructure` | 1 | `CreditService.cs` |
| `BuildingBlocks/Catalog` | 2 | `TenantDatabaseCreator.cs`, `ConnectorProviderCatalog.cs` (contains the one real `MERGE` statement) |
| `src/Api` (Services/Middleware/Controllers) | ~13 | `TenantSignupService.cs`, `PlaygroundApiKeyService.cs`, `SupportTicketStore.cs`, `LegacyWorkflowTransactionService.cs`, `WorkflowAttachmentArchiveService.cs`, schema-ensuring middleware (`WorkflowSchemaEnsuringMiddleware.cs`, `UsersPermissionSchemaEnsuringMiddleware.cs`, `RepositorySchemaEnsuringMiddleware.cs`, `DmsSchemaEnsuringMiddleware.cs`, `ActivityLogSchemaEnsuringMiddleware.cs`, `TenantSchemaEnsureHelper.cs`), `WorkflowsController.cs`, `RepositoriesController.cs`, `DmsController.cs` |

### 3.2 `.sql` script files

**55 unique scripts** under the canonical `scripts/` folder (52) plus 3 more that currently exist only under `src/Api/scripts/` (`AddCreditMaster.sql`, `AddRepositoryFolderDocumentSecurity.sql`, `AddRepositoryFolderDocumentSecurityAllTenants.sql` — reconcile these into `scripts/` before migrating). `src/Api/SaaSApp.Api.csproj` copies/embeds several of them (`CreateWorkflowSchemaComplete.sql`, `CreateDmsSchema.sql`, `Create-Connector-Table.sql`, `Create-EmailIngest-Tables.sql`, the Playground API key scripts) into the build output and reads them at runtime via `Path.Combine(AppContext.BaseDirectory, "scripts", ...)` — these are **live, executed-by-the-app** scripts, not just DBA reference material.

They fall into four categories, all of which need a Postgres rewrite:
- **Bootstrap** (`01a/b/c_*`, `02_CreateTenantDatabase.sql`) — create the Catalog DB and a fresh tenant DB from scratch, including Hangfire's SQL Server schema installer and **temporal tables** (see §5).
- **Schema-complete scripts** run by the app at tenant-signup/schema-ensure time (`CreateWorkflowSchemaComplete.sql`, `CreateDmsSchema.sql`, `CreateRepositorySchema.sql`, `Create-Connector-Table.sql`, `CreatePlaygroundApiKey*.sql`, `Create_ApAgentJobProgress.sql`, `Create_RepositoryItemShares.sql`, `CreateActivityLogSchema.sql`, `CreateLegacyTransactionTable_Manual.sql`).
- **Incremental `Add*`/`Alter*`/`Drop*` scripts** — ~25 files, applied "to all tenants" (several are literally named `...AllTenants.sql`) via PowerShell loops (`Apply-SchemaUpdates.ps1`, `RunCatalogScripts.ps1`, `ApplyWorkflowSchemaToTenant.ps1`). This is effectively a **hand-rolled migration system running in parallel with EF Core migrations** — a second migration mechanism to account for.
- **Seed / manual / perf-test data scripts** (`Insert_MSP_MF_ezfb_items.sql`, `ManualInsert_MSP_MF_MasterForm.sql`, `Seed*.sql`) — lower priority, but `ManualInsert_MSP_MF_MasterForm.sql` also creates a **temporal table** and is the one place `SCOPE_IDENTITY()` and `#temp` tables are used.

### 3.3 Stored procedures / functions

**None** in application code. The only `CREATE PROCEDURE`/`CREATE FUNCTION`/`CREATE TRIGGER` bodies in the repo belong to `Hangfire.SqlServer`'s own installer script (`01c_InstallHangfire.sql`) — this is replaced wholesale by swapping to `Hangfire.PostgreSql`, not ported by hand.

---

## 4. T-SQL-specific syntax inventory

| Construct | Found? | Where | Postgres migration note |
|---|---|---|---|
| `TOP (n)` | **Yes** — 26 files, mostly Workflow/Repository query services and dashboard builders | Rewrite to `LIMIT n` (careful with `TOP` inside subqueries/CTEs — ordering must be made explicit, Postgres has no positional-`TOP`-without-`ORDER BY` ambiguity but SQL Server code often omits it) |
| `IDENTITY` columns | Not found in EF models (all PKs are `uniqueidentifier`/GUID); check the dynamic-DDL tables in `WorkflowTableCreator.cs` and legacy scripts individually — some legacy tables (`CreateLegacyTransactionTable_Manual.sql`, `ManualInsert_MSP_MF_MasterForm.sql`) use `INT IDENTITY` | Rewrite to `GENERATED ALWAYS AS IDENTITY` or `serial`/`bigserial` |
| `GETDATE()` / `GETUTCDATE()` | Rare — only 2 test/seed scripts (`Seed-LegacyMailbox-LoadTest-F001E07B.sql`, `Seed_LegacyMailbox_PerformanceTest.sql`). Most code uses `SYSUTCDATETIME()` or sets timestamps from C# `DateTime.UtcNow` | `GETDATE()`/`GETUTCDATE()`/`SYSUTCDATETIME()` → `now()` / `timezone('utc', now())`; prefer pushing more of this into C# `DateTime.UtcNow` where feasible to reduce dialect surface |
| `ISNULL(...)` | **Yes** — 26 files, concentrated in Workflow legacy-mailbox and Repository query/status code | → `COALESCE(...)` (ANSI-standard, near drop-in, but check `ISNULL` two-arg-only vs `COALESCE` n-ary semantics and differing type-inference rules for the "then" branch) |
| `NVARCHAR` / `DATETIME2` / `UNIQUEIDENTIFIER` / `BIT` types | **Yes, extensively.** Confirmed exact set from EF migration snapshots: `uniqueidentifier`, `nvarchar(8/16/32/64/128/256/512/max)`, `datetime2`, `bit`, `int`. Same vocabulary reused in dynamic-DDL C# (`WorkflowTableCreator.cs`, `RepositorySqlHelper.MapDataTypeToSql`: `NVARCHAR(MAX)`, `DECIMAL(18,2)`, `BIT`, `INT`, `DATE`) | See type-mapping table in §7.4 |
| Square-bracket identifiers (`[dbo].[Table]`, `[workflow].[WorkflowInstances]`) | **Yes** — 30 files, split between C# inline SQL (`ConnectorProviderCatalog.cs`, `CreditService.cs`, `WorkflowStartBootstrapService.cs`, `FormService.cs`, `UsersSchemaEnsurer.cs`, `WorkflowSecurityService.cs`, EF migrations) and most `.sql` scripts | Postgres uses double quotes for case-sensitive/reserved identifiers (`"workflow"."WorkflowInstances"`) — see the case-sensitivity risk in §7.6, this is not a mechanical find/replace |
| `OUTPUT INSERTED.*` / `OUTPUT DELETED.*` | **None found** | No work needed here |
| `MERGE` | **One real usage**: `ConnectorProviderCatalog.cs:48` (`MERGE [catalog].[ConnectorProviders] AS t ...`). (The 9 other hits are the word "merge" in comments/method names like `RepositoryItemMetadataMerger`, or Hangfire's own installer script, which is being replaced wholesale, not ported.) | Rewrite as `INSERT ... ON CONFLICT (...) DO UPDATE SET ...` |
| `CROSS APPLY` / `OUTER APPLY` | **Yes** — 40 occurrences / 10 files, all Workflow-related (`FormService.cs`, `FormService.Queries.cs`, `WorkflowLegacyMailboxQueryService.cs`, `WorkflowLegacyMailboxSyncService.cs`, `WorkflowTicketSearchService.cs`, `WorkflowsController.cs`, plus 3 `.sql` scripts) | Rewrite as `LATERAL JOIN` (Postgres equivalent; `CROSS APPLY` → `, LATERAL (...) AS x`, `OUTER APPLY` → `LEFT JOIN LATERAL (...) AS x ON true`) |
| `TRY_CONVERT` / `TRY_CAST` | **Yes, but small** — 5 occurrences in 3 `.sql` scripts only (none in C# inline SQL) | Postgres has no `TRY_CONVERT`; needs `CASE WHEN ... ~ '^regex$' THEN CAST ... ELSE NULL END` or a small `safe_cast` helper function |
| String concatenation with `+` | **Yes**, pervasive wherever SQL builds strings from parts (dynamic table/column names, dashboard filter builders) — this is mixed with C# string interpolation (`$"..."`), so a pure grep undercounts it; expect it in most of the "dynamic SQL" files listed in §3.1 | Postgres uses `||` for SQL-level concatenation; C#-side interpolation building identifiers is unaffected by the DB engine but is the actual injection-risk area to re-audit (see §7.6) |
| `#temp` tables | **One occurrence**: `ManualInsert_MSP_MF_MasterForm.sql` | Rewrite as `CREATE TEMP TABLE` (Postgres supports this natively, syntax differs slightly — no `#` prefix, session-scoped by default) |
| `SCOPE_IDENTITY()` | **One occurrence**, same file as above | With GUID PKs elsewhere this is a non-issue almost everywhere; for this one legacy `IDENTITY` table, replace with `RETURNING id` on the `INSERT` |
| `OFFSET n ROWS FETCH NEXT n ROWS ONLY` | **Yes** — 24 files use this ANSI-standard paging form (good news) | Postgres supports the same `OFFSET ... FETCH NEXT ... ROWS ONLY` syntax directly — **low risk**, likely no change needed |
| `COLLATE` | **None found explicitly** | Still a risk — see case-sensitivity in §7.6; the *absence* of explicit collation just means everything is quietly relying on SQL Server's default case-insensitive collation |
| Temporal tables (`PERIOD FOR SYSTEM_TIME`, `SYSTEM_VERSIONING`) | **Yes** — `scripts/02_CreateTenantDatabase.sql` (`workflow.WorkflowInstances` / `WorkflowInstancesHistory`) and `ManualInsert_MSP_MF_MasterForm.sql` | No native Postgres equivalent — see §5 |
| `SqlDbType` enum usage (ADO.NET parameter typing) | **Yes** — `EmailIngestService.cs` (heaviest — `UniqueIdentifier`, `NVarChar`), `RepositoryItemMetadataUpdateHelper.cs` (`Decimal`, `Date`, `TinyInt`), `WorkflowLegacyMailboxSyncService.cs` | Every `SqlParameter`/`SqlDbType` call site becomes `NpgsqlParameter`/`NpgsqlDbType` — mechanical but must be done file-by-file since the enum values don't share a namespace |

---

## 5. DB-specific features (beyond syntax)

| Feature | Used? | Detail | Postgres path |
|---|---|---|---|
| Temporal tables (system-versioned) | **Yes** | `workflow.WorkflowInstances` is system-versioned with a `WorkflowInstancesHistory` table (SQL Server 2016+ feature, requires `PERIOD FOR SYSTEM_TIME` + `SYSTEM_VERSIONING = ON`). One more instance in `ManualInsert_MSP_MF_MasterForm.sql`. Docs (`00_MASTER_SETUP_README.md`) explicitly call out `FOR SYSTEM_TIME ALL` queries. | **No native Postgres equivalent.** Options: (a) hand-roll with a trigger that copies the old row into a history table on `UPDATE`/`DELETE` (closest behavioral match), or (b) the `temporal_tables` Postgres extension, or (c) move this to an application-level audit log (there's already an ActivityLog module — evaluate whether it can absorb this). This needs its own design decision before implementation starts. |
| Full-text search | **Not used** | No `FULLTEXT INDEX`, `CONTAINS()`, or `FREETEXT()` found anywhere | N/A — one less thing to worry about |
| XML columns / `FOR XML` / `.value()`/`.nodes()` | **Not used** | No matches found | N/A |
| Computed columns | **Not used** | No `HasComputedColumnSql` or `AS ... PERSISTED` found | N/A |
| Triggers | **Not used** (application-level) | Only Hangfire's internal installer defines any trigger-like objects, and that's replaced by the Postgres storage package | N/A, *unless* temporal-table history is reimplemented via triggers (see above) |
| Stored procedures / functions | **Not used** (application-level) | Same caveat as triggers | N/A |
| Dynamic runtime DDL (`CREATE TABLE`/`ALTER TABLE` generated and executed per-tenant/per-workflow at request time) | **Yes, extensively** | `WorkflowTableCreator.cs` (17 tables per published workflow, in a `workflow` schema, using `sys.tables`/`sys.schemas` existence checks and `EXEC(N'CREATE SCHEMA workflow')`), `RepositorySqlHelper.cs`/`RepositoryItemTableColumns.cs` (dynamic columns per repository based on user-defined fields) | This is the largest single risk item in the whole migration — it's a bespoke schema-generation engine hardcoded to T-SQL (`sys.*` catalog views, bracket-qualified names, `NVARCHAR(MAX)`/`DECIMAL(18,2)`/`BIT` literals baked into C# switch expressions). Needs a full rewrite against `information_schema` / `pg_catalog`, not a syntax patch. |
| `master` database / `sys.databases` administrative access | **Yes** | `TenantDatabaseCreator.cs` connects to `master`, checks `sys.databases`, runs `CREATE DATABASE [name]` | Postgres has no `master` DB; use `postgres` (or a maintenance) database, query `pg_database`, and note Postgres **cannot** run `CREATE DATABASE` inside a transaction block — connection/transaction handling here needs to change, not just the SQL text |
| Hangfire SQL Server schema | **Yes** | `HangFire.*` tables (`Job`, `State`, `JobQueue`, etc.), installed by `01c_InstallHangfire.sql`, running in both `src/Api` (in-process) and `src/Workers/HangfireWorker` | Swap package to `Hangfire.PostgreSql`; it ships its own schema installer — do not hand-port `01c_InstallHangfire.sql` |

---

## 6. EF Core migrations — history and provider swappability

| DbContext | Migrations folder? | History | Provider swap complexity |
|---|---|---|---|
| `CatalogDbContext` | **Yes** — `src/BuildingBlocks/Catalog/Migrations/` | 6 migrations, `20250226100000_InitialCatalog` → `20250720160000_AddConnectorProviders` | Standard EF provider swap: remove `Microsoft.EntityFrameworkCore.SqlServer`, add `Npgsql.EntityFrameworkCore.PostgreSQL`, change `UseSqlServer`→`UseNpgsql`, then **regenerate** migrations from the model (don't try to hand-edit the SQL Server migration `Up()`/`Down()` bodies — they contain `HasColumnType("nvarchar(...)")` etc. that must be dropped so EF infers Postgres-native types, or replaced with explicit Postgres types) |
| `UsersDbContext` | **Yes** — `src/Modules/Users/Users.Infrastructure/Migrations/` | 15 migrations, `20250226000000_InitialUsers` → `20250710130000_AddDashboardPermissionCategory` | Same as above; this context has the most `HasColumnType` calls (~85) to review |
| `WorkflowDbContext` | **No migrations folder exists**, despite `WorkflowDbContext : DbContext` and `Microsoft.EntityFrameworkCore.SqlServer` being referenced | Schema for `workflow.*` tables is created by the raw script `CreateWorkflowSchemaComplete.sql` / `02_CreateTenantDatabase.sql`, **not** by `dotnet ef database update`. This is effectively a database-first / script-first context wearing an EF Core skin. | Confirm at implementation time whether `WorkflowDbContext` is used for querying only (LINQ over an externally-managed schema) or also for any `Add-Migration` workflow that just hasn't been run. Either way, the source of truth for this schema is the `.sql` scripts, so the migration strategy is "port the script," not "regenerate EF migrations." |
| `Billing`, `Reporting` | **No `DbContext` found** despite EF Core package references | — | Confirm these packages are unused and drop them rather than migrate them; don't spend effort here until confirmed |

**Is the provider swappable in principle?** Yes — none of the 3 contexts use raw SQL Server-only EF features (no `[SqlServer]`-specific annotations beyond `HasColumnType` strings, no `HiLo` sequences tied to SQL Server, no computed columns). The mechanical EF part of this migration is the *easy* part; the raw-ADO.NET 77 files and the dynamic-DDL engine are where the real effort is, consistent with the existing prep doc's difficulty rating.

---

## 7. Migration plan

### 7.1 NuGet / package swaps

| Remove | Add | Projects affected |
|---|---|---|
| `Microsoft.Data.SqlClient` | `Npgsql` | All 8 projects listed in §1.2 |
| `Microsoft.EntityFrameworkCore.SqlServer` | `Npgsql.EntityFrameworkCore.PostgreSQL` | `SaaSApp.Catalog`, `Users.Infrastructure`, `Workflow.Infrastructure`, (`Billing.Infrastructure`/`Reporting.Infrastructure` — confirm unused first) |
| `Hangfire.SqlServer` | `Hangfire.PostgreSql` | `src/Api`, `src/Workers/HangfireWorker`, `Users.Infrastructure` (has the package but confirm it's actually wired) |
| — | (keep) `Microsoft.EntityFrameworkCore`, `Microsoft.EntityFrameworkCore.Design`, `Microsoft.EntityFrameworkCore.Tools` | Provider-agnostic, no change |

Pin `Npgsql.EntityFrameworkCore.PostgreSQL` to the 8.0.x line to match the existing `Microsoft.EntityFrameworkCore` 8.0.11 (Npgsql's EF provider tracks EF Core major.minor).

### 7.2 Connection string format changes

SQL Server (current, e.g. `appsettings.json`):
```
Data Source=EZOFIS_DELL_I9;Database=ezofis_catalog_new;Persist Security Info=True;User ID=sa;Password=...;Pooling=False;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=True;Command Timeout=0
```
Postgres equivalent shape:
```
Host=<host>;Port=5432;Database=ezofis_catalog_new;Username=<user>;Password=...;Pooling=true;Command Timeout=0;SSL Mode=Require;Trust Server Certificate=true
```
Concrete tasks:
- Rewrite `appsettings*.json` (`DefaultConnection`), `HangfireWorker/appsettings*.json`, and drop the unused `eztapicontext` key.
- **Rewrite `Catalog.dbo.Tenants.ConnectionString` for every existing tenant row** — this is a one-time data migration script/job, not a config change, and it must be sequenced with the actual tenant database migration (§7.7) or every request will break between the two steps.
- `TenantDatabaseCreator.cs` needs a behavior change, not just a connection-string format change: connect to Postgres's `postgres` maintenance DB instead of `master`, query `pg_database` instead of `sys.databases`, and issue `CREATE DATABASE` **outside of any open transaction/`SqlConnectionStringBuilder`-transaction assumption** (Npgsql/Postgres reject `CREATE DATABASE` inside a transaction block).
- `TenantSignupService.cs` builds tenant connection strings — audit for any SQL-Server-specific builder options (`Encrypt`, `TrustServerCertificate`, `MultipleActiveResultSets`) that have no Postgres analog and should be dropped rather than mistranslated.

### 7.3 File-by-file change list

This groups the concrete files that need code changes; within each group the specific T-SQL construct(s) present are as documented in §3/§4.

**A. EF Core plumbing (mechanical swap + regenerate migrations)**
| File | Change |
|---|---|
| `src/BuildingBlocks/Catalog/CatalogServiceCollectionExtensions.cs` | `UseSqlServer` → `UseNpgsql` |
| `src/Modules/Users/Users.Infrastructure/UsersInfrastructureServiceCollectionExtensions.cs` | same |
| `src/Modules/Users/Users.Infrastructure/Persistence/UsersDbContextFactory.cs` | same |
| `src/Modules/Workflow/Workflow.Infrastructure/WorkflowInfrastructureServiceCollectionExtensions.cs` | same |
| `src/Api/Middleware/UsersPermissionSchemaEnsuringMiddleware.cs` | ad-hoc `DbContextOptionsBuilder<UsersDbContext>().UseSqlServer(...)` → `UseNpgsql` |
| `src/Api/Services/TenantSignupService.cs` (3 call sites) | same ad-hoc pattern |
| `src/Modules/Repository/Repository.Infrastructure/Services/ShareGuestUserProvisioningService.cs` | same ad-hoc pattern |
| `src/BuildingBlocks/Catalog/Migrations/*` + `src/Modules/Users/Users.Infrastructure/Migrations/*` | delete and regenerate against Postgres (don't hand-port — see §7.5) |
| `src/BuildingBlocks/Catalog/TenantDatabaseCreator.cs` | rewrite `master`/`sys.databases`/`CREATE DATABASE` logic (§7.2) |
| `src/BuildingBlocks/Catalog/TenantConnectionStringResolver.cs`, `TenantConnectionStringCache.cs` | no SQL changes, but exercise carefully once the underlying strings change format |

**B. Hangfire**
| File | Change |
|---|---|
| `src/Api/Program.cs` | `.UseSqlServerStorage(...)` → `.UsePostgreSqlStorage(...)` |
| `src/Workers/HangfireWorker/Program.cs` | same |

**C. Raw ADO.NET — provider + parameter typing (every file in §3.1's table)**
Mechanical part: `using Microsoft.Data.SqlClient` → `using Npgsql`; `SqlConnection`→`NpgsqlConnection`; `SqlCommand`→`NpgsqlCommand`; `SqlParameter`→`NpgsqlParameter`; `SqlDbType.X`→`NpgsqlDbType.X` (`EmailIngestService.cs`, `RepositoryItemMetadataUpdateHelper.cs`, `WorkflowLegacyMailboxSyncService.cs` need the enum-mapping pass specifically). Non-mechanical part: every SQL string in these files needs the dialect rewrites from §4 — this cannot be done as a blind find/replace, each file needs a read-through. Priority order (highest query complexity / most T-SQL-specific constructs first):
1. `WorkflowTableCreator.cs`, `RepositorySqlHelper.cs`, `RepositoryItemTableColumns.cs` — dynamic DDL engine (see §5)
2. `FormService.cs` / `FormService.Queries.cs` / `FormService.Controls.cs` / `FormService.UpdateDelete.cs`, `WorkflowLegacyMailboxQueryService.cs`, `WorkflowLegacyMailboxSyncService.cs`, `WorkflowTicketSearchService.cs` — heaviest `CROSS/OUTER APPLY`, `ISNULL`, `TOP` usage
3. `ConnectorProviderCatalog.cs` — the one `MERGE` → `ON CONFLICT`
4. Everything else in the §3.1 table

**D. `.sql` scripts (all 55, see §3.2)** — full rewrite against Postgres DDL syntax (identifier quoting, types per §7.4, `IDENTITY`→`GENERATED ALWAYS AS IDENTITY`, schema-creation `IF NOT EXISTS` patterns, the temporal-table script in particular needs a design decision before it can be ported at all — see §5). Reconcile the 3 scripts that exist only under `src/Api/scripts/` into the canonical `scripts/` folder first so there's one copy to migrate, not two.

**E. PowerShell orchestration scripts** (`RunCatalogScripts.ps1`, `ResetCatalog.ps1`, `Apply-SchemaUpdates.ps1`, `ApplyWorkflowSchemaToTenant.ps1`, `Verify-Tables.ps1`, `RunTenantSchema.ps1`, the `Test-E2E*.ps1` suite) — these shell out to `sqlcmd`; will need a Postgres equivalent (`psql`) invocation, and any embedded T-SQL (`sys.*` checks, `sqlcmd`-specific `GO` batch separators) rewritten.

### 7.4 Data type mapping table (grounded in what this codebase actually uses)

| SQL Server type (confirmed usage) | PostgreSQL type | Notes |
|---|---|---|
| `uniqueidentifier` | `uuid` | Every PK/FK in EF-managed tables uses this — straightforward, but generation moves from `NEWID()`/C#-side `Guid.NewGuid()` to `gen_random_uuid()` (Postgres 13+, `pgcrypto`/`pgcrypto`-free via `gen_random_uuid()` built-in from PG13) or keep GUID generation in C# (simplest, no behavior change) |
| `nvarchar(8/16/32/64/128/256/512)` | `varchar(n)` | Same length semantics (character count, not bytes) — direct mapping |
| `nvarchar(max)` | `text` | Direct mapping |
| `datetime2` | `timestamptz` (preferred) or `timestamp` | Use `timestamptz` since the app already normalizes to UTC (`SYSUTCDATETIME()`, `DateTime.UtcNow`) — avoids a whole class of timezone bugs |
| `bit` | `boolean` | Direct mapping; watch for any code treating `bit` as `0`/`1` integer via ADO.NET rather than `bool` |
| `int` | `integer` | Direct mapping |
| `decimal(18,2)` (seen in `RepositorySqlHelper.MapDataTypeToSql`) | `numeric(18,2)` | Direct mapping |
| `date` (seen in dynamic repository columns) | `date` | Direct mapping |
| `tinyint` (seen via `SqlDbType.TinyInt` in `RepositoryItemMetadataUpdateHelper.cs`) | `smallint` | Postgres has no 1-byte integer type; `smallint` (2 bytes) is the standard substitute |
| `[dbo].[Table]` / `[workflow].[Table]` bracket-qualified names | `"dbo"."Table"` / `"workflow"."Table"`, or better: lowercase unquoted (`workflow.table`) | See case-sensitivity discussion in §7.6 — this is a design decision, not a mechanical substitution |
| Legacy `int identity` (only in `CreateLegacyTransactionTable_Manual.sql`, `ManualInsert_MSP_MF_MasterForm.sql`) | `integer generated always as identity` | Small, isolated blast radius |

### 7.5 EF migration strategy — regenerate, don't script-translate

For `CatalogDbContext` and `UsersDbContext`:
1. Swap the provider (§7.1/§7.3-A) against a scratch Postgres database.
2. **Delete** the existing `Migrations/` folders' generated files (keep the model/entity classes — those are provider-agnostic).
3. Run `dotnet ef migrations add InitialPostgres` fresh against each context, so EF's Npgsql provider picks native Postgres types (`uuid`, `text`, `timestamptz`, `boolean`) instead of leftover `HasColumnType("nvarchar...")` annotations that would otherwise force it back toward SQL-Server-shaped columns.
4. Diff the generated schema against the current SQL Server schema (column-by-column) to make sure nothing was silently dropped — especially default values, nullability, and any indexes/unique constraints defined via Fluent API.
5. For `WorkflowDbContext` (no migrations today): decide whether to (a) finally introduce proper EF migrations for it during this project, or (b) keep it script-first and just port `CreateWorkflowSchemaComplete.sql` to Postgres DDL directly. Given the temporal-table dependency and the dynamic per-workflow tables layered on top of this schema, **(b) is lower-risk** — don't take on an EF-migrations introduction project at the same time as a database engine migration.
6. Do **not** attempt to run the old SQL-Server-targeted migration `Up()`/`Down()` C# files against Postgres — EF's SQL Server migration operations don't have a 1:1 Postgres translation for several `HasColumnType` calls, and hand-patching 21 migration files (6 Catalog + 15 Users) is more error-prone than regenerating from the current model snapshot.

### 7.6 Risk areas

- **Case sensitivity & identifier quoting.** SQL Server is case-insensitive by default collation; Postgres folds unquoted identifiers to lowercase and is case-sensitive once quoted. This codebase's tables/columns are PascalCase (`WorkflowInstances`, `TenantId`) referenced via `[bracket]` quoting in raw SQL and via EF's default quoting in migrations. Every raw-SQL file that currently relies on SQL Server's forgiving case-folding (e.g., mixing `[TenantId]` and `[tenantid]`, or building column names dynamically without consistent casing) will break outright on Postgres unless identifiers are consistently double-quoted with exact-case matches everywhere, including inside every dynamically-built DDL/DML string in `WorkflowTableCreator.cs` and `RepositorySqlHelper.cs`. **Recommend deciding up front**: either (a) keep PascalCase and quote everything religiously (higher fidelity to current code, more verbose SQL, easy to get wrong), or (b) adopt lowercase/snake_case for all new Postgres schema objects and accept a larger rename (cleaner long-term, bigger one-time diff). This decision gates a large fraction of the file-by-file work in §7.3 and should be made before writing any migration code.
- **Dynamic identifier construction.** Table/column names built via string concatenation (`$"Items_{suffix}"`, `RepositorySqlHelper.SanitizeColumnName`, etc.) need re-validation against Postgres identifier rules (63-byte length limit vs SQL Server's 128, and different reserved-word lists) in addition to the quoting question above.
- **Transaction / isolation-level behavior.** SQL Server defaults to `READ COMMITTED` (with `READ_COMMITTED_SNAPSHOT` possibly enabled — worth checking on the actual production DB); Postgres's `READ COMMITTED` has different lock/MVCC visibility behavior, and Postgres has no direct equivalent of SQL Server's `NOLOCK`/`(READUNCOMMITTED)` table hints (grep found none in this codebase, which is good, but confirm during the file-by-file pass). Also: **`CREATE DATABASE` cannot run inside a transaction in Postgres** — directly affects `TenantDatabaseCreator.cs`'s connection/transaction handling, not just its SQL text.
- **`master`-database administrative pattern.** As above — needs an architectural change (connect-to-`postgres`-DB pattern), not a syntax swap.
- **Temporal tables have no native target.** This is a genuine open design question (trigger-based history table vs. extension vs. folding into the existing ActivityLog module) that blocks a straight port of `workflow.WorkflowInstances`.
- **The "second migration system."** The ~25 `Add*/Alter*/DropAllTenants.sql` scripts plus their PowerShell "run against every tenant" runners are a parallel, ad-hoc migration mechanism alongside EF Core migrations. Decide during planning whether Postgres-era schema changes should be unified into EF migrations going forward (recommended, reduces long-term dialect risk) or whether this pattern continues on Postgres.
- **Hidden coupling in the connection-string-as-data pattern.** Because tenant connection strings live in `Catalog.dbo.Tenants`, any bug in the data-migration step that rewrites them leaves some tenants pointed at SQL Server and others at Postgres simultaneously with no compile-time signal — this needs an explicit verification step (query every tenant row post-migration and confirm connectivity) before cutover, not just a "run once" script.
- **NuGet reference cleanup risk.** `Billing.Infrastructure` and `Reporting.Infrastructure` reference EF Core SQL Server packages with no confirmed `DbContext` usage — verify before deciding whether they need migration work at all, to avoid wasted effort.

### 7.7 Suggested execution order

1. **Decisions first** (blocks everything else): identifier casing/quoting convention (§7.6), temporal-table replacement design (§5), and confirm Billing/Reporting EF references are actually dead.
2. **Config & connection strings** — `appsettings*.json`, `TenantDatabaseCreator.cs`'s master/`sys.databases`→`postgres`/`pg_database` rework, connection-string-builder audit in `TenantSignupService.cs`. Stand up a scratch Postgres instance to develop/test against from here on.
3. **ORM layer** — package swap, `UseNpgsql` everywhere (§7.3-A), regenerate EF migrations for `CatalogDbContext`/`UsersDbContext` (§7.5), diff schemas.
4. **Static `.sql` scripts** — port the 55 scripts in dependency order: bootstrap (§3.2 category 1) → schema-complete scripts (category 2) → incremental Add/Alter scripts (category 3) → seed/manual scripts (category 4). This produces a Postgres-native tenant database you can provision end-to-end before touching the raw ADO.NET query code.
5. **Raw ADO.NET / dynamic DDL layer** — in the priority order given in §7.3-C, starting with the dynamic-DDL engine (`WorkflowTableCreator.cs`, `RepositorySqlHelper.cs`) since every other Workflow/Repository query depends on the tables it creates being correct first.
6. **Hangfire** — package swap + storage init, on both `src/Api` and `HangfireWorker`; can happen in parallel with step 5 since it's isolated.
7. **PowerShell tooling** — `sqlcmd`→`psql` rewrites, once the SQL they wrap has stabilized.
8. **Data migration tooling & rehearsal** — script to migrate one tenant end-to-end (schema + data + rewrite its `Tenants.ConnectionString` row), rehearse against a copy of a real tenant DB, measure downtime.
9. **Testing** — the functional checklist already listed in the companion prep doc (§6.3 there): tenant signup/provisioning, auth/login/guest-share/sign-request, repository list/workspace/security/share/sign, workflow start/inbox/approve/history/comments, credits/billing, activity/event logs, Hangfire jobs (email ingest, OCR, archive, AP agent) — plus explicitly re-test the temporal-table replacement and every dynamically-created workflow/repository table shape.
10. **Cutover & decommission** — per-tenant or big-bang per the open decision in the companion doc's §9, then remove `Microsoft.Data.SqlClient`/`Microsoft.EntityFrameworkCore.SqlServer`/`Hangfire.SqlServer` references entirely once no tenant is left on SQL Server.

---

## 8. Open questions to resolve before implementation starts

1. Identifier casing convention for the new Postgres schema (PascalCase+quoted vs. lowercase/snake_case) — see §7.6.
2. Temporal-table replacement strategy for `workflow.WorkflowInstances` — trigger-based history table, `temporal_tables` extension, or fold into ActivityLog — see §5.
3. Confirm `Billing.Infrastructure` and `Reporting.Infrastructure`'s EF Core SQL Server package references are genuinely unused (no `DbContext` found in either) before scoping them out.
4. Whether to finally introduce EF Core migrations for `WorkflowDbContext` during this project, or keep it script-first on Postgres too — recommend script-first (§7.5, step 5).
5. Whether the ~25 hand-rolled "apply to all tenants" `.sql`/PowerShell scripts should be folded into EF migrations going forward, or continue as a parallel mechanism on Postgres.
6. Migration/cutover style — big-bang vs. phased per-tenant vs. new-tenants-only-on-Postgres (this is flagged as an open decision in the companion prep doc too — same answer should drive both docs).
7. Clean up: delete/archive the stale `v6Api/` nested repo, reconcile the 3 scripts that only exist under `src/Api/scripts/` into the canonical `scripts/` folder, and remove the unreferenced `eztapicontext` connection string before starting.
