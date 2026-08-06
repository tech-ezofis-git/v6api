# AP Command Center Dashboard — Frontend Integration Guide

Share this with the frontend team.

**Endpoint:** `POST /api/reports/ap-dashboard`  
**Also:** `GET /api/reports/ap-dashboard` (same filters as query params)

All calls need **JWT** and **`X-Tenant-Id`**.

---

## 1. Base setup

| Item | Value |
|------|--------|
| **Local (IIS Express)** | `http://localhost:52095` or `https://localhost:44311` |
| **IIS (PathBase)** | `http://localhost/V6API` |
| **Auth** | `Authorization: Bearer {accessToken}` |
| **Tenant** | `X-Tenant-Id: {tenant-guid}` on every request |

### Common headers

```http
Authorization: Bearer {jwt_token}
X-Tenant-Id: {tenant_guid}
Content-Type: application/json
```

> If the API uses PathBase `/V6API`, the full path is:  
> `POST {base}/V6API/api/reports/ap-dashboard`

---

## 2. Endpoints

### POST (recommended)

```http
POST /api/reports/ap-dashboard
```

### GET (same filters via query string)

```http
GET /api/reports/ap-dashboard?period=thisMonth&department=MRO&status=approved&currency=USD&includeInvoiceDetails=true
```

---

## 3. Request body

### Minimal

```json
{
  "period": "thisMonth"
}
```

### Full (matches UI filter bar)

```json
{
  "workflowId": null,
  "period": "thisMonth",
  "fromUtc": null,
  "toUtc": null,
  "department": "MRO",
  "supplier": null,
  "status": "approved",
  "currency": "USD",
  "requestStatus": null,
  "poAmountTier": null,
  "includeInvoiceDetails": true
}
```

### Custom date range

```json
{
  "period": "custom",
  "fromUtc": "2026-05-01T00:00:00Z",
  "toUtc": "2026-07-31T23:59:59Z",
  "includeInvoiceDetails": true
}
```

### Reset filters

Omit filter fields, or send `"all"`:

```json
{
  "period": "thisMonth",
  "department": "all",
  "status": "all",
  "currency": "all",
  "requestStatus": "all",
  "poAmountTier": "all"
}
```

---

## 4. Request fields

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `workflowId` | guid \| null | null | Limit to one workflow |
| `period` | string | `"thisMonth"` | Time range (see below) |
| `fromUtc` | datetime \| null | null | Required when `period = "custom"` |
| `toUtc` | datetime \| null | null | Required when `period = "custom"` |
| `department` | string \| null | null | Spend category (e.g. `MRO`, `IT Services`) |
| `supplier` | string \| null | null | Partial supplier name search |
| `status` | string \| null | null | Approval status filter |
| `currency` | string \| null | null | `USD`, `EUR`, `INR`, `GBP`, or `all` |
| `requestStatus` | string \| null | null | Workflow request status |
| `poAmountTier` | string \| null | null | Amount bucket filter |
| `includeInvoiceDetails` | bool | false | If `true`, returns `invoices[]` for drill-down |

### `period` values

| String | Meaning |
|--------|---------|
| `today` | Calendar today (UTC) — invoices **due** today |
| `tomorrow` | Calendar tomorrow (UTC) — invoices **due** tomorrow |
| `thisWeek` | Current week Mon–Sun (UTC) — invoices **due** this week |
| `thisMonth` | Current calendar month (UTC) |
| `lastMonth` | Previous calendar month |
| `thisQuarter` | Current quarter to date |
| `thisYear` | Current year to date |
| `custom` | Use `fromUtc` + `toUtc` |

```json
{ "period": "today" }
```

```json
{ "period": "tomorrow", "includeInvoiceDetails": true }
```

```json
{ "period": "thisWeek" }
```

> **Note:** For `today` / `tomorrow` / `thisWeek`, the API filters mainly by **due date** (AP cash calendar).  
> For month/quarter/year, it filters by agent **created** date first.

### `status` (Approval Status dropdown)

| Value | UI label |
|-------|----------|
| `all` | All Statuses |
| `approved` | Approved |
| `partially_approved` | Partially Approved |
| `rejected` | Rejected |
| `paid` | Paid |
| `processing` | Processing |
| `hold` | Hold |
| `overdue` | Overdue |
| `due_today` | Due Today |
| `pending` | Pending |

### `requestStatus` (More filters → Request Status)

