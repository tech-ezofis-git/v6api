"""
Local Jira create-issue gateway for Windows Server 2012 R2.

Uses Python OpenSSL + certifi so Atlassian HTTPS works when .NET/Schannel cannot.
The SaaS API calls http://127.0.0.1:5055/jira/create-issue (no outbound TLS from .NET).

Config: copy config.example.json -> config.json and fill Email / ApiToken.
"""

from __future__ import annotations

import base64
import json
import os
import ssl
import traceback
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

try:
    import certifi
except ImportError as exc:  # pragma: no cover
    raise SystemExit("Install certifi first: python -m pip install certifi") from exc

ROOT = Path(__file__).resolve().parent
CONFIG_PATH = Path(os.environ.get("JIRA_GATEWAY_CONFIG", ROOT / "config.json"))


def load_config() -> dict:
    if not CONFIG_PATH.is_file():
        raise FileNotFoundError(
            f"Missing {CONFIG_PATH}. Copy config.example.json to config.json and set credentials."
        )
    with CONFIG_PATH.open(encoding="utf-8") as f:
        cfg = json.load(f)
    required = ("BaseUrl", "Email", "ApiToken", "ProjectKey", "IssueType")
    missing = [k for k in required if not str(cfg.get(k, "")).strip()]
    if missing:
        raise ValueError(f"config.json missing required fields: {', '.join(missing)}")
    return cfg


def map_priority(priority: str | None) -> str | None:
    if not priority or not str(priority).strip():
        return None
    p = str(priority).strip()
    return {
        "Low": "Low",
        "Normal": "Medium",
        "High": "High",
        "Urgent": "Highest",
    }.get(p, p)


def build_description(body: dict) -> str:
    lines = [
        f"Support category: {body.get('supportCategory')}",
        f"Priority: {body.get('priority')}",
        f"Preferred contact: {body.get('preferredContact')}",
        f"Phone: {body.get('phoneNO')}",
        f"Caller email: {body.get('callerEmail')}",
        f"Email updates: {body.get('isEmailSend')}",
        "",
        "Description:",
        body.get("requestDescription") or "",
    ]
    return "\n".join(lines)


def build_adf_description(text: str) -> dict:
    paragraphs = text.replace("\r\n", "\n").split("\n")
    content = []
    for line in paragraphs:
        content.append(
            {
                "type": "paragraph",
                "content": [{"type": "text", "text": line}],
            }
        )
    if not content:
        content.append(
            {
                "type": "paragraph",
                "content": [{"type": "text", "text": ""}],
            }
        )
    return {"type": "doc", "version": 1, "content": content}


def create_jira_issue(cfg: dict, body: dict) -> dict:
    base_url = str(body.get("baseUrl") or cfg["BaseUrl"]).rstrip("/")
    project_key = str(body.get("projectKey") or cfg["ProjectKey"]).strip()
    issue_type = str(body.get("issueType") or cfg["IssueType"]).strip()
    email = str(cfg["Email"]).strip()
    token = str(cfg["ApiToken"]).strip()

    summary = (body.get("supportCategory") or "Support request").strip() or "Support request"
    if len(summary) > 255:
        summary = summary[:255]

    fields: dict = {
        "project": {"key": project_key},
        "summary": summary,
        "issuetype": {"name": issue_type},
        "description": build_adf_description(build_description(body)),
    }
    jira_priority = map_priority(body.get("priority"))
    if jira_priority:
        fields["priority"] = {"name": jira_priority}

    payload = json.dumps({"fields": fields}).encode("utf-8")
    auth = base64.b64encode(f"{email}:{token}".encode("utf-8")).decode("ascii")

    req = urllib.request.Request(
        f"{base_url}/rest/api/3/issue",
        data=payload,
        method="POST",
        headers={
            "Authorization": f"Basic {auth}",
            "Accept": "application/json",
            "Content-Type": "application/json",
        },
    )

    ctx = ssl.create_default_context(cafile=certifi.where())
    try:
        with urllib.request.urlopen(req, context=ctx, timeout=60) as resp:
            raw = resp.read().decode("utf-8", errors="replace")
            data = json.loads(raw) if raw else {}
            issue_key = data.get("key")
            return {
                "success": True,
                "issueId": data.get("id"),
                "issueKey": issue_key,
                "issueUrl": f"{base_url}/browse/{issue_key}" if issue_key else None,
                "rawResponse": raw,
            }
    except urllib.error.HTTPError as e:
        raw = e.read().decode("utf-8", errors="replace")
        return {
            "success": False,
            "issueId": None,
            "issueKey": None,
            "issueUrl": None,
            "rawResponse": raw or str(e),
            "httpStatus": e.code,
        }
    except Exception as e:
        return {
            "success": False,
            "issueId": None,
            "issueKey": None,
            "issueUrl": None,
            "rawResponse": str(e),
        }


class Handler(BaseHTTPRequestHandler):
    server_version = "JiraGateway/1.0"

    def log_message(self, fmt: str, *args) -> None:
        print(f"[{self.log_date_time_string()}] {self.address_string()} - {fmt % args}")

    def _send_json(self, status: int, obj: dict) -> None:
        data = json.dumps(obj).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self) -> None:  # noqa: N802
        if self.path.rstrip("/") in ("", "/health", "/jira/health"):
            self._send_json(200, {"status": "ok"})
            return
        self._send_json(404, {"success": False, "rawResponse": "Not found"})

    def do_POST(self) -> None:  # noqa: N802
        path = self.path.split("?", 1)[0].rstrip("/")
        if path != "/jira/create-issue":
            self._send_json(404, {"success": False, "rawResponse": "Not found"})
            return

        length = int(self.headers.get("Content-Length", "0") or 0)
        raw_body = self.rfile.read(length) if length > 0 else b"{}"
        try:
            body = json.loads(raw_body.decode("utf-8") or "{}")
            if not isinstance(body, dict):
                raise ValueError("JSON body must be an object")
        except Exception as e:
            self._send_json(400, {"success": False, "rawResponse": f"Invalid JSON: {e}"})
            return

        try:
            cfg = load_config()
            result = create_jira_issue(cfg, body)
            status = 200 if result.get("success") else 502
            self._send_json(status, result)
        except Exception as e:
            traceback.print_exc()
            self._send_json(
                500,
                {"success": False, "rawResponse": str(e)},
            )


def main() -> None:
    # Fail fast if config is missing
    cfg = load_config()
    host = str(cfg.get("ListenHost", "127.0.0.1"))
    port = int(cfg.get("ListenPort", 5055))
    print(f"Jira gateway listening on http://{host}:{port}")
    print(f"Config: {CONFIG_PATH}")
    print(f"Jira: {cfg['BaseUrl']} project={cfg['ProjectKey']} type={cfg['IssueType']}")
    print("POST /jira/create-issue   GET /health")
    httpd = ThreadingHTTPServer((host, port), Handler)
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        print("\nShutting down")
        httpd.shutdown()


if __name__ == "__main__":
    main()
