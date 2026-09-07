# Workflow PDF Generation

When a workflow step block has `generatePDF: true` in designer JSON, V6 generates a PDF **when leaving that step** (move-next), using the step's `pdfTemplate` (pdfme) and current form data from ezfb.

## Trigger

- User completes a step whose block has `settings.generatePDF: true`
- Move-next saves form data to ezfb first
- V6 loads `pdfTemplate` from workflow JSON blob
- Form data is mapped from **jsonId keys** ΓåÆ **template dataKey names**
- Python service returns PDF bytes
- PDF is archived as a **new attachment** (OCR invoice PDF is unchanged)

## Configuration

`appsettings.json`:

```json
"Workflow": {
  "PdfGeneration": {
    "Enabled": true,
    "ServiceUrl": "http://your-python-host/api/pdf/generate",
    "TimeoutSeconds": 120
  }
}
```

If `ServiceUrl` is empty, move-next succeeds and PDF generation is skipped (logged).

## Python API contract

**POST** `{ServiceUrl}`  
**Content-Type:** `application/json`

Configured production URL: `https://cloud.ezofis.com/api/pdf/generate`

### Request

```json
{
  "templateJson": {
    "schemas": [[{ "name": "...", "dataKey": "Customer / Principal", "type": "text" }]],
    "basePdf": { "width": 210, "height": 297 }
  },
  "formData": {
    "Customer / Principal": "ACME Shipping",
    "Vessel Name": "MV Pacific Star",
    "Amount (SGD) 1": "1500.00"
  },
  "fileName": "Track Customer Advance-REQ-2026-001.pdf",
  "metadata": {
    "tenantId": "...",
    "workflowId": "...",
    "instanceId": "...",
    "activityId": "HOab3qP1B60dLpFlvaJn2",
    "stepName": "Track Customer Advance",
    "referenceNumber": "REQ-2026-001"
  }
}
```

### Response (either)

1. `200` with `Content-Type: application/pdf` (raw bytes), or  
2. `200` JSON: `{ "status": "success", "filename": "...", "pdf_base64": "..." }`  
   (also accepts `pdfBase64` / `contentBase64`)

V6 decodes the base64 PDF, archives it as a **new** `WorkflowAttachments` row (does not replace OCR/email PDF), and optionally binds `itemId` into `generatePDFFields` FILE controls.

## Form data mapping (jsonId ΓåÆ dataKey)

Move-next `formData` uses **jsonId** keys. pdfme templates use **dataKey** labels (e.g. `"Customer / Principal"`).

V6 maps via `wFormControl`:

| ezfb key (jsonId) | wFormControl.name | Python formData key |
|-------------------|-------------------|---------------------|
| `3dcfba9e-...` | Customer / Principal | Customer / Principal |
| `ce4760f7-...` | Vessel Name | Vessel Name |

TABLE rows are flattened to numbered keys when present (e.g. `Service / Cost Item 1`).

## Attachments vs OCR

| Artifact | Source | Storage |
|----------|--------|---------|
| Email invoice PDF | Email ingest + OCR | Existing FILE control + `WorkflowAttachments` |
| Generated PDA/FDA | Step `generatePDF` | New `WorkflowAttachments` row + optional FILE bind |

Optional: set `generatePDFFields` on the block to jsonIds of FILE controls where the generated PDF `itemId` should be written.

## Move-next response

When a PDF is generated, the move-next result includes:

- `generatedPdfAttachmentId`
- `generatedPdfFileName`

## Vessel Call example

| Step | ActivityId | generatePDF |
|------|------------|-------------|
| Track Customer Advance | `HOab3qP1B60dLpFlvaJn2` | true (PDA template) |
| Settlement | `Msk7eVsZ0i8tV0p5puKJG` | true (FDA template) |

PDF is created when the user **submits** those steps (leaves the step), not when entering them.

## Key files

- [`WorkflowPdfGenerationService.cs`](../src/Modules/Workflow/Workflow.Infrastructure/Services/WorkflowPdfGenerationService.cs)
- [`WorkflowPdfFormDataMapper.cs`](../src/Modules/Workflow/Workflow.Infrastructure/Services/WorkflowPdfFormDataMapper.cs)
- [`MoveToNextStepCommandHandler.cs`](../src/Modules/Workflow/Workflow.Application/Workflows/Commands/MoveToNextStep/MoveToNextStepCommandHandler.cs)