| Value | UI label |
|-------|----------|
| `all` | All Request Statuses |
| `pending` | Pending |
| `processing` | Processing |
| `completed` | Completed |
| `hold` | On Hold |
| `rejected` | Rejected |

### `poAmountTier` (More filters → PO Amount)

| Value | UI label |
|-------|----------|
| `all` | All Amounts |
| `high_value` | High Value (> $100K) |
| `low_value` | Low Value (< $1K) |

---

## 5. UI filter → API mapping

| UI control | Request field | Example |
|------------|---------------|---------|
| This Month | `period` | `"thisMonth"` |
| MRO / IT Services / … | `department` | `"MRO"` |
| Supplier search | `supplier` | `"Northern"` |
| Approved / Paid / … | `status` | `"approved"` |
| USD / EUR / … | `currency` | `"USD"` |
| More → Request Status | `requestStatus` | `"processing"` |
| More → PO Amount | `poAmountTier` | `"high_value"` |
| Reset | omit fields or `"all"` | — |

Use **`filterOptions`** from the response to populate dropdowns.  
Use **`activeFilters`** to show selected chips.

---

## 6. Response overview

```json
{
  "period": "thisMonth",
  "periodLabel": "July 2026",
  "rangeStartUtc": "2026-07-01T00:00:00Z",
  "rangeEndUtc": "2026-07-31T23:59:59.9999999Z",
  "header": { },
  "kpis": [ ],
  "supplierRiskRadar": { },
  "profitVsApSpending": { },
  "monthlyPaymentTrend": { },
  "cashFlowForecast": { },
  "topSuppliersByInvoice": [ ],
  "outstandingBySupplier": [ ],
  "departmentSpend": [ ],
  "supplierGeography": [ ],
  "filterOptions": { },
  "activeFilters": { },
  "invoices": [ ]
}
```

`invoices` is **`null`** unless `includeInvoiceDetails: true`.

---

## 7. AP Command Center header → `header`

Maps to the top summary strip:

| UI | Field | Example |
|----|-------|---------|
| TOTAL AP | `totalAp` / `totalApDisplay` | `87579.52` / `"$87.6K"` |
| OVERDUE | `overdue` / `overdueDisplay` | `73974.32` / `"$74.0K"` |
| OPEN INVOICES | `openInvoices` | `23` |
| DPO | `dpoDays` / `dpoDisplay` | `20` / `"20d"` |
| Subtitle | `contextLabel` | `"Real-time · month · all suppliers"` |

```json
{
  "header": {
    "totalAp": 87579.52,
    "totalApDisplay": "$87.6K",
    "overdue": 73974.32,
    "overdueDisplay": "$74.0K",
    "openInvoices": 23,
    "dpoDays": 20,
    "dpoDisplay": "20d",
    "contextLabel": "Real-time · month · all suppliers"
  }
}
```

---

## 8. KPI cards → `kpis[]`

| `key` | Label | UI card |
|-------|-------|---------|
| `total_outstanding` | Total Outstanding | Outstanding |
| `total_paid` | Total Paid | Total Paid |
| `pending_payments` | Pending Payments | Pending |
| `due_today` | Due Today | Due Today |
| `overdue_amount` | Overdue | Overdue |
| `avg_processing_time` | Avg. Processing Time | Avg processing |

```json
{
  "key": "total_paid",
  "label": "Total Paid",
  "displayValue": "$8.1K",
  "value": 8146.08,
  "changePercent": 36,
  "trend": "up",
  "changeDirection": "up",
  "comparisonChangeLabel": "36% up",
  "comparisonPeriodLabel": "vs last month",
  "comparisonLabel": "36% up vs last month",
  "previousValue": 5989.76
}
```

| Field | Notes |
|-------|--------|
| `displayValue` | Ready-to-show string (`"$3.0K"`, `"2.5 d"`) |
| `value` | Raw number for charts / math |
| `changePercent` | Signed MoM % (`0` when both empty; `100` when previous was 0 and current > 0; negative when down) |
| `trend` | `"up"` \| `"down"` \| `"flat"` — **use for arrow/color** (inverted for “bad” KPIs like overdue) |
| `changeDirection` | Raw MoM direction `"up"` \| `"down"` \| `"flat"` (not inverted) |
| `comparisonChangeLabel` | Change text only: `"36% up"`, `"48% down"`, `"Flat"` |
| `comparisonPeriodLabel` | Period text only: `"vs last month"`, `"vs last week"`, … |
| `comparisonLabel` | Full footer: `"36% up vs last month"` (or compose `comparisonChangeLabel` + `comparisonPeriodLabel`) |
| `previousValue` | Previous-period raw value (tooltip / debug) |

