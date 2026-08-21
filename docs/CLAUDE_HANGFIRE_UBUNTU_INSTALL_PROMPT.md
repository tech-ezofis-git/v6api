# Claude prompt — install V6 Hangfire on WSL Ubuntu

Copy everything below the line into Claude (Claude Code / Claude.ai / Cursor).

---

## Prompt

Install **V6 Hangfire-only** on **WSL Ubuntu** on this Windows Server. The **core API is already on Azure** at `https://demo.ezofis.com/v6api`. Azure must keep `Hangfire:RunServerInApi = false`. This Ubuntu host only **processes** Hangfire jobs.

### Published build (already on Windows)

- Windows: `D:\Aravinthan_Backup\V6_Hangfire_Linux`
- WSL: `/mnt/d/Aravinthan_Backup/V6_Hangfire_Linux`
- Install target: `/opt/v6-hangfire`
- Entry: `dotnet /opt/v6-hangfire/SaaSApp.Api.dll`
- Runtime: **.NET 8** ASP.NET Core (`aspnetcore-runtime-8.0`), linux-x64, framework-dependent
- Config to use: `appsettings.Production.json` (already in publish folder)
- Reference copy: `appsettings.Hangfire.Ubuntu.json`
- Docs in folder: `HANGFIRE_UBUNTU_AZURE_SPLIT.md`, `CLAUDE_INSTALL_PROMPT.md`

### Architecture (do not violate)

1. Azure demo API enqueues jobs + may register email cron.
2. Ubuntu: `Hangfire:RunServerInApi = true`, `WorkerCount = 20`.
3. Ubuntu: `EmailIngest:HangfireEnabled = false` (Azure owns cron — no double register).
4. Same catalog SQL as demo Azure (`ConnectionStrings:DefaultConnection`).
5. Ubuntu must reach: catalog SQL, tenant SQL, blob, Python HTTPS, Azure API HTTPS.
6. Bind Kestrel to localhost only: `ASPNETCORE_URLS=http://127.0.0.1:5055` (not a public API).
7. Prefer systemd; if WSL has no systemd, provide start script + Windows Task Scheduler fallback.
8. Dashboard for ops: prefer `https://demo.ezofis.com/v6api/hangfire` (Azure). Local Ubuntu dashboard optional at `http://127.0.0.1:5055/hangfire`.

### Required Production config values

Edit `/opt/v6-hangfire/appsettings.Production.json` (or env vars). These are already partially set in the publish folder:

```json
{
  "Hangfire": {
    "RunServerInApi": true,
    "WorkerCount": 20
  },
  "EmailIngest": {
    "HangfireEnabled": false
  },
  "ApAgent": {
    "Enabled": true,
    "ApiBaseUrl": "https://demo.ezofis.com/v6api/api/workflows",
    "PythonServiceUrl": "https://REPLACE_WITH_PYTHON_HOST/api/ap-agent/run",
    "TimeoutMinutes": 30
  },
  "FormMasterFileImport": {
    "UseHangfirePython": true,
    "PythonServiceUrl": "https://cloud.ezofis.com/api/ezDataImport"
  },
  "Agents": {
    "ChatUrl": "https://cloud.ezofis.com/chat"
  },
  "ConnectionStrings": {
    "DefaultConnection": "REPLACE_WITH_SAME_CATALOG_SQL_AS_DEMO_AZURE_API"
  },
  "HttpsRedirection": { "Enabled": false },
  "Swagger": { "Enabled": false },
  "PathBase": ""
}
```

For **cloud.ezofis.com** API production, also merge keys from `src/Api/appsettings.Production.example.json` (`FormMasterFileImport:PythonServiceUrl`, `Agents:ChatUrl`). Ask me for the real catalog SQL connection string and other Python host URLs if still placeholders. Do not invent secrets. Keep blob / auth settings from published `appsettings.json` if they are already correct for demo, or ask me.

### SSL / networking notes

- Azure does **not** call Ubuntu Hangfire over HTTP for jobs — they share SQL.
- Ubuntu → Azure API and Python must use **HTTPS**.
- Azure SQL / catalog SQL: use encrypted connection as required; open firewall for this server’s outbound IP.
- Do **not** put Swagger URL in config. ApiBaseUrl is `https://demo.ezofis.com/v6api/api/workflows` only.

### Steps to perform

1. Verify WSL Ubuntu (`wsl -l -v`, `uname -a`).
2. Install `aspnetcore-runtime-8.0` if missing; verify `dotnet --list-runtimes`.
3. `sudo mkdir -p /opt/v6-hangfire` and copy all files from `/mnt/d/Aravinthan_Backup/V6_Hangfire_Linux/` into it.
4. Fill placeholders in `appsettings.Production.json`.
5. Create `/etc/systemd/system/v6-hangfire.service`:

```ini
[Unit]
Description=V6 Hangfire Worker (WSL Ubuntu)
After=network.target

[Service]
WorkingDirectory=/opt/v6-hangfire
ExecStart=/usr/bin/dotnet /opt/v6-hangfire/SaaSApp.Api.dll
Restart=always
RestartSec=10
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://127.0.0.1:5055
Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false

[Install]
WantedBy=multi-user.target
```

6. `sudo systemctl daemon-reload && sudo systemctl enable --now v6-hangfire`
7. `sudo systemctl status v6-hangfire` and `sudo journalctl -u v6-hangfire -n 80 --no-pager`
8. Confirm logs show Hangfire server started with workers.
9. If systemd unavailable in WSL: enable systemd via `/etc/wsl.conf` `[boot] systemd=true` + `wsl --shutdown`, or create `/opt/v6-hangfire/start-hangfire.sh` and a Windows scheduled task that runs `wsl -e /opt/v6-hangfire/start-hangfire.sh`.

### Deliverables back to me

- Install path used
- .NET runtimes installed
- `systemctl status` (or fallback method)
- Log lines proving Hangfire workers started
- List of any secrets still needed (keys only, not values)

Start now: check WSL/.NET, copy publish folder, then configure and start the service.
