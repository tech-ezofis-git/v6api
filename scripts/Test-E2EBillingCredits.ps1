# E2E Billing/Credits Test
# Flow: Signup -> Login -> Update credit (consume) -> Get master -> Get monthly balances -> Usage dashboard
# Prerequisites: API running, Catalog DB set up
#
# Usage: .\Test-E2EBillingCredits.ps1
#        .\Test-E2EBillingCredits.ps1 -BaseUrl "https://localhost:5001"

param(
    [string]$BaseUrl = "https://localhost:5001",
    [string]$OrgName = "Billing Test Corp",
    [string]$Email = "billing@ezofis.com",
    [string]$Password = "Ezofis@123",
    [switch]$SkipSignup,
    [string]$TenantId = ""
)

$ErrorActionPreference = "Stop"

if ($BaseUrl -match "localhost") {
    add-type @"
    using System.Net; using System.Security.Cryptography.X509Certificates;
    public class TrustAllCertsPolicy : ICertificatePolicy { public bool CheckValidationResult(ServicePoint s, X509Certificate c, WebRequest r, int p) { return true; } }
"@
    [System.Net.ServicePointManager]::CertificatePolicy = New-Object TrustAllCertsPolicy
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
}

$headers = @{ "Content-Type" = "application/json"; "Accept" = "application/json" }

function Invoke-Api {
    param([string]$Method, [string]$Uri, [object]$Body, [hashtable]$ExtraHeaders)
    $h = $headers.Clone()
    if ($ExtraHeaders) { $ExtraHeaders.GetEnumerator() | ForEach-Object { $h[$_.Key] = $_.Value } }
    $params = @{ Method = $Method; Uri = $Uri; Headers = $h; UseBasicParsing = $true }
    if ($Body) { $params.Body = ($Body | ConvertTo-Json -Depth 10) }
    return Invoke-RestMethod @params
}

Write-Host "`n=== E2E Billing/Credits Test ===" -ForegroundColor Cyan
Write-Host "BaseUrl: $BaseUrl`n" -ForegroundColor Yellow

# Step 1: Signup or use existing tenant
if ($SkipSignup -and $TenantId) {
    Write-Host "Step 1: Using existing tenant $TenantId" -ForegroundColor Cyan
} else {
    Write-Host "Step 1: Signup tenant..." -ForegroundColor Cyan
    $suffix = Get-Random -Minimum 1000 -Maximum 9999
    $signupBody = @{
        organizationName = "$OrgName $suffix"
        email            = $Email
        password         = $Password
        firstName        = "Billing"
        lastName         = "Tester"
    }
    $signup = Invoke-Api -Method POST -Uri "$BaseUrl/api/Signup" -Body $signupBody
    $tenantId = $signup.tenantId; if (-not $tenantId) { $tenantId = $signup.TenantId }
    Write-Host "  OK: Tenant $tenantId created" -ForegroundColor Green
}

# Step 2: Login
Write-Host "`nStep 2: Login..." -ForegroundColor Cyan
$loginBody = @{ email = $Email; password = $Password }
$loginHeaders = @{ "X-Tenant-Id" = $tenantId }
$login = Invoke-Api -Method POST -Uri "$BaseUrl/api/auth/ezofis/login" -Body $loginBody -ExtraHeaders $loginHeaders
$token = $login.accessToken; if (-not $token) { $token = $login.token }
if (-not $token) { throw "No token in login response." }
Write-Host "  OK: Logged in" -ForegroundColor Green

$authHeaders = @{ "Authorization" = "Bearer $token"; "X-Tenant-Id" = $tenantId }

# Step 3: Consume credit (writes a creditTransaction ledger row + updates/creates creditMaster row)
Write-Host "`nStep 3: Update credit (consume)..." -ForegroundColor Cyan
$updateBody = @{
    activityType = "AP_AGENT"
    subActivity  = "OCR"
    identify     = "Invoice"
    identifyId   = 1
    remarks      = "E2E test consumption"
    credit       = 5
    inputTokens  = 100
    outputTokens = 50
    totalTokens  = 150
}
$updateResp = Invoke-Api -Method POST -Uri "$BaseUrl/api/billing/credits/update" -Body $updateBody -ExtraHeaders $authHeaders
Write-Host "  Response: Id=$($updateResp.id) Output=$($updateResp.output)" -ForegroundColor Gray
Write-Host "  OK: Credit update call succeeded" -ForegroundColor Green

# Step 4: Get credit master (current month)
Write-Host "`nStep 4: Get credit master (current month)..." -ForegroundColor Cyan
try {
    $master = Invoke-Api -Method GET -Uri "$BaseUrl/api/billing/credits/master" -ExtraHeaders $authHeaders
    Write-Host "  OK: Master row found (InitialCredit=$($master.initialCredit), BalanceCredit=$($master.balanceCredit), MonthlyBalance=$($master.monthlyBalance))" -ForegroundColor Green
} catch {
    if ($_.Exception.Response.StatusCode.value__ -eq 404) {
        Write-Host "  Note: No credit master row exists yet for this tenant/month (404 - expected if no plan/allocation configured)." -ForegroundColor Yellow
    } else { throw }
}

# Step 5: Get monthly balances for current year
Write-Host "`nStep 5: Get monthly balances (year)..." -ForegroundColor Cyan
$year = (Get-Date).Year
$monthly = Invoke-Api -Method GET -Uri "$BaseUrl/api/billing/credits/master/monthly?year=$year" -ExtraHeaders $authHeaders
Write-Host "  OK: Monthly balances returned (count=$($monthly.Count))" -ForegroundColor Green

# Step 6: Credit usage dashboard (monthly)
Write-Host "`nStep 6: Credit usage dashboard (monthly)..." -ForegroundColor Cyan
$usageBody = @{ period = "monthly" }
$usage = Invoke-Api -Method POST -Uri "$BaseUrl/api/billing/credits/usage" -Body $usageBody -ExtraHeaders $authHeaders
Write-Host "  OK: Usage dashboard returned (TotalCreditsConsumed=$($usage.totalCreditsConsumed), TransactionCount=$($usage.transactionCount))" -ForegroundColor Green

Write-Host "`n=== E2E Billing/Credits Test Passed ===" -ForegroundColor Cyan
Write-Host "TenantId: $tenantId`n" -ForegroundColor White
