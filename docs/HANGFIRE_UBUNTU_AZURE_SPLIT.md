# Hangfire on Ubuntu / WSL — Azure API Split

**Audience:** DevOps, backend  
**Last updated:** August 2026  
**Product:** V6 SaaS API (`ezSaaSApi` / v6api)

Use this when the **core API runs on Azure** and **Hangfire workers run on Ubuntu** (native Linux or **WSL on Windows Server**).

Related: [HANGFIRE_ARCHITECTURE_AND_CAPACITY.md](./HANGFIRE_ARCHITECTURE_AND_CAPACITY.md)

---

## 1. Does Azure “access” Hangfire over HTTP?

**No — not for job processing.**

Azure App Service (main API) and the Ubuntu Hangfire host talk through the **shared catalog SQL** Hangfire tables (`HangFire.*`), not by calling each other’s Hangfire URL.

```
Other systems / UI
       │  HTTPS
       ▼
Azure Main API  (Hangfire:RunServerInApi = false)
  • HTTP APIs
  • Enqueues jobs
  • Registers email-ingest cron (optional)
       │
       │  SQL (TDS) — same DefaultConnection
       ▼
Catalog SQL — HangFire.Job / JobQueue / Server / …
       │
       │  SQL poll
       ▼
Ubuntu Hangfire host  (Hangfire:RunServerInApi = true)
  • Processes AP Agent / email ingest / master import
       │
       ├──► HTTPS → Python services
       └──► HTTPS → Azure Main API (progress / move-next callbacks)
```

| Direction | Protocol | Purpose |
|-----------|----------|---------|
| Client → Azure API | **HTTPS** | Normal API calls |
| Azure API → Catalog SQL | SQL / TDS (encrypt as required by Azure SQL) | Enqueue + cron registration |
| Ubuntu Hangfire → Catalog SQL | SQL / TDS | Dequeue / process jobs |
| Ubuntu Hangfire → Azure API | **HTTPS** | AP Agent callbacks (`ApAgent:ApiBaseUrl`) |
| Ubuntu Hangfire → Python | **HTTPS** (or HTTP only on private LAN) | AP Agent / master import |
| Browser → Ubuntu `/hangfire` | Optional HTTP/HTTPS | **Dashboard only** — not required for Azure |

**Other systems do not need a Hangfire URL.** They only call the Azure main API.

---

## 2. SSL / TLS — what must be enabled?

### Required / strongly recommended

| Link | SSL needed? | Notes |
|------|-------------|--------|
| Public clients → Azure API | **Yes (HTTPS)** | Azure App Service default TLS certificate |
| Ubuntu Hangfire → Azure API (`ApAgent:ApiBaseUrl`) | **Yes (HTTPS)** | Worker calls Azure over the internet; use `https://…` |
| Ubuntu Hangfire → Python (if Python is public / Azure) | **Yes (HTTPS)** | Prefer TLS on Python endpoints |
| Azure API / Ubuntu → **Azure SQL** | **Encrypt / TLS** | Use Azure SQL connection string with encryption (`Encrypt=True` / modern drivers default). Allow Ubuntu outbound to Azure SQL (firewall / private endpoint) |

### Not required for job flow

| Link | SSL needed? | Notes |
|------|-------------|--------|
| Azure API → Ubuntu Hangfire HTTP | **No** | Azure does **not** call Ubuntu Hangfire for jobs |
| Ubuntu `/hangfire` dashboard | Optional | Localhost HTTP is fine for ops. If you expose the dashboard to other PCs, use VPN / reverse proxy with HTTPS + auth |

### Summary

- **Job pipeline:** SQL + Azure/Python **HTTPS** — no SSL between Azure and Hangfire HTTP.  
- **SSL to enable:** Azure App Service HTTPS (already), Azure SQL encryption, HTTPS URLs in Hangfire config for API + Python.  
- **Do not** depend on opening Hangfire port 5055 to Azure for jobs to work.

---

## 3. Azure main API configuration

Set on Azure App Service (Configuration / `appsettings`):

```json
"Hangfire": {
  "RunServerInApi": false
},
"ConnectionStrings": {
  "DefaultConnection": "<same catalog SQL as Ubuntu Hangfire>"
},
"EmailIngest": {
  "HangfireEnabled": true,
  "HangfireCron": "*/5 * * * *"
},
"ApAgent": {
  "PythonServiceUrl": "https://<python-host>/api/ap-agent/run",
  "ApiBaseUrl": "https://<your-azure-api>/V6API/api/workflows",
  "TimeoutMinutes": 30
}
```

| Key | Value | Why |
|-----|-------|-----|
| `Hangfire:RunServerInApi` | **`false`** | API enqueues only; Ubuntu processes |
| `DefaultConnection` | Same catalog as worker | Shared Hangfire storage |
| `EmailIngest:HangfireEnabled` | **`true`** on Azure | Register recurring cron once |
| Deploy version | Compatible with Ubuntu publish | Job type deserialize |

Checklist:

- [ ] Hangfire tables exist on catalog (`01c_InstallHangfire.sql`)  
- [ ] `RunServerInApi = false`  
- [ ] Same catalog connection string as Ubuntu  
- [ ] Azure SQL firewall allows **Ubuntu / Windows Server outbound IP** (or Private Link)  
- [ ] `/hangfire` on Azure optional; protect or disable if unused  

