# Repository Sign Request — Complete Flow (ezofis)

**Audience:** Backend + Frontend  
**Status:** Design / implementation guide  
**Related:** File share invite (`docs/REPOSITORY_FILE_SHARE_FRONTEND_GUIDE.md`)

---

## 1. Goal (what you asked for)

1. Initiator selects a repository file (`repositoryId` + `itemId`).
2. Chooses signers (emails) and mode:
   - **Parallel** — all signers get mail at once; each signs the same current file.
   - **Sequential** — signer 1 gets mail → signs → **then** signer 2 gets mail with the latest signed file → …  
3. Each signer gets an **ezofis** email (DocuSign-style “Do you recognize this document?”).
4. Signer opens link → views file → **signs anywhere** on the document.
5. Signed PDF is saved **in the same folder, same name** (replace / overwrite the repository item file).
6. Initiator gets email updates (each signer done + final complete).

---

## 2. What we can reuse (already in V6)

| Need | Reuse |
|------|--------|
| Invite by email + guest TenantUser | Repository file share (`ShareGuestUserProvisioningService`, set-password / social-login) |
| Invite URL + SMTP email | `RepositoryItemShareService` / catalog `MailSettings` |
| Open file securely | Share-token / JWT + `OpenItemFileAsync` |
| Save file to blob | `IRepositoryFileStorage.SaveAsync(..., overwrite: true)` |
| Permission flag (future gate) | Folder security `sendForSignature` |
| Parallel vs sequential mental model | Workflow approval policy (`AnyOne` vs ordered steps) — **logic idea only**, not the same tables |

**Not reused (must build new):**

- Sign-request / signer tables  
- Sign-anywhere capture + PDF stamp/flatten  
- Replace-existing-item file API  
- Parallel / sequential routing + initiator notification emails  

---

## 3. High-level flow

```text
┌─────────────┐     POST create sign request      ┌──────────────────┐
│  Initiator  │ ─────────────────────────────────►│  V6 API          │
│  (Admin /   │   repoId, itemId, mode, signers   │  SignRequest     │
│   allowed)  │◄─────────────────────────────────│                  │
└─────────────┘     requestId, status             └────────┬─────────┘
                                                           │
                     ┌─────────────────────────────────────┤
                     │ Email (ezofis brand)                 │
                     ▼                                     ▼
              ┌────────────┐                        ┌────────────┐
              │ Signer A   │                        │ Signer B   │
              │ (parallel: │                        │ (parallel: │
              │  both now; │                        │  both now; │
              │  sequential│                        │  wait)     │
              │  A first)  │                        │            │
              └─────┬──────┘                        └─────┬──────┘
                    │ Open link → preview → login           │
                    │ View PDF → sign anywhere              │
                    │ POST submit signature                 │
                    ▼                                       ▼
              API stamps PDF → replaces repo file (same path/name)
                    │
                    ├─ Parallel: wait until all required signers done
                    ├─ Sequential: email next signer with latest file
                    └─ Always: email initiator (progress + completed)
```

---

## 4. Modes

### 4.1 Parallel (`signingMode: "parallel"`)

| Step | What happens |
|------|----------------|
| 1 | Create request |
| 2 | **All** signers get “Please sign” email at once |
| 3 | Each opens link, signs the **current** file |
| 4 | Each submit stamps onto the **latest** file (or merge order by `signedAt`) — see note below |
| 5 | When **all** signers have status `Signed` → request `Completed` |
| 6 | Initiator gets “completed” email |

**PDF note (parallel):** Safest approach for v1:

- Each signer signs against the **original** (or latest completed version).
- Server applies signatures in **signer order** (or completion order) onto one PDF chain:  
  `original → +sigA → +sigB` (serialize applies so two people don’t overwrite each other).

### 4.2 Sequential (`signingMode: "sequential"`)

| Step | What happens |
|------|----------------|
| 1 | Create request with ordered signers (`order: 1, 2, 3…`) |
| 2 | Email **only order 1** |
| 3 | Signer 1 signs → file replaced with signed PDF |
| 4 | Email **order 2** (and initiator “Signer 1 completed”) |
| 5 | Signer 2 signs the **already signed** file → replace again |
| 6 | … until last signer |
| 7 | Request `Completed` → initiator “all signed” email |

