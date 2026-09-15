# HANA Cloud purchase-order API

Read purchase orders from the HANA Cloud table saved on a connector, then link an invoice to a PO. One PO can match many workflow instances. Those links are stored in a separate table, `DBADMIN.PO_INVOICE_MATCH`. `PURCHASE_ORDERS` is not updated.

## Common

| Item | Value |
|------|--------|
| Auth | `Authorization: Bearer <token>` |
| Tenant | `X-Tenant-Id: b843b988-00ec-44e3-aca2-b8470133ef63` |
| Connector | `f7636e21-1a0c-457c-a2b4-e28430705477` |
| Base | `/api/connector/{connectorId}/hana/purchase-orders` |

The connector row must already have `dbo.connector.HanaDatabaseJson` (host, port, user, password, schema, table). That JSON is not returned by these APIs.

`PO_INVOICE_MATCH` is created on the first call if it does not exist. Existing tables get missing columns via `ALTER TABLE` on the next match or purchase-order call.

| Column | Meaning |
|--------|---------|
| `PO_NUMBER` | Purchase order number |
| `INSTANCE_ID` | Workflow instance id. Together with `PO_NUMBER` this identifies one match |
| `INVOICE_NUMBER` | Invoice linked to that instance |
| `SUPPLIER_NAME` | Supplier name from the invoice |
| `INVOICE_DATE` | Invoice date (`yyyy-MM-dd`) |
| `CURRENCY` | Invoice currency |
| `TOTAL_AMOUNT` | Invoice total |
| `MATCH_STATUS` | Match status, for example `Matched` or `Approved` |
| `INVOICE_STATUS` | Invoice status |
| `ITEMS` | Invoice line items as a JSON array |
| `CREATED_AT` / `UPDATED_AT` | Row timestamps |

---

## 1. Get purchase orders

Returns every row in `PURCHASE_ORDERS`, including the `matches` list for each PO.

```http
GET /api/connector/f7636e21-1a0c-457c-a2b4-e28430705477/hana/purchase-orders
Authorization: Bearer <token>
X-Tenant-Id: b843b988-00ec-44e3-aca2-b8470133ef63
```

One PO:

```http
GET /api/connector/f7636e21-1a0c-457c-a2b4-e28430705477/hana/purchase-orders?poNumber=4500069456
```

### Response `200`

```json
{
  "found": true,
  "connectorId": "f7636e21-1a0c-457c-a2b4-e28430705477",
  "poNumber": "4500069456",
  "count": 1,
  "items": [
    {
      "poNumber": "4500069456",
      "poType": "Standard PO (NB)",
      "supplierId": "USSU-VSF01",
      "supplierName": "EV Parts Inc.",
      "companyCode": "1710",
      "purchasingOrg": "1710",
      "purchasingGroup": "002",
      "currency": "USD",
      "poDate": "2026-09-11",
      "approvalStatus": "Approved automatically",
      "createdBy": "Trial 0047470 User (CB9980044260)",
      "total": 368.94,
      "items": [
        {
          "itemNumber": 10,
          "itemCategory": "Standard",
          "materialId": "MZ-RM-R100-02",
          "materialDescription": "BKR-100 Handle Bars",
          "materialGroup": "ZHANDLE",
          "plant": "1710",
          "orderQuantity": 129,
          "unitOfMeasure": "PC",
          "netPrice": 2.86,
          "priceUnit": 1,
          "netValue": 368.94
        }
      ],
      "matches": [
        {
          "instanceId": "11111111-1111-1111-1111-111111111111",
          "invoiceNumber": "INV-1001",
          "supplierName": "EV Parts Inc.",
          "invoiceDate": "2026-09-11",
          "currency": "USD",
          "totalAmount": 368.94,
          "status": "Matched",
          "invoiceStatus": "Open",
          "items": [
            {
              "itemNumber": 10,
              "itemCategory": "Standard",
              "materialId": "MZ-RM-R100-02",
              "materialDescription": "BKR-100 Handle Bars",
              "materialGroup": "ZHANDLE",
              "plant": "1710",
              "orderQuantity": 129,
              "unitOfMeasure": "PC",
              "netPrice": 2.86,
              "priceUnit": 1,
              "netValue": 368.94
            }
          ]
        }
      ]
    }
  ]
}
```

`matches` is empty until an invoice is linked. `poNumber` in the wrapper is the filter you sent, or `null` when you asked for all rows. `found` is `false` and `count` is `0` when that PO number is not in HANA.

---

## 2. Post purchase orders

Same result as GET. `poNumber` in the body is optional. Omit it to return all rows.

