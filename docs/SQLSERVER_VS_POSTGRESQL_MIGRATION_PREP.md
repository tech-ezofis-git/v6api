# SQL Server vs PostgreSQL — Decision & Migration Prep (V6 API)

**Audience:** Architecture / Tech leads / Management  
**Product:** ezofis V6 SaaS API (`v6APi`)  
**Stack today:** ASP.NET Core 8 + Microsoft SQL Server (catalog + multi-tenant DBs)  
**Document purpose:** Compare databases, map company needs to PostgreSQL, and answer whether source-code change is possible (and how hard).  
**Last updated:** August 2026

---

## 1. Executive summary

| Question | Answer |
|----------|--------|
| Is PostgreSQL good for enterprise? | **Yes** |
| Does it fit our needs (low cost, Linux, JSON/search, .NET Core)? | **Yes — preferred long-term** |
| Can we change the V6 API source from SQL Server → PostgreSQL? | **Yes, possible** |
| Is it easy? | **No — high effort / multi-month program** |
| Should we stop SQL Server immediately? | **No — keep shipping on SQL Server; plan Postgres as a program** |

**Recommendation**

1. **Target platform (future):** PostgreSQL on Linux (AWS RDS / Azure Flexible Server / self-managed K8s).  
2. **Current delivery:** Stay on SQL Server until a funded migration phase.  
3. **Source change:** Possible, but treat as a **full data-platform migration**, not a config switch.

---

## 2. Comparison — SQL Server vs PostgreSQL

### 2.1 Side-by-side

| Area | SQL Server / Azure SQL | PostgreSQL |
|------|------------------------|------------|
| License cost | Commercial / Azure SQL billed | Open source (optional paid support) |
| Enterprise maturity | Excellent | Excellent |
| Linux / Kubernetes | Supported (Linux SQL / containers) | Native strength |
| AWS / multi-cloud | Good | Excellent |
| .NET Core support | Native (`Microsoft.Data.SqlClient`, EF SqlServer) | Excellent (`Npgsql`, EF Npgsql) |
| JSON | Good (`JSON`, `OPENJSON`) | Excellent (`jsonb` + GIN indexes) |
| Full-text / search | Full-Text Search | Built-in FTS; extend with pg_trgm / extensions |
| Analytics growth | Strong | Strong; often preferred for JSON-heavy analytics |
| HA / backups | Azure SQL / Always On | RDS Multi-AZ, Patroni, managed backups |
| Windows / IIS shops | Most natural | Also fine with Linux API hosts |
| Team skills (typical MS shop) | High | Needs Postgres DBA / training |
| Tooling | SSMS, Azure Portal | pgAdmin, DBeaver, cloud consoles |

### 2.2 Cost (high level)

| Cost driver | SQL Server | PostgreSQL |
|-------------|------------|------------|
| Engine license | Often significant (or included in Azure SQL tier) | None for community |
| Managed cloud | Azure SQL pricing | Usually lower for equivalent size (vendor-dependent) |
| Ops / DBA | Familiar if MS-skilled | Training or hire |
| Migration one-time | N/A (already here) | Large one-time engineering cost |

**Rule of thumb:** Postgres wins **ongoing license/cloud** cost; migration has a **large one-time** engineering cost.

### 2.3 JSON / search / analytics

| Need | SQL Server | PostgreSQL |
|------|------------|------------|
| Store flexible document metadata as JSON | Supported | **Stronger** (`jsonb`) |
| Index inside JSON | Possible | **Natural** with GIN |
| Full-text search | Available | Available + extensions |
| Heavy reporting on semi-structured data | Possible | Often smoother |

For “heavy JSON + search growth,” **PostgreSQL is the better long-term fit**.

### 2.4 .NET Core

**.NET Core does not require SQL Server.**

Production pattern:

```text
ASP.NET Core API  →  Npgsql  →  PostgreSQL
ASP.NET Core API  →  EF Core UseNpgsql()  →  PostgreSQL
Hangfire          →  PostgreSQL storage (or alternate queue)
```

So “we use .NET Core” is **compatible** with Postgres.

---

## 3. Our needs → which database?

### 3.1 Stated needs

| Need | Preference |
|------|------------|
| Lower license cost / open source | PostgreSQL |
| Deploy on Linux / Kubernetes / AWS / multi-cloud | PostgreSQL |
| Heavy JSON, search, analytics growth | PostgreSQL |
| Postgres DBA skills (have or hire) | Assumed yes |
| Keep .NET Core API | Compatible with PostgreSQL |

