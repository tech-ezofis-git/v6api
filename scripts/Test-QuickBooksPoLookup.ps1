<#
.SYNOPSIS
  Smoke-test QuickBooks Purchase Order lookup by PO Number (AP Agent matching API).

.EXAMPLE
  .\scripts\Test-QuickBooksPoLookup.ps1 -BaseUrl http://localhost:5000 -Jwt $token -TenantId 0B3E1B77-... -ConnectorId A678760B-...
#>
param(
    [string]$BaseUrl = "http://localhost:5000",
    [Parameter(Mandatory = $true)][string]$Jwt,
    [Parameter(Mandatory = $true)][string]$TenantId,
    [Parameter(Mandatory = $true)][string]$ConnectorId,
    [string]$PoNumber = ""
)

$ErrorActionPreference = "Stop"
$headers = @{
    Authorization  = "Bearer $Jwt"
    "X-Tenant-Id"  = $TenantId
    "Content-Type" = "application/json"
}

Write-Host "== List PurchaseOrders (discover PO Number) ==" -ForegroundColor Cyan
$pos = Invoke-RestMethod -Uri "$BaseUrl/api/connector/$ConnectorId/quickbooks/documents?documentType=PurchaseOrder&maxResults=10" -Headers $headers
$pos.items | Select-Object id, docNumber, txnDate, totalAmount, customerVendorName, status | Format-Table -AutoSize

if ([string]::IsNullOrWhiteSpace($PoNumber)) {
    $PoNumber = ($pos.items | Where-Object { $_.docNumber } | Select-Object -First 1).docNumber
}
if ([string]::IsNullOrWhiteSpace($PoNumber)) {
    throw "No PurchaseOrder DocNumber found in sandbox. Create a PO in QBO or pass -PoNumber."
}

Write-Host "`n== Lookup PO Number: $PoNumber ==" -ForegroundColor Cyan
$body = @{ poNumber = $PoNumber } | ConvertTo-Json
$result = Invoke-RestMethod -Method POST -Uri "$BaseUrl/api/connector/$ConnectorId/quickbooks/purchase-orders/lookup" -Headers $headers -Body $body
$result | ConvertTo-Json -Depth 12

Write-Host "`n== Not-found check ==" -ForegroundColor Cyan
$missing = Invoke-RestMethod -Method POST -Uri "$BaseUrl/api/connector/$ConnectorId/quickbooks/purchase-orders/lookup" -Headers $headers -Body (@{ poNumber = "__NO_SUCH_PO__" } | ConvertTo-Json)
$missing | ConvertTo-Json -Depth 4

if (-not $result.found) { throw "Expected found=true for PO '$PoNumber'" }
$lineCount = @($result.purchaseOrder.'PO Line Item').Count
Write-Host ("`nSMOKE OK - found={0} vendor={1} total={2} lines={3}" -f $result.found, $result.purchaseOrder.'Vendor Name', $result.purchaseOrder.'PO Amount', $lineCount) -ForegroundColor Green
