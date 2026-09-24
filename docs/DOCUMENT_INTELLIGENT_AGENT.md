# Document Intelligent Agent

Infers which **repository** a document belongs to from the tenant catalog.

Exposed only through the shared agents **`/chat`** endpoint with `intent=document_intelligent`.  
There is **no** `/document-intelligent` URL on agents.

| What | URL |
|---|---|
| Agents `/chat` | `POST https://cloud.ezofis.com/chat` (or `Agents:ChatUrl`) |
| V6 proxy (JSON) | `POST /api/repositories/document-intelligent-agent` |
| V6 proxy (multipart) | `POST /api/repositories/document-intelligent-agent/upload` |

## Agents request (direct)

```json
{
  "session_id": "demo-di",
  "intent": "document_intelligent",
  "payload": {
    "tenant_id": "{tenant-guid}",
    "ocr_text": "TAX INVOICE\nVendor: …"
  }
}
```

Or `filepath` + `pageno`, or multipart `file` + `intent=document_intelligent`.

## Locked response key: `document_intelligent_result`

```json
{
  "confidence_score": 88.0,
  "repository_id": "…",
  "repository_name": "Accounts Payable",
  "rationale": "…",
  "candidates": [{ "repository_id": "…", "repository_name": "…", "score": 88.0 }],
  "ocr_text": "…",
  "source_reference": "invoice.pdf"
}
```

The V6 proxy unwraps this object (same pattern as assistant search/chatbot).

## V6 behaviour

1. Resolves `tenant_id` from the access token / `X-Tenant-Id` (not from the frontend body or form).
2. By default loads the tenant repository catalog (`id` / `name` / fields) into `payload.repositories`.
3. POSTs to `Agents:ChatUrl` with `intent=document_intelligent`.
4. Returns only `document_intelligent_result`.

Requires the agents service to implement the `document_intelligent` intent.

**Note:** Agents shared `/chat` validation requires at least one of `message`, `ocr_text`, `filepath`, etc. A bare multipart file is not enough — V6 always sends a classify `message` plus the file as both `file` and `upload_file`.
