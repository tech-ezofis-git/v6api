# V6 Hangfire — Architecture, Capacity & Azure Cost Guide

**Audience:** Backend, DevOps, Python team, architects, stakeholders  
**Last updated:** July 2026  
**Product:** V6 SaaS API (`v6APi`)

---

## 1. Overview

Hangfire is the **background job engine** for V6. The API enqueues work; a dedicated Hangfire worker on Azure executes it asynchronously (Python AP Agent calls, email mailbox polling, master-file import).

| Concern | Answer |
|---------|--------|
| Software license | **Hangfire open-source** — no per-job or per-worker license fee |
| Storage | SQL Server Hangfire tables on catalog DB (`DefaultConnection`) |
| Dashboard | `/hangfire` on the API (protect with auth in production) |
| Python | Hangfire jobs **HTTP POST** to Python services (separate hosts) |
| Production pattern | **API enqueues only** + **dedicated Hangfire worker on Azure** |

Hangfire does **not** run Python inside .NET. It orchestrates C# jobs that call Python over HTTP.

**Jobs covered in this guide (production scope):**

- Email ingest (recurring poll → start workflow)  
- AP Agent (per ticket / workflow start)  
- Master file import (Python)

**Ubuntu / WSL Hangfire + Azure API split (SSL, publish path, install):** see [HANGFIRE_UBUNTU_AZURE_SPLIT.md](./HANGFIRE_UBUNTU_AZURE_SPLIT.md).

---

## 2. Azure architecture (production)

```
Azure App Service — V6 API
  ├── HTTP APIs (auth, workflows, billing, …)
  ├── Hangfire:RunServerInApi = false   ← does NOT process jobs
  └── BackgroundJob.Enqueue(...) → SQL Hangfire tables

Azure App Service / Container Apps — Hangfire Worker
  ├── Hangfire Server, WorkerCount = 20–25
  ├── Polls same Hangfire SQL storage
  ├── Runs: AP Agent, email ingest, master file import
  └── HTTP → Python services

Azure — Python services (separate)
  ├── AP Agent     (ApAgent:PythonServiceUrl)
  └── Master import (FormMasterFileImport:PythonServiceUrl)

Azure SQL — Catalog (Hangfire.* + catalog)
  └── Shared by API + Hangfire worker
```

API and worker **must share**:

- Same Hangfire SQL database  
- Compatible job assemblies / deployment version  
- Same config for tenant DBs, blob, Python URLs  

Worker host project: `src/Workers/HangfireWorker/` (wire full module DI before go-live).

---

## 3. Cost: Hangfire vs Azure (and alternatives)

### 3.1 Important distinction

| Item | Cost model |
|------|------------|
| **Hangfire (NuGet / OSS)** | **$0** license for core Hangfire used in V6 |
| **Azure resources that run Hangfire** | You pay for **App Service / Container Apps / SQL / network** — not “per Hangfire job” |
| **Python / OCR / LLM** | Separate compute (usually the largest variable cost under AP Agent load) |

So “Hangfire cost” in production = **Azure compute + SQL** for the worker pattern, not a Hangfire SaaS bill.

### 3.2 Estimated monthly Azure cost (target: ~20 concurrent AP Agent)