---

## 5. Email (ezofis — same idea as DocuSign screenshot)

**Subject example:** `Please review and sign: INV-2026-6001_v20.pdf`

**Body fields:**

- Heading: **Do you recognize this document?**
- Text: We received a request to send you a document for review/signature.
- Details:
  - **Sender:** {initiator display name}
  - **Company / tenant:** {tenant name}
  - **Document name:** {fileName}
  - **Sender email:** {initiator email}
  - **Your role:** Signer {n} of {total} ({Parallel|Sequential})
- Buttons / links:
  - **Continue** → `{FrontendBaseUrl}/sign-request/{token}?email=...`
  - Optional: **Report** → mailto / support link (later)

Reuse SMTP from catalog `MailSettings` (same as file share).

**Also notify initiator when:**

| Event | Email to initiator |
|-------|--------------------|
| Request created | Confirmation + signer list |
| Each signer completed | “{name} signed {file}” |
| Sequential: next signer notified | Optional CC |
| All complete | “Signing complete — file updated in repository” |
| Declined / expired | Alert |

---

## 6. Proposed APIs

Base: `/api/repositories/{repositoryId}/items/{itemId}/sign-requests`

### 6.1 Create sign request

```http
POST /api/repositories/{repositoryId}/items/{itemId}/sign-requests
Authorization: Bearer <jwt>
X-Tenant-Id: <tenant>
Content-Type: application/json
```

```json
{
  "signingMode": "sequential",
  "message": "Please sign this invoice",
  "expiresInDays": 14,
  "signers": [
    { "email": "a@company.com", "name": "Alice", "order": 1 },
    { "email": "b@company.com", "name": "Bob", "order": 2 }
  ]
}
```

| `signingMode` | Meaning |
|---------------|---------|
| `parallel` | All notified now |
| `sequential` | Notify by `order` one after another |

**Response `201`:**

```json
{
  "signRequestId": "guid",
  "repositoryId": "guid",
  "itemId": "guid",
  "fileName": "INV-2026-6001_v20.pdf",
  "signingMode": "sequential",
  "status": "InProgress",
  "signers": [
    {
      "signerId": "guid",
      "email": "a@company.com",
      "order": 1,
      "status": "Pending",
      "inviteUrl": "https://demoapp.ezofis.com/sign-request/TOKEN_A?email=a%40company.com"
    },
    {
      "signerId": "guid",
      "email": "b@company.com",
      "order": 2,
      "status": "Waiting",
      "inviteUrl": null
    }
  ]
}
```

### 6.2 Get sign request (initiator)

```http
GET /api/sign-requests/{signRequestId}
```

### 6.3 Signer preview (anonymous / before login)

```http
GET /api/sign-requests/invite/{inviteToken}/preview
```

Returns: sender, company, fileName, senderEmail, signingMode, permission to continue, auth methods (reuse share guest auth).

### 6.4 Open document for signing

```http
GET /api/sign-requests/invite/{inviteToken}/file
Authorization: Bearer <jwt>   // after set-password / login
```

Streams current PDF (latest signed version for sequential).

### 6.5 Submit signature (“sign anywhere”)

```http
POST /api/sign-requests/invite/{inviteToken}/sign
Authorization: Bearer <jwt>
Content-Type: application/json
```

```json
{
  "pageNumber": 1,
  "x": 120.5,
  "y": 440.2,
  "width": 160,
  "height": 48,
  "signatureImageBase64": "data:image/png;base64,...",
  "signedAtClientUtc": "2026-07-29T12:00:00Z"
}
```

**Server:**

1. Validate token + signer still `Pending`  
2. Load current item file  
3. Stamp signature image onto PDF at coordinates  
4. **Overwrite** blob + update item metadata (same logical file / same name)  
5. Mark signer `Signed`  
6. Parallel → if all done → `Completed` + initiator mail  
7. Sequential → activate next signer email + initiator mail  

### 6.6 Decline (optional)

```http
POST /api/sign-requests/invite/{inviteToken}/decline
{ "reason": "Not my document" }
```

### 6.7 Cancel (initiator)

```http
POST /api/sign-requests/{signRequestId}/cancel
```

---

## 7. Data model (new tables — catalog or tenant)