```http
POST /api/connector/f7636e21-1a0c-457c-a2b4-e28430705477/hana/purchase-orders
Authorization: Bearer <token>
X-Tenant-Id: b843b988-00ec-44e3-aca2-b8470133ef63
Content-Type: application/json

{
  "poNumber": "4500069456"
}
```

All rows:

```json
{}
```

Response shape is the same as GET.

---

## 3. Save or update an invoice match

```http
POST /api/connector/f7636e21-1a0c-457c-a2b4-e28430705477/hana/purchase-orders/match
Authorization: Bearer <token>
X-Tenant-Id: b843b988-00ec-44e3-aca2-b8470133ef63
Content-Type: application/json
```

`poNumber` and `instanceId` are required. The same PO with a different `instanceId` inserts another row. The same `poNumber` + `instanceId` updates that row only.

### Create the link

Call this after a PO match. If `status` is omitted, it is saved as `Matched`.

```json
{
  "instanceId": "11111111-1111-1111-1111-111111111111",
  "poNumber": "4500069456",
  "invoiceNumber": "INV-1001",
  "supplierName": "EV Parts Inc.",
  "invoiceDate": "2026-09-11",
  "currency": "USD",
  "totalAmount": 368.94,
  "status": "Matched",
  "invoiceStatus": "Open",
  "items": [
    {
      "itemNumber": 10,
      "itemCategory": "Standard",
      "materialId": "MZ-RM-R100-02",
      "materialDescription": "BKR-100 Handle Bars",
      "materialGroup": "ZHANDLE",
      "plant": "1710",
      "orderQuantity": 129,
      "unitOfMeasure": "PC",
      "netPrice": 2.86,
      "priceUnit": 1,
      "netValue": 368.94
    }
  ]
}
```

A second instance for the same PO:

```json
{
  "instanceId": "22222222-2222-2222-2222-222222222222",
  "poNumber": "4500069456",
  "invoiceNumber": "INV-1002",
  "supplierName": "EV Parts Inc.",
  "invoiceDate": "2026-09-12",
  "currency": "USD",
  "totalAmount": 100.00,
  "status": "Matched"
}
```

### Update status only

Do not send fields that should stay as they are.

```json
{
  "instanceId": "11111111-1111-1111-1111-111111111111",
  "poNumber": "4500069456",
  "status": "Approved",
  "invoiceStatus": "Posted"
}
```

You can also send a new `invoiceNumber`, totals, or `items` with the status to change those fields.

### Response `200`

```json
{
  "updated": true,
  "created": true,
  "connectorId": "f7636e21-1a0c-457c-a2b4-e28430705477",
  "poNumber": "4500069456",
  "instanceId": "11111111-1111-1111-1111-111111111111",
  "invoiceNumber": "INV-1001",
  "supplierName": "EV Parts Inc.",
  "invoiceDate": "2026-09-11",
  "currency": "USD",
  "totalAmount": 368.94,
  "status": "Matched",
  "invoiceStatus": "Open",
  "items": [
    {
      "itemNumber": 10,
      "itemCategory": "Standard",
      "materialId": "MZ-RM-R100-02",
      "materialDescription": "BKR-100 Handle Bars",
      "materialGroup": "ZHANDLE",
      "plant": "1710",
      "orderQuantity": 129,
      "unitOfMeasure": "PC",
      "netPrice": 2.86,
      "priceUnit": 1,
      "netValue": 368.94
    }
  ]
}
```

| Field | Meaning |
|-------|---------|
| `created` | `true` when a new match row was inserted. `false` when an existing instance was updated |
| `updated` | `true` when the write succeeded |
| `status` | Value stored in `MATCH_STATUS` |
| `items` | Invoice lines stored in `ITEMS` as JSON |

`status` and `invoiceStatus` are free strings, up to 64 characters. `instanceId` is up to 128. `invoiceNumber` is up to 64.

### Auto mark Paid on workflow move

When Accounts Payable moves into a step whose name contains `Paid`, the API:

1. Reads `Blocks[].Settings.apAgent.connectorId` from the workflow JSON
2. Uses that connector’s `HanaDatabaseJson` to update `PO_INVOICE_MATCH` by `INSTANCE_ID`

- Row found → set `MATCH_STATUS` and `INVOICE_STATUS` to `Paid`
- No connector id / no row → leave HANA unchanged (non-HANA invoices)

No extra client call is required for that case.

---

## Errors

| Status | When |
|--------|------|
| `400` | Missing `poNumber` or `instanceId` on match. New match without `invoiceNumber`. HANA connection or query failed |
| `404` | Connector id not found, PO number not in `PURCHASE_ORDERS`, or status update for an instance that was never matched |

```json
{ "error": "Purchase order not found." }
```

```json
{ "error": "instanceId is required. One PO can match many instances." }
```
