# Jira Python Gateway (Windows Server 2012 R2 workaround)

.NET `HttpClient` on Windows Server 2012 R2 fails TLS to Atlassian (Schannel).
Chrome/Postman/Python (OpenSSL + certifi) succeed. This gateway lets **SaaSApp.Api**
create Jira issues via `http://127.0.0.1:5055` so .NET never opens TLS to Atlassian.

## Flow

```text
POST /api/support-tickets  (SaaSApp.Api)
  → POST http://127.0.0.1:5055/jira/create-issue  (this gateway)
    → POST https://ezofis.atlassian.net/rest/api/3/issue  (Python + certifi)
```

## Setup on EZOFISNEW

1. Copy this folder to the server (e.g. `C:\ezofis\jira-gateway`).

2. Install dependency (once):

```powershell
python -m pip install -r requirements.txt
```

3. Create config:

```powershell
copy config.example.json config.json
notepad config.json
```

Set `Email`, `ApiToken`, `ProjectKey`, `IssueType` to match Jira.

4. Start the gateway:

```powershell
cd C:\ezofis\jira-gateway
python gateway.py
```

You should see: `Jira gateway listening on http://127.0.0.1:5055`

5. Health check:

```powershell
Invoke-WebRequest -Uri "http://127.0.0.1:5055/health" -UseBasicParsing
```

6. In SaaSApp.Api `appsettings.json`:

```json
"Jira": {
  "Enabled": true,
  "UseProxy": true,
  "ProxyBaseUrl": "http://127.0.0.1:5055",
  "BaseUrl": "https://ezofis.atlassian.net",
  "Email": "support@ezofis.com",
  "ApiToken": "",
  "ProjectKey": "VP",
  "IssueType": "Task"
}
```

`ApiToken` on the API can be empty when `UseProxy` is true (token lives in gateway `config.json`).
Keep `Email` on the API — it is used for support-team notification emails.

7. Restart / recycle the API app pool, then submit a support ticket.

## Run at startup (optional)

Use Task Scheduler:

- Trigger: At startup / At log on
- Action: `python.exe C:\ezofis\jira-gateway\gateway.py`
- Start in: `C:\ezofis\jira-gateway`
- Run whether user is logged on or not (service account)

Or NSSM:

```powershell
nssm install JiraGateway "C:\Program Files\Python311\python.exe" "C:\ezofis\jira-gateway\gateway.py"
nssm set JiraGateway AppDirectory "C:\ezofis\jira-gateway"
nssm start JiraGateway
```

## Test create (optional)

```powershell
Invoke-WebRequest -Uri "http://127.0.0.1:5055/jira/create-issue" -Method POST -ContentType "application/json" -Body '{"supportCategory":"Test","priority":"Low","requestDescription":"gateway test","callerEmail":"test@example.com"}' -UseBasicParsing
```

## Endpoints

| Method | Path | Description |
|--------|------|-------------|
| GET | `/health` | Liveness |
| POST | `/jira/create-issue` | Create Jira issue; returns `{ success, issueId, issueKey, issueUrl, rawResponse }` |