Prefer **tenant DB** (`repository` schema) so it stays with the file:

### `repository.SignRequests`

| Column | Notes |
|--------|--------|
| Id | PK |
| TenantId | |
| RepositoryId | |
| ItemId | |
| FileName | snapshot |
| SigningMode | `parallel` / `sequential` |
| Status | `Draft`, `InProgress`, `Completed`, `Cancelled`, `Expired` |
| Message | optional |
| InitiatedByUserId | |
| InitiatedByEmail | |
| InitiatedByName | |
| ExpiresAtUtc | |
| CompletedAtUtc | |
| CreatedAtUtc | |

### `repository.SignRequestSigners`

| Column | Notes |
|--------|--------|
| Id | PK |
| SignRequestId | FK |
| Email | |
| Name | |
| UserId | set after guest provision / login |
| SortOrder | 1..n |
| Status | `Waiting`, `Pending`, `Signed`, `Declined`, `Skipped` |
| InviteToken | unique |
| InvitedAtUtc | |
| SignedAtUtc | |
| SignatureMetaJson | page, x, y, w, h |
| SignatureBlobPath | optional stored PNG |

---

## 8. Frontend steps

### 8.1 Initiator UI

1. Open repository file.  
2. **Send for signature**.  
3. Add signer emails + choose **Parallel** or **Sequential** (drag order if sequential).  
4. Submit → show status tracker (Pending / Signed per person).  

### 8.2 Signer UI (from email Continue)

1. Landing: “Do you recognize this document?” (sender, company, file, email).  
2. **Continue** → same auth as file share (preview → set-password / Google / Microsoft / login).  
3. PDF viewer + **sign anywhere** (place signature image / draw pad).  
4. Confirm → `POST .../sign`.  
5. Success page: “Thank you — document signed.”  

Reuse sign-in from file share; only the deep link path changes (`/sign-request/...` vs `/sign-in?shareToken=`).

---

## 9. File replace rule (same folder, same name)

On each successful sign:

1. Read current item `FilePath` / storage provider.  
2. Write stamped PDF with **`overwrite: true`** to same path (or same archive folder + same file name).  
3. Update item: `FileSize`, `ModifiedAtUtc`, `ModifiedBy`, bump `FileVersion` if you want history metadata **without** changing display name.  
4. Timeline entry: “Signed by {email}”.

Result: repository list still shows **same file name**; content is the signed PDF.

---

## 10. Status machine

```text
SignRequest:
  InProgress ──► Completed
       │
       ├──► Cancelled (initiator)
       └──► Expired

Signer:
  Waiting ──► Pending (email sent) ──► Signed
                   │
                   └──► Declined
```

- **Parallel:** all signers start `Pending`.  
- **Sequential:** first `Pending`, others `Waiting` until previous `Signed`.

---

## 11. Suggested build order

| Phase | Deliverable |
|-------|-------------|
| **P1** | Tables + create request + emails (Continue link) + guest auth reuse |
| **P2** | Preview + file download for signer |
| **P3** | Submit signature + PDF stamp + replace item file |
| **P4** | Parallel completion + sequential next-mail + initiator notifications |
| **P5** | Decline / cancel / expire job + FE tracker |
| **P6** | Enforce `sendForSignature` ACL on create |

---

## 12. Out of scope for v1 (optional later)

- Legal certificate / certified PDF  
- Multiple signature fields predefined by initiator  
- DocuSign / Adobe Sign external providers  
- Report Abuse workflow (link only in email)  
- Non-PDF formats (convert to PDF first)  

---

## 13. Summary for the team

| Question | Answer |
|----------|--------|
| New API? | Yes — **Sign Request** (not the same as file share) |
| Reuse share? | Yes — guest user, login, SMTP, open-file patterns |
| Parallel vs sequential? | `signingMode` on create |
| Where is signed file? | **Same repository item** — overwrite same name/path |
| Initiator emails? | On create, each sign, and complete |
| Sign anywhere? | FE places coords + image; API stamps PDF |

---

**Next step:** Say if you want this **implemented in V6 now** (P1–P4), and confirm:

1. PDF only for v1?  
2. Parallel stamp strategy: serialize on server (recommended)?  
3. Deep link base path (e.g. `https://demoapp.ezofis.com/sign-request/{token}`)?  