Prices are **approximate USD / month**, East US–class regions, pay-as-you-go list-style ballparks (2025–2026). Confirm in [Azure Pricing Calculator](https://azure.microsoft.com/pricing/calculator/) for your region and offers.

| Resource | Suggested SKU (start) | Approx. USD / month | Role |
|----------|----------------------|---------------------|------|
| **V6 API** | App Service **P1v3** (1 instance) | ~$140–180 | Enqueue jobs + HTTP APIs |
| **Hangfire worker** | App Service **P1v3** or **P2v3** (1 instance), Always On | ~$140–300 | 20–25 workers |
| **Python AP Agent** | App Service **P1v3–P2v3** or Container Apps (sized for 20 parallel) | ~$140–350+ | Heavy OCR/LLM work |
| **Python master import** | Can share AP Agent host or small separate app | ~$0–150 | Import jobs |
| **Azure SQL** (catalog + Hangfire tables) | **S3** / **GP Gen5 2 vCore** class (adjust to load) | ~$150–400 | Hangfire storage + catalog |
| **Tenant SQL** | Existing / per-tenant DBs | (existing budget) | Workflow data |
| **Blob storage** | Standard | ~$20–80+ (usage) | Files / masters |
| **Bandwidth / Private Link** | Optional | variable | Worker ↔ Python ↔ SQL |

**Rough total (API + Hangfire worker + Python + SQL for this pattern):** about **$600–1,300 / month** before discounts, reserved instances, or heavy LLM/OCR add-ons.

| Plan | Hangfire workers | Worker SKU ballpark | Extra vs 20-worker plan |
|------|------------------|---------------------|-------------------------|
| **Recommended** | 20–25 | P1v3 / P2v3 × 1 | Baseline |
| Higher peak | 40 | 2× P1v3/P2v3 or 1× P3v3 | ~+$140–300 / month worker only |

### 3.3 Hangfire-on-Azure vs other Azure job options

| Approach | Typical cost driver | Pros | Cons for V6 |
|----------|---------------------|------|-------------|
| **Hangfire + Azure App Service worker (current design)** | Always-on App Service + SQL | Already implemented; dashboard; retries; SQL storage; fits AP Agent + email cron | Always-on compute even when idle |
| **Azure Service Bus + Functions / Container Apps jobs** | Bus + executions / always-on consumers | Native Azure scaling, deep Azure integration | Rewrite enqueue/progress; rebuild email recurring; more ops design |
| **Azure Queue Storage + Functions** | Storage + executions | Cheap at low volume | Poor fit for long AP Agent (timeouts); rewrite |
| **Logic Apps / scheduled only** | Per action | Easy schedules | Not ideal for 20 parallel long AP Agent jobs |
| **Hangfire Cloud / Hangfire Pro** (optional commercial) | Vendor license | Extra features / support | **Not required** for current V6 OSS usage |

**Recommendation:** Keep **Hangfire OSS on Azure compute**. Cost is dominated by **API + worker + Python + SQL**, not by Hangfire licensing. Switching to Service Bus/Functions mainly pays for a rewrite, not a large license saving.

### 3.4 Cost control tips

- Size Hangfire for **peak concurrent AP Agent (~20)**, not total users (5000).  
- Keep API `RunServerInApi = false` so API SKU stays smaller.  
- Prefer **1 mid worker** (20–25 workers) over undersized Basic tiers that fail under load.  
- Scale **Python** with real concurrency — that is usually costlier than Hangfire itself.  
- Use Reserved Instance / Savings Plan on App Service + SQL when stable.  
- Mail every **1 minute** costs almost nothing extra in Azure (more Graph/API calls); it does not require more Hangfire workers for 10 tenants.

---

## 4. SQL / setup

Hangfire schema lives on the **catalog** database.

| Script / doc | Purpose |
|--------------|---------|
| `src/Api/scripts/01c_InstallHangfire.sql` | Create Hangfire tables |
| `src/Api/scripts/00_MASTER_SETUP_README.md` | Master setup includes Hangfire |

Tables: `HangFire.*` (Job, State, JobQueue, Server, …).

```json
"ConnectionStrings": {
  "DefaultConnection": "<catalog SQL — Hangfire storage>"
}
```

API and Hangfire worker both need this connection string.

---

## 5. Configuration reference

```json
"Hangfire": {
  "RunServerInApi": false,
  "WorkerCount": 20
},
"EmailIngest": {
  "HangfireEnabled": true,
  "HangfireCron": "*/5 * * * *",
  "TenantDiscoveryMinutes": 30
},
"ApAgent": {
  "Enabled": true,
  "PythonServiceUrl": "https://<python-host>/api/ap-agent/run",
  "ApiBaseUrl": "https://<api-host>/V6API/api/workflows",
  "TimeoutMinutes": 30
},
"FormMasterFileImport": {
  "Enabled": true,
  "UseHangfirePython": true,
  "PythonServiceUrl": "https://cloud.ezofis.com/api/ezDataImport",
  "TimeoutMinutes": 30
},
"Agents": {
  "ChatUrl": "https://cloud.ezofis.com/chat"
}
```

| Key | Meaning |
|-----|---------|
| `Hangfire:RunServerInApi` | Production API: **`false`** (enqueue only). |
| `Hangfire:WorkerCount` | Parallel jobs on the **dedicated worker** (recommend **20–25**). |
| `EmailIngest:HangfireEnabled` | Register recurring mail poll. |
| `EmailIngest:HangfireCron` | Default every **5** minutes; use `*/1 * * * *` for every minute. |
| `EmailIngest:TenantDiscoveryMinutes` | Rescan tenants for new mailboxes (default 30). |
| `ApAgent:PythonServiceUrl` | Python AP Agent URL (reachable from Hangfire worker). |
| `FormMasterFileImport:UseHangfirePython` | Enqueue master import job after upload. |
| `FormMasterFileImport:PythonServiceUrl` | Master Excel import Python URL (cloud: `/api/ezDataImport`). |
| `Agents:ChatUrl` | Chat UI base URL (config key for deploy; not used by API runtime code yet). |

Per mailbox: set `PollIntervalMinutes` to **1** if you want processing as often as every minute (must align with cron).

---

## 6. Where Hangfire is used

### 6.1 Recurring

| Recurring id | Class / method | Schedule | Purpose |
|--------------|----------------|----------|---------|
| `email-ingest-poll` | `RunEmailIngestPollJob.Execute` | `EmailIngest:HangfireCron` | Tenants with enabled mailboxes → enqueue per-tenant polls |

Registered in `src/Api/Program.cs` when Hangfire is enabled and `EmailIngest:HangfireEnabled` is true.

### 6.2 On demand

| Job | Class | Triggered from | Purpose |
|-----|-------|----------------|---------|
| Email ingest · tenant | `RunEmailIngestPollJob.ExecuteForTenant` | Recurring scheduler | Poll mailbox; start workflows for new attachments |
| AP Agent · tenant | `RunApAgentPythonJob.Execute` | Workflow start / email ingest | POST to Python AP Agent |
| Master file import · tenant | `RunMasterFileImportPythonJob.Execute` | Form master upload | POST to Python import API |

### 6.3 Key source files

| Area | Path |
|------|------|
| API Hangfire + mail cron | `src/Api/Program.cs` |
| AP Agent job | `src/Modules/Workflow/Workflow.Infrastructure/Jobs/RunApAgentPythonJob.cs` |
| AP Agent client | `.../Jobs/ApAgentPythonJobClient.cs` |
| AP Agent HTTP | `.../Services/ApAgentPythonPipelineService.cs` |
| Email ingest job | `.../Jobs/RunEmailIngestPollJob.cs` |
| Email tenant index | `.../Jobs/EmailIngestTenantIndex.cs` |
| Email service | `.../Services/EmailIngestService.cs` |
| Master import job | `.../Jobs/RunMasterFileImportPythonJob.cs` |
| Worker host | `src/Workers/HangfireWorker/Program.cs` |
| AP Agent progress | `.../Services/ApAgentJobProgressService.cs` |

### 6.4 Folder watch (files)

| Need | Status |
|------|--------|
| Mailbox email poll → start workflow | Implemented |
| Mail `QueryFilter` (provider query) | Partial |
| OneDrive / SharePoint / blob folder watch | Not implemented (future Hangfire poll or Graph webhook) |

---

## 7. How flows work

### 7.1 Ticket → AP Agent

```
Start workflow (TriggerApAgentPythonJob = true)
  → ApAgentPythonJobClient.EnqueueAsync
  → RunApAgentPythonJob → POST ApAgent:PythonServiceUrl
  → Python callbacks API (progress / move-next / complete)
  → Workflow advances
```

Max parallel AP Agent jobs ≈ `Hangfire:WorkerCount` (shared with email + master import on `default` queue).

### 7.2 Email → workflow (+ AP Agent)

```
Cron (e.g. every 1 or 5 minutes)
  → RunEmailIngestPollJob.Execute
  → ExecuteForTenant per mailbox tenant
  → Poll mail → start workflow → may enqueue AP Agent
```

**Latency:** up to cron × mailbox poll interval (e.g. ~1 minute if both are 1). Not zero-delay push.

### 7.3 Master file import

```
Master file upload → enqueue RunMasterFileImportPythonJob
  → POST FormMasterFileImport:PythonServiceUrl
```

---

## 8. Capacity planning

### 8.1 Target load

| Metric | Plan |
|--------|------|
| Tenants | ~10 |
| Users | ~5000 (not all concurrent) |
| Peak concurrent AP Agent | **~20** |
| Mailbox tenants | ~10 |
| Mail freshness | 5 min default, or **1 min** for faster pickup |

### 8.2 Hangfire + Python

| Setting | Recommended |
|---------|-------------|
| `Hangfire:WorkerCount` | **20–25** |
| API `RunServerInApi` | **false** |
| Python parallel capacity | **20–25** |

- 20 workers ⇒ 20 jobs at once.  
- If 30 tickets start together ⇒ 20 run, 10 queue; tickets still create immediately.  
- Hangfire workers and Python must match or the bottleneck moves to Python.

### 8.3 Azure compute guidance

| Role | Start SKU |
|------|-----------|
| Hangfire worker (20–25) | App Service **P1v3 / P2v3** Always On, or Container Apps equivalent |
| API (enqueue only) | **P1v3** (or S1 if light) |
| Python (20 parallel) | **P1v3–P2v3** / Container Apps (validate under load) |
| Avoid | Consumption Functions as Hangfire server; Basic App Service for AP Agent peak |

Optional HA: **2 Hangfire instances × 12–13 workers** ≈ 24–26 total.

---

## 9. Mail every minute

1. `EmailIngest:HangfireCron` = `*/1 * * * *`  
2. Mailbox `PollIntervalMinutes` = `1`  
3. `EmailIngest:HangfireEnabled` = `true`  
4. Dedicated Hangfire worker running  

Expectation: new mail → workflow within about **0–60 seconds**.

---

## 10. Production checklist

- [ ] Hangfire SQL installed on catalog DB  
- [ ] API + worker same `DefaultConnection`  
- [ ] API: `Hangfire:RunServerInApi = false`  
- [ ] Worker Always On; `WorkerCount` = 20–25  
- [ ] Python URLs reachable from **worker**  
- [ ] Python sized for ~20 concurrent AP Agent  
- [ ] Email cron + mailbox poll agreed  
- [ ] `/hangfire` protected  
- [ ] Load test 20 parallel starts; queue depth ≈ 0  
- [ ] Budget reviewed (API + worker + Python + SQL)  

---

## 11. Monitoring

| Tool | Watch |
|------|--------|
| `/hangfire` | Enqueued / Processing / Failed; `email-ingest-poll` |
| Logs | AP Agent + email ingest failures |
| Python | Concurrency, latency, errors |
| Azure cost | App Service + SQL + Python vs estimate |
| SQL | Hangfire growth; blocking |

Healthy peak-20: **Processing ≤ WorkerCount**, **Queued ≈ 0**.

---

## 12. FAQ

**Q: Do we pay Hangfire per job?**  
A: No. OSS Hangfire is free; you pay Azure for the machines/SQL that host it.

**Q: Is Hangfire cheaper than Azure Service Bus?**  
A: Often similar or lower TCO for V6 because the stack is already built. Bus/Functions need a rewrite; always-on consumers still cost money.

**Q: Does WorkerCount make mail faster?**  
A: No. Mail speed = cron + `PollIntervalMinutes`.

**Q: 20 workers = only 20 tickets ever?**  
A: No — only 20 **at the same time**.

**Q: Zero-delay mail/folder?**  
A: Not with polling. Use webhooks for near-instant; folder file watch is not implemented yet.

---

## 13. Summary (go-live)

| Layer | Choice |
|-------|--------|
| Hangfire software | OSS — **$0** license |
| Azure API | Enqueue only (`RunServerInApi = false`) |
| Azure Hangfire worker | Separate Always On, **WorkerCount 20–25** (~P1v3/P2v3) |
| Azure Python | Sized for **20–25** parallel AP Agent |
| Peak design | **20** concurrent ticket/AP Agent |
| Email | Recurring Hangfire poll (1 or 5 min) |
| Folder file watch | Future work |
| Est. platform cost (API+worker+Python+SQL) | ~**$600–1,300 / month** ballpark — confirm in Azure calculator |

Fits ~10 tenants, ~5000 users, ~20 concurrent AP Agent starts with minimal Hangfire queue delay under that peak.