### 3.2 Conclusion for needs

**PostgreSQL matches the product direction.**  
SQL Server remains the **current production reality** of V6 API.

---

## 4. Is source-code change possible?

### 4.1 Short answer

**Yes — source change is possible.**  
It is **not** a connection-string-only change. It is a **full platform migration** of data access, SQL dialect, scripts, jobs, and tenant provisioning.

### 4.2 Why it is hard in *this* codebase

Current V6 API is tightly coupled to SQL Server:

| Coupling | Evidence in V6 |
|----------|----------------|
| ADO.NET provider | Widespread `Microsoft.Data.SqlClient` / `SqlConnection` |
| EF provider | `UseSqlServer(...)` in Catalog, Users, Workflow, etc. |
| Hangfire | SQL Server storage |
| T-SQL dialect | `NVARCHAR`, `UNIQUEIDENTIFIER`, `SYSUTCDATETIME()`, `OPENJSON`, `NEWID()`, `GO` batches |
| Dynamic SQL | Workflow per-suffix tables, repository item tables, filters |
| Ops scripts | Large set of `.sql` scripts under `src/Api/scripts` (T-SQL / all-tenants loops) |
| Multi-tenancy | Catalog DB + per-tenant SQL Server databases / connection strings |

Rough scale (order of magnitude):

- **Many** projects and services use `SqlConnection` / SqlClient  
- **Dozens** of SQL scripts are SQL Server–specific  
- Modules affected: **Catalog, Users, Repository, Workflow, Billing, ActivityLog, DMS, Hangfire worker, Api**

### 4.3 What “possible” means

| Approach | Possible? | Notes |
|----------|-----------|-------|
| Swap connection string only | **No** | Will fail immediately |
| Rewrite all SQL + providers to Postgres | **Yes** | Full migration program |
| Dual-run (SQL Server + Postgres) temporarily | **Yes** | Complex; only for phased cutover |
| New greenfield modules on Postgres only | **Yes** | Lower risk entry |

---

## 5. Difficulty assessment

### 5.1 Effort rating

| Area | Difficulty | Why |
|------|------------|-----|
| NuGet / DI (`UseNpgsql`, Hangfire PG) | Medium | Mechanical but touches all hosts |
| EF migrations / model | Medium–High | Type mappings (`uuid`, `timestamptz`) |
| Raw ADO.NET services | **Very High** | Hundreds of call sites; T-SQL → PL/pgSQL / ANSI |
| Dynamic workflow tables | **Very High** | Table naming, scripts, sync jobs |
| Repository dynamic columns / `OPENJSON` filters | **Very High** | Must redesign JSON filter SQL |
| Schema ensure scripts | High | Rewrite `CREATE`/`ALTER` for Postgres |
| All-tenants migration scripts | High | Today uses SQL Server linked-style loops |
| Data migration (all tenants) | **Very High** | Downtime / dual-write / cutover plan |
| QA / regression | **Very High** | Auth, workflow, repository, billing, Hangfire |

**Overall: High / Very High** — treat as a **program (months)**, not a sprint.

### 5.2 Indicative phases (planning only)

| Phase | Scope | Outcome |
|-------|-------|---------|
| 0 — Decision | This document + budget + owner | Go / No-go |
| 1 — Spike | One module (e.g. Catalog read) on Postgres | Prove Npgsql + types |
| 2 — Abstraction | Introduce DB abstraction / SQL dialect helpers | Reduce future lock-in |
| 3 — Schema port | Postgres DDL for catalog + one tenant | Schema parity |
| 4 — Module port | Users → Repository → Workflow → Billing → Logs | Feature parity |
| 5 — Jobs | Hangfire / workers on Postgres | Background parity |
| 6 — Data migrate | Tools + rehearsal + cutover | Production move |
| 7 — Decommission | Remove SqlClient paths | SQL Server exit |

---

## 6. Source changes required (checklist)

### 6.1 Packages / hosting

- [ ] Replace `Microsoft.Data.SqlClient` with `Npgsql` (where ADO is used)  
- [ ] Replace `UseSqlServer` with `UseNpgsql`  
- [ ] Hangfire SQL Server → Hangfire PostgreSQL (or alternate)  
- [ ] Connection string format (`Host=;Username=;Password=;Database=`)  
- [ ] Linux deployment images / K8s secrets for Postgres  