---

## 9. Supplier Risk Radar → `supplierRiskRadar`

Donut: Low / Medium / High.

```json
{
  "supplierRiskRadar": {
    "title": "Supplier Risk Radar",
    "subtitle": "Which vendors carry the most risk exposure?",
    "totalSuppliers": 12,
    "totalExposure": 87579.52,
    "totalExposureDisplay": "$87.6K",
    "segments": [
      {
        "key": "low",
        "label": "Low",
        "supplierCount": 8,
        "amount": 12000,
        "amountDisplay": "$12.0K",
        "percent": 67
      },
      {
        "key": "medium",
        "label": "Medium",
        "supplierCount": 3,
        "amount": 25000,
        "amountDisplay": "$25.0K",
        "percent": 25
      },
      {
        "key": "high",
        "label": "High",
        "supplierCount": 1,
        "amount": 50579.52,
        "amountDisplay": "$50.6K",
        "percent": 8
      }
    ],
    "topRiskSuppliers": [
      {
        "supplier": "CHAMPION INDUSTRIAL SUPPLY",
        "riskLevel": "high",
        "outstandingAmount": 67647.45,
        "outstandingDisplay": "$67.6K",
        "openInvoices": 5,
        "overdueInvoices": 3,
        "countryCode": "CA",
        "currency": "CAD"
      }
    ]
  }
}
```

**Frontend tip:** use `segments[].percent` + `supplierCount` for the donut; use `topRiskSuppliers` for a side list / tooltip.

Risk levels: `"low"` | `"medium"` | `"high"`.

---

## 10. Charts

### Profit vs AP spending → `profitVsApSpending`

Dual axis:

- `primary` = AP amount (`primaryUnit: "currency"`)
- `secondary` = match-rate % (`secondaryUnit: "percent"`)

```json
{
  "title": "Profit vs AP spending",
  "subtitle": "Dual axis: AP amount and match-rate % (proxy for profit efficiency)",
  "points": [
    { "label": "Jul 2026", "primary": 87579.52, "secondary": 42.3, "primaryUnit": "currency", "secondaryUnit": "percent" }
  ]
}
```

### Monthly payment trend → `monthlyPaymentTrend`

```json
{
  "title": "Monthly payment trend",
  "subtitle": "Cash leaving the building, month by month",
  "points": [
    { "label": "Jul 2026", "primary": 12000, "secondary": null, "primaryUnit": "currency", "secondaryUnit": null }
  ]
}
```

### Cash flow forecast → `cashFlowForecast`

Next 10 weeks (`W1`…`W10`) — cash required by due date:

```json
{
  "title": "Cash flow forecast",
  "subtitle": "Liquidity projection and cash needs over next 10 weeks",
  "points": [
    { "label": "W1", "primary": 15000, "secondary": null, "primaryUnit": "currency", "secondaryUnit": null }
  ]
}
```

---

## 11. Supplier / department / geography lists

### Top 10 suppliers by invoice value → `topSuppliersByInvoice[]`

```json
{ "supplier": "Northern Tech Supplies Inc", "amount": 14238.00, "currency": "CAD" }
```

### Outstanding by supplier → `outstandingBySupplier[]`

Same shape; unpaid only.

### Department-wise spend → `departmentSpend[]`

```json
{ "department": "MRO", "amount": 30000, "percent": 34, "currency": "USD" }
```

### Supplier geography → `supplierGeography[]`

```json
{
  "countryCode": "CA",
  "country": "CA",
  "amount": 50000,
  "supplierCount": 4,
  "percent": 57,
  "currency": "CAD"
}
```

---

## 12. Filter options & active filters

### `filterOptions` — populate dropdowns

```json
{
  "filterOptions": {
    "departments": ["General", "MRO", "IT Services"],
    "suppliers": ["CHAMPION INDUSTRIAL SUPPLY", "Northern Tech Supplies Inc"],
    "currencies": ["CAD", "USD"],
    "approvalStatuses": [
      { "key": "all", "label": "All Statuses" },
      { "key": "approved", "label": "Approved" },
      { "key": "partially_approved", "label": "Partially Approved" },
      { "key": "rejected", "label": "Rejected" },
      { "key": "paid", "label": "Paid" },
      { "key": "processing", "label": "Processing" },
      { "key": "hold", "label": "Hold" }
    ],
    "requestStatuses": [
      { "key": "all", "label": "All Request Statuses" },
      { "key": "pending", "label": "Pending" },
      { "key": "processing", "label": "Processing" },
      { "key": "completed", "label": "Completed" },
      { "key": "hold", "label": "On Hold" },
      { "key": "rejected", "label": "Rejected" }
    ],
    "poAmountTiers": [
      { "key": "all", "label": "All Amounts" },
      { "key": "high_value", "label": "High Value (> $100K)" },
      { "key": "low_value", "label": "Low Value (< $1K)" }
    ]
  }
}
```