---

## 4. Ubuntu Hangfire host configuration

```json
"Hangfire": {
  "RunServerInApi": true,
  "WorkerCount": 20
},
"EmailIngest": {
  "HangfireEnabled": false
},
"HttpsRedirection": {
  "Enabled": false
},
"ConnectionStrings": {
  "DefaultConnection": "<same catalog SQL as Azure API>"
},
"ApAgent": {
  "PythonServiceUrl": "https://<python-host>/api/ap-agent/run",
  "ApiBaseUrl": "https://<your-azure-api>/V6API/api/workflows",
  "TimeoutMinutes": 30
},
"FormMasterFileImport": {
  "UseHangfirePython": true,
  "PythonServiceUrl": "https://cloud.ezofis.com/api/ezDataImport"
},
"Agents": {
  "ChatUrl": "https://cloud.ezofis.com/chat"
}
```

Cloud production overrides are also listed in `src/Api/appsettings.Production.example.json`.
| Key | Value | Why |
|-----|-------|-----|
| `RunServerInApi` | **`true`** | This machine runs workers |
| `WorkerCount` | **20–25** | Parallel jobs |
| `EmailIngest:HangfireEnabled` | **`false`** | Avoid double cron (Azure owns schedule) |
| `ApAgent:ApiBaseUrl` | **HTTPS Azure API** | Callbacks from worker |
| Kestrel URL | `http://127.0.0.1:5055` | No public API; dashboard local only |

Ubuntu must reach: catalog SQL, tenant SQL, blob, Python HTTPS, Azure API HTTPS.

---

## 5. Publish path (Windows → Linux)

From the V6 repo (or use existing folder):

```powershell
dotnet publish "src\Api\SaaSApp.Api.csproj" `
  -c Release -r linux-x64 --self-contained false `
  -o "D:\Aravinthan_Backup\V6_Hangfire_Linux"
```

| Item | Path |
|------|------|
| Windows publish folder | `D:\Aravinthan_Backup\V6_Hangfire_Linux` |
| WSL path | `/mnt/d/Aravinthan_Backup/V6_Hangfire_Linux` |
| Suggested install on Ubuntu | `/opt/v6-hangfire` |
| Entry | `SaaSApp.Api.dll` (.NET 8 runtime) |
| Helper overrides | `appsettings.Hangfire.Ubuntu.json` |
| Claude/WSL install prompt | `CLAUDE_INSTALL_PROMPT.md` (in publish folder) |

> Until `src/Workers/HangfireWorker` has full Workflow DI, use **published `SaaSApp.Api`** as the Hangfire host on Ubuntu.

---

## 6. Install on WSL Ubuntu (Windows Server)

1. Install **ASP.NET Core 8.0 runtime** on Ubuntu (`aspnetcore-runtime-8.0`).  
2. Copy publish folder → `/opt/v6-hangfire`.  
3. Add Production secrets (connection strings, Python/API URLs) via `appsettings.Production.json` or environment variables.  
4. Create systemd unit (if WSL systemd enabled):

```ini
[Unit]
Description=V6 Hangfire Worker
After=network.target

[Service]
WorkingDirectory=/opt/v6-hangfire
ExecStart=/usr/bin/dotnet /opt/v6-hangfire/SaaSApp.Api.dll
Restart=always
RestartSec=10
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://127.0.0.1:5055

[Install]
WantedBy=multi-user.target
```

5. `sudo systemctl enable --now v6-hangfire`  
6. Logs: `sudo journalctl -u v6-hangfire -f` — expect Hangfire server / worker count.  

If systemd is unavailable in WSL: use a start script + Windows Task Scheduler / `wsl.exe -d Ubuntu -e …`.

---

## 7. Optional: Hangfire dashboard URL from another PC

Jobs **do not** need this. Dashboard only.

1. Change bind to `http://0.0.0.0:5055` (or host IP).  
2. Open Windows Firewall / WSL port forwarding for **5055**.  
3. Browse: `http://<windows-server-LAN-IP>:5055/hangfire`  
4. **Protect with auth** (do not leave open on the internet). Prefer VPN.  
5. For public HTTPS dashboard: put **nginx/Caddy** in front with a certificate — still not required for Azure job flow.

---

## 8. End-to-end verify

1. Azure API healthy (`https://<azure-api>/…`).  
2. Ubuntu service running; logs show workers.  
3. From UI/API: start workflow / AP Agent → job row in HangFire SQL.  
4. Job moves to **Processing** then **Succeeded** on Ubuntu.  
5. Python + Azure callbacks succeed (HTTPS).  
6. Healthy peak: Processing ≤ WorkerCount, Queued ≈ 0.

---

## 9. FAQ

**Q: Do I enable SSL between Azure and Hangfire?**  
A: Not over HTTP. They share SQL. Use TLS for Azure SQL and HTTPS for Azure API + Python URLs the worker calls.

**Q: What URL do other systems use?**  
A: Only the **Azure main API** HTTPS URL. Not the Ubuntu Hangfire port.

**Q: Can Azure call `http://ubuntu:5055/hangfire`?**  
A: Not needed for jobs. Only if you deliberately expose the dashboard (not recommended publicly).

**Q: Why HTTPS on `ApAgent:ApiBaseUrl`?**  
A: The worker runs outside Azure App Service and must call your public API securely.