### 6.2 SQL dialect mapping (examples)

| SQL Server | PostgreSQL |
|------------|------------|
| `UNIQUEIDENTIFIER` | `uuid` |
| `NVARCHAR(n)` / `NVARCHAR(MAX)` | `varchar` / `text` |
| `DATETIME2` | `timestamptz` (preferred) |
| `BIT` | `boolean` |
| `NEWID()` | `gen_random_uuid()` |
| `SYSUTCDATETIME()` | `now() AT TIME ZONE 'utc'` / `timezone('utc', now())` |
| `OPENJSON` | `jsonb` operators / `jsonb_array_elements` |
| `IF NOT EXISTS ... CREATE` (T-SQL batches) | Postgres `DO $$ ... $$` / migration tooling |
| Schemas `[repository].[Items_…]` | `"repository"."Items_…"` (quoting rules differ) |
| `GO` | Not used (separate statements / migrations) |

### 6.3 Functional areas to retest after change

- [ ] Tenant signup / DB provision  
- [ ] Login / JWT / guest share / sign-request invites  
- [ ] Repository list / workspace / security / share / sign  
- [ ] Workflow start / inbox / approve / history / comments  
- [ ] Credits / billing  
- [ ] Activity / event logs  
- [ ] Hangfire jobs (email ingest, OCR, archive, AP agent)  

---

## 7. Risks

| Risk | Impact | Mitigation |
|------|--------|------------|
| Underestimating raw SQL volume | Delay / bugs | Spike + inventory all `SqlConnection` sites |
| Silent SQL dialect bugs | Data corruption / wrong filters | Golden regression suite per module |
| Multi-tenant cutover downtime | Business impact | Rehearse; blue/green or dual-write |
| Hangfire job store mismatch | Missed jobs | Migrate jobs carefully; drain queues |
| Team lack of Postgres ops | Outages | Hire/train DBA; use managed Postgres first |
| Parallel feature work on SQL Server | Merge conflicts | Feature freeze window for data layer |

---

## 8. Recommended decision

### 8.1 Strategic target

**Adopt PostgreSQL** as the long-term database for:

- Lower license / cloud cost  
- Linux / Kubernetes / AWS / multi-cloud  
- Heavy JSON + search + analytics  
- .NET Core compatibility  

### 8.2 Tactical near-term

**Do not block current V6 delivery** (sign request, share, timeline, security) on migration.  
Continue SQL Server for production until Phase 0–1 are approved and funded.

### 8.3 Feasibility statement (for management)

> Changing V6 API source from Microsoft SQL Server to PostgreSQL **is possible**.  
> It requires rewriting data access, T-SQL scripts, Hangfire storage, and migrating all tenant databases.  
> Effort is **high**; success needs dedicated ownership, regression tests, and a phased cutover.  
> .NET Core is **not** a blocker.

---

## 9. Open decisions (fill in)

| Decision | Options | Owner | Due |
|----------|---------|-------|-----|
| Target cloud for Postgres | AWS RDS / Azure Flexible Server / self-managed | | |
| Migration style | Big-bang / phased modules / new tenants only | | |
| Budget / timeline | e.g. 1 quarter / 2 quarters | | |
| Feature freeze during data-layer port? | Yes / No | | |
| Who owns Postgres DBA? | Internal / vendor | | |

---

## 10. Appendix — Current architecture snapshot

```text
Client / FE
    │
    ▼
ASP.NET Core V6 API (.NET 8)
    │
    ├── Catalog DB (SQL Server)     → tenants, mail settings, shares index, …
    ├── Tenant DB (SQL Server) × N  → users, repository, workflow, billing, logs
    └── Hangfire (SQL Server)
```

**Desired (future):**

```text
Client / FE
    │
    ▼
ASP.NET Core V6 API (.NET 8) on Linux / K8s
    │
    ├── Catalog DB (PostgreSQL)
    ├── Tenant DB (PostgreSQL) × N
    └── Hangfire (PostgreSQL)  [or alternate job store]
```

---

## 11. Document control

| Version | Date | Author | Notes |
|---------|------|--------|-------|
| 0.1 | Aug 2026 | Engineering | Initial comparison + feasibility for V6 API |

**Next step:** Review with stakeholders → approve Phase 0/1 spike → produce detailed effort estimate from a full `SqlConnection` / script inventory.