### `activeFilters` — show applied chips

```json
{
  "activeFilters": {
    "period": "thisMonth",
    "department": "MRO",
    "supplier": null,
    "status": "approved",
    "currency": "USD",
    "requestStatus": null,
    "poAmountTier": null,
    "workflowId": null
  }
}
```

---

## 13. Invoice drill-down → `invoices[]`

Only when `includeInvoiceDetails: true`.

```json
{
  "workflowId": "d78cead9-4530-4032-af2b-374829338dec",
  "workflowName": "Accounts Payable",
  "instanceId": "c009617e-fe7d-42bd-940f-5dc3840446c6",
  "referenceNumber": null,
  "supplier": "Northern Tech Supplies Inc",
  "amount": 1582.00,
  "currency": "CAD",
  "invoiceDate": "2026-06-15T00:00:00Z",
  "dueDate": "2026-07-15T00:00:00Z",
  "department": "General",
  "countryCode": "CA",
  "paymentStatus": "overdue",
  "approvalStatus": "processing",
  "requestStatus": "processing",
  "matchedStatus": "Partially Matched",
  "riskLevel": "high",
  "createdAtUtc": "2026-07-13T02:31:42.2474412Z",
  "processingDays": 0.0
}
```

| Field | Values / notes |
|-------|----------------|
| `paymentStatus` | `pending`, `approved`, `paid`, `overdue`, `due_today` |
| `approvalStatus` | `approved`, `partially_approved`, `rejected`, `paid`, `processing`, `hold`, … |
| `requestStatus` | `pending`, `processing`, `completed`, `hold`, `rejected` |
| `riskLevel` | `low`, `medium`, `high` |

---

## 14. Suggested frontend flow

1. On load: `POST` with `{ "period": "thisMonth" }` (no detail rows).
2. Bind filter chips from `filterOptions`.
3. Bind Command Center strip from `header`.
4. Bind KPI cards from `kpis`.
5. Bind Risk Radar donut from `supplierRiskRadar.segments`.
6. On filter change: re-POST with updated fields (keep `period` + filters in state).
7. For invoice table / aging: set `"includeInvoiceDetails": true`.

### Example fetch

```ts
async function loadApDashboard(token: string, tenantId: string, body: object) {
  const res = await fetch(`${BASE_URL}/api/reports/ap-dashboard`, {
    method: "POST",
    headers: {
      Authorization: `Bearer ${token}`,
      "X-Tenant-Id": tenantId,
      "Content-Type": "application/json",
    },
    body: JSON.stringify(body),
  });
  if (!res.ok) throw new Error(await res.text());
  return res.json();
}
```

---

## 15. Errors

| Status | Meaning |
|--------|---------|
| `400` | Bad body / invalid enum (e.g. wrong `period` string) |
| `401` | Missing/invalid JWT |
| `403` | Role not allowed (`Admin` / `TenantUser` required) |
| `404` | Tenant not found / inactive |

Ensure `period` is a **string** (`"thisMonth"`), not a number.

---

## 16. Data source (for context)

Dashboard aggregates from the current tenant DB:

- `workflow.agentDataValidation_{workflow8}` → `AgentResponse` JSON (primary)
- `dbo.ezfb_*_items` → form fallback
- `workflow.WorkflowInstances_*` / `transaction_*` → status enrichment

Empty KPIs usually mean wrong **`X-Tenant-Id`** or no agent rows in the selected **period**.

---

## 17. Quick checklist for FE

- [ ] Send JWT + `X-Tenant-Id`
- [ ] Use PathBase `/V6API` if on IIS
- [ ] Prefer POST with JSON body
- [ ] Use `filterOptions` for dropdowns
- [ ] Use `header` for Command Center strip
- [ ] Use `supplierRiskRadar` for Low/Medium/High donut
- [ ] Use `kpis[].displayValue` for card text; `value` for charts
- [ ] Set `includeInvoiceDetails: true` only when table is open
- [ ] On Reset, clear filters / send `"all"`
