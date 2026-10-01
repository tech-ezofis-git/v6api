# Document preview — frontend guide

**Audience:** Frontend  
**Endpoint:** `POST /api/workflows/document/preview`  
**Auth:** JWT and tenant header, same as other workflow calls.

This call generates a PDF for preview only. It does not archive the file, does not create an attachment, and does not move the ticket.

---

## 1. Headers

| Header | Required |
|--------|----------|
| `Authorization: Bearer <jwt>` | Yes |
| `X-Tenant-Id: <tenant-guid>` | Yes |
| `Content-Type: application/json` | Yes |

---

## 2. Request

```http
POST /api/workflows/document/preview
```

| Field | Required | What to send |
|-------|----------|----------------|
| `formData` | Yes | Current form values keyed by **jsonId**. Tables are arrays; each cell is also keyed by the column jsonId. |
| `templateJson` | Yes | The document node `templateJson` (or `pdfTemplate`) from the workflow designer. Send the object as-is. |
| `formId` | No | Form id. Send it when you have it. If omitted, the API looks up the form from the first jsonId in `formData`. |

`formData` and `templateJson` may be JSON objects. A JSON string is also accepted.

```json
{
  "formId": "787372d8-d66a-47f6-ba3f-36cb6dffb360",
  "formData": {
    "a1b2c3d4-order-number": "EST-900001",
    "b2c3d4e5-billing-address": "123 King St",
    "c3d4e5f6-line-item": [
      {
        "d4e5f6a7-product": "Widget",
        "e5f6a7b8-qty": "2",
        "f6a7b8c9-price": "10.00"
      }
    ]
  },
  "templateJson": {
    "id": "inflow_customer_estimate",
    "schemas": []
  }
}
```

Use the real jsonIds from the form and the real template from the document node. The ids above are only to show the shape.

---

## 3. What the API does before Python

The API converts jsonId keys to form field names, then posts that named `formData` plus `templateJson` to the Python chat function (`intent` `ftl_quote_estimator`).

| You send | Python receives |
|----------|-----------------|
| Order Number jsonId | `Order Number` |
| Billing Address jsonId | `Billing Address` |
| Line Item jsonId | `Line Item` |
| Product / Qty / Price jsonIds inside the table | `Product`, `Qty`, `Price` |

Table column titles come from the form field name. The template `dataKey` values must be those same names. The API does not rename keys to template ids such as `estimate_number` or `line_items`.

---

## 4. Response `200`

```json
{
  "fileName": "quote.pdf",
  "pdfBase64": "JVBERi0xLjQK...",
  "formData": {
    "Order Number": "EST-900001",
    "Billing Address": "123 King St",
    "Line Item": [
      {
        "Product": "Widget",
        "Qty": "2",
        "Price": "10.00"
      }
    ]
  }
}
```

| Field | Use |
|-------|-----|
| `pdfBase64` | Decode and show the PDF. |
| `fileName` | Download name. |
| `formData` | The named payload that was sent to Python. Use it to confirm the keys match the template. |

Show the PDF in the browser:

```javascript
const bytes = Uint8Array.from(atob(data.pdfBase64), (c) => c.charCodeAt(0));
const blob = new Blob([bytes], { type: "application/pdf" });
const url = URL.createObjectURL(blob);
// <iframe src={url}> or window.open(url)
```

---

## 5. Errors `400`

```json
{ "error": "formData is required." }
```

| `error` | Cause |
|---------|--------|
| `JSON body is required.` | Body is not a JSON object. |
| `formData is required.` | `formData` is missing or empty. |
| `templateJson is required.` | `templateJson` is missing. |
| `templateJson is not valid JSON: ...` | `templateJson` string does not parse. |
| `Agents:ChatUrl is not configured.` | API chat URL is not set. |
| `FTL document response did not include pdf_base64.` | Python did not return a PDF. |
| `FTL document pdf_base64 is not valid base64.` | Python PDF payload could not be decoded. |

A missing field name in the PDF means that value was empty in `formData`, or the template `dataKey` does not match the field name in the response `formData`.
