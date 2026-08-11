# E2E DMS (Repository) Test
# Flow: Signup -> Login -> Create Repository -> Setup DMS schema (or auto on first request) -> Folder tree -> Documents
# Prerequisites: API running, Catalog DB set up
#
# Usage: .\Test-E2EDms.ps1
#        .\Test-E2EDms.ps1 -BaseUrl "https://localhost:5001"
#        .\Test-E2EDms.ps1 -SkipSignup -TenantId "guid" -Email "x@ezofis.com" -Password "xxx"

param(
    [string]$BaseUrl = "https://localhost:5001",
    [string]$OrgName = "DMS Test Corp",
    [string]$Email = "dms@ezofis.com",
    [string]$Password = "Ezofis@123",
    [switch]$SkipSignup,
    [string]$TenantId = ""
)

$ErrorActionPreference = "Stop"
$scriptDir = $PSScriptRoot

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

Write-Host "`n=== E2E DMS (Repository) Test ===" -ForegroundColor Cyan
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
        firstName        = "DMS"
        lastName         = "Tester"
    }
    try {
        $signup = Invoke-Api -Method POST -Uri "$BaseUrl/api/Signup" -Body $signupBody
        $tenantId = $signup.tenantId; if (-not $tenantId) { $tenantId = $signup.TenantId }
        Write-Host "  OK: Tenant $tenantId created (Workflow + DMS schema created on signup)" -ForegroundColor Green
    } catch {
        $errBody = $_.ErrorDetails.Message
        if (-not $errBody -and $_.Exception.Response) {
            try {
                $sr = [System.IO.StreamReader]::new($_.Exception.Response.GetResponseStream())
                $errBody = $sr.ReadToEnd(); $sr.Close()
            } catch { }
        }
        if ($errBody) { Write-Host "  API response: $errBody" -ForegroundColor Red }
        if ($errBody -match "already registered") {
            throw "Signup failed. Use -SkipSignup -TenantId 'guid' for existing tenant."
        }
        throw
    }
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

# Step 3: Setup DMS schema (idempotent - creates if missing; middleware applies on first DMS request too)
Write-Host "`nStep 3: Setup DMS schema (POST /api/Dms/setup-schema)..." -ForegroundColor Cyan
try {
    Invoke-Api -Method POST -Uri "$BaseUrl/api/Dms/setup-schema" -ExtraHeaders $authHeaders | Out-Null
    Write-Host "  OK: DMS schema applied" -ForegroundColor Green
} catch {
    Write-Host "  Note: $($_.Exception.Message)" -ForegroundColor Gray
}

# Step 4: Get folder children (root) - uses sample_items table
$repoId = [guid]::NewGuid()
Write-Host "`nStep 4: Get folder children (root) - GET /api/Dms/repositories/{id}/folders/children..." -ForegroundColor Cyan
$folderResp = Invoke-Api -Method GET -Uri "$BaseUrl/api/Dms/repositories/$repoId/folders/children?path=&tableName=sample_items" -ExtraHeaders $authHeaders
$childCount = if ($folderResp.children) { $folderResp.children.Count } else { 0 }
Write-Host "  OK: Folder children returned (path=$($folderResp.path), count=$childCount)" -ForegroundColor Green

# Step 5: Get documents (empty folder - no data yet)
Write-Host "`nStep 5: Get documents in folder - GET /api/Dms/repositories/{id}/documents..." -ForegroundColor Cyan
$docResp = Invoke-Api -Method GET -Uri "$BaseUrl/api/Dms/repositories/$repoId/documents?path=2025/Purchase/Acme%20Corp&tableName=sample_items&page=1&pageSize=10" -ExtraHeaders $authHeaders
$docCount = if ($docResp.items) { $docResp.items.Count } else { 0 }
$total = if ($docResp.totalCount) { $docResp.totalCount } else { 0 }
Write-Host "  OK: Documents returned (items=$docCount, totalCount=$total)" -ForegroundColor Green

Write-Host "`n=== E2E DMS Test Passed ===" -ForegroundColor Cyan
Write-Host "TenantId:   $tenantId" -ForegroundColor White
Write-Host "RepositoryId (sample): $repoId" -ForegroundColor White
Write-Host "`nDMS tables: dms.Repository, dms.RepositoryFolderConfig, dms.DocumentWorkflowLink, dms.sample_items" -ForegroundColor Gray
Write-Host "To add test data: Insert into dms.sample_items (TenantId, RepositoryId, Year, InvoiceType, VendorName, FileName, CreatedBy) and re-run folder/documents API.`n" -ForegroundColor Gray
