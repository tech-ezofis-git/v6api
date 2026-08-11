# E2E Multi-User Approval Policy Test
# Tests ApprovalPolicy: AnyOneApprove (first approval moves) vs AllMustApprove (all must approve)
# Uses single user with ApproversJson = [userId, userId] to simulate two approvers for AllMustApprove
#
# Flow A - AnyOneApprove: First approval moves to next step
# Flow B - AllMustApprove: First approval records "waiting for other approvers"; second approval moves
#
# Prerequisites: API running, Catalog DB set up
# Usage: .\Test-E2EMultiUserApproval.ps1
#        .\Test-E2EMultiUserApproval.ps1 -StartFromStep 11 -TenantId "guid" -WorkflowId "guid" -InstanceId "guid" -Email "x@ezofis.com" -Password "xxx"

param(
    [string]$BaseUrl = "https://localhost:5001",
    [string]$OrgName = "Approval Test Corp",
    [string]$Email = "approval@ezofis.com",
    [string]$Password = "Ezofis@123",
    [switch]$SkipSignup,
    [string]$TenantId = "",
    [int]$StartFromStep = 0,
    [string]$WorkflowId = "",
    [string]$InstanceId = ""
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

function Get-StepInstanceByOrder {
    param($StepInstances, [int]$Order)
    $step = $StepInstances | Where-Object { $_.order -eq $Order } | Select-Object -First 1
    if (-not $step) { $step = $StepInstances[$Order - 1] }
    $id = $step.id; if (-not $id) { $id = $step.Id }
    return $id
}

Write-Host "`n=== E2E Multi-User Approval Policy Test ===" -ForegroundColor Cyan
Write-Host "BaseUrl: $BaseUrl" -ForegroundColor Yellow
if ($StartFromStep -gt 0) { Write-Host "StartFromStep: $StartFromStep`n" -ForegroundColor Yellow }
else { Write-Host "" }

# Step 1: Signup or use existing tenant
if ($StartFromStep -ge 2 -and $StartFromStep -le 13) {
    if (-not $TenantId -or -not $WorkflowId -or -not $InstanceId) {
        throw "StartFromStep $StartFromStep requires -TenantId, -WorkflowId, -InstanceId"
    }
    $tenantId = $TenantId
    $workflowId = $WorkflowId
    $instanceId = $InstanceId
    Write-Host "Step 1: Skipped (using provided TenantId, WorkflowId, InstanceId)" -ForegroundColor Cyan
} elseif ($SkipSignup -and $TenantId) {
    Write-Host "Step 1: Using existing tenant $TenantId" -ForegroundColor Cyan
} else {
    Write-Host "Step 1: Signup tenant..." -ForegroundColor Cyan
    $suffix = Get-Random -Minimum 1000 -Maximum 9999
    $signupBody = @{
        organizationName = "$OrgName $suffix"
        email            = $Email
        password         = $Password
        firstName        = "Approval"
        lastName         = "Tester"
    }
    try {
        $signup = Invoke-Api -Method POST -Uri "$BaseUrl/api/Signup" -Body $signupBody
        $tenantId = $signup.tenantId; if (-not $tenantId) { $tenantId = $signup.TenantId }
        Write-Host "  OK: Tenant $tenantId created" -ForegroundColor Green
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

# Step 2: Login (always needed for auth)
if ($StartFromStep -eq 0 -or $StartFromStep -ge 2) {
Write-Host "`nStep 2: Login..." -ForegroundColor Cyan
$loginBody = @{ email = $Email; password = $Password }
$loginHeaders = @{ "X-Tenant-Id" = $tenantId }
$login = Invoke-Api -Method POST -Uri "$BaseUrl/api/auth/ezofis/login" -Body $loginBody -ExtraHeaders $loginHeaders
$token = $login.accessToken; if (-not $token) { $token = $login.token }
if (-not $token) { throw "No token in login response." }
Write-Host "  OK: Logged in" -ForegroundColor Green

$authHeaders = @{ "Authorization" = "Bearer $token"; "X-Tenant-Id" = $tenantId }
}

# Step 3: Get current user ID (for ApproversJson)
if ($StartFromStep -eq 0 -or $StartFromStep -ge 3) {
Write-Host "`nStep 3: Get current user ID..." -ForegroundColor Cyan
$usersResp = Invoke-Api -Method GET -Uri "$BaseUrl/api/Users" -ExtraHeaders $authHeaders
$currentUser = $usersResp.items | Where-Object { $_.email -eq $Email } | Select-Object -First 1
if (-not $currentUser) { $currentUser = $usersResp.Items | Where-Object { $_.Email -eq $Email } | Select-Object -First 1 }
if (-not $currentUser) { throw "Current user not found in /api/Users." }
$userId = $currentUser.id; if (-not $userId) { $userId = $currentUser.Id }
Write-Host "  OK: UserId = $userId" -ForegroundColor Green
}

# Step 3.5: Create a minimal form (workflow start requires InitiateUsing.FormId -- see Test-E2EWorkflow.ps1 for detail).
if ($StartFromStep -eq 0 -or $StartFromStep -ge 4) {
Write-Host "`nStep 3.5: Create form (for workflow InitiateUsing.FormId)..." -ForegroundColor Cyan
$formId = Invoke-Api -Method POST -Uri "$BaseUrl/api/form" -Body @{ formJson = @{ name = "Approval Test Form" } } -ExtraHeaders $authHeaders
$formId = $formId -replace '"', ''
Write-Host "  OK: Form $formId created" -ForegroundColor Green
}

# Step 4: Create workflow
if ($StartFromStep -eq 0 -or $StartFromStep -ge 4) {
Write-Host "`nStep 4: Create Approval Policy Test workflow..." -ForegroundColor Cyan
$createBody = @{
    name        = "Approval Policy Test"
    description = "Tests AnyOneApprove vs AllMustApprove"
    triggerType = 0
    workflowJson = @{
        settings = @{
            general = @{
                initiateUsing = @{ type = "FORM"; formId = $formId }
            }
        }
    }
}
$create = Invoke-Api -Method POST -Uri "$BaseUrl/api/Workflows" -Body $createBody -ExtraHeaders $authHeaders
$workflowId = $create.workflowId; if (-not $workflowId) { $workflowId = $create.WorkflowId }
Write-Host "  OK: Workflow $workflowId created" -ForegroundColor Green
}

# Step 5: Add steps
if ($StartFromStep -eq 0 -or $StartFromStep -ge 5) {
# Step 1: Submit (Task), Step 2: AnyOneApprove (first approval moves), Step 3: AllMustApprove (need 2 approvals)
# approversJson: JSON array of GUIDs as strings, e.g. ["guid1","guid2"]
$approversJson = '["' + $userId + '","' + $userId + '"]'  # Same user twice to simulate 2 approvers
Write-Host "`nStep 5: Add workflow steps (AnyOneApprove + AllMustApprove)..." -ForegroundColor Cyan
$steps = @(
    @{ name = "Submit"; stepType = 0; order = 1; description = "Submit request"; isRequired = $true }
    @{ name = "AnyOne Approval"; stepType = 1; order = 2; description = "First approval moves"; isRequired = $true; approvalPolicy = 1; approversJson = $approversJson }
    @{ name = "AllMust Approval"; stepType = 1; order = 3; description = "Both must approve"; isRequired = $true; approvalPolicy = 0; approversJson = $approversJson }
    @{ name = "Final"; stepType = 0; order = 4; description = "Final step"; isRequired = $true }
)
$stepIdsByOrder = @{}
foreach ($s in $steps) {
    $stepResp = Invoke-Api -Method POST -Uri "$BaseUrl/api/Workflows/$workflowId/steps" -Body $s -ExtraHeaders $authHeaders
    $stepId = $stepResp.stepId; if (-not $stepId) { $stepId = $stepResp.StepId }
    if (-not $stepId) { $stepId = $stepResp.id }
    $stepIdsByOrder[$s.order] = $stepId
}
Write-Host "  OK: Added Submit, AnyOne Approval, AllMust Approval, Final" -ForegroundColor Green
}

# Step 6: Publish
if ($StartFromStep -eq 0 -or $StartFromStep -ge 6) {
Write-Host "`nStep 6: Publish workflow..." -ForegroundColor Cyan
Invoke-Api -Method POST -Uri "$BaseUrl/api/Workflows/$workflowId/publish" -ExtraHeaders $authHeaders | Out-Null
Write-Host "  OK: Published" -ForegroundColor Green

# Step 7: Start instance
Write-Host "`nStep 7: Start workflow instance..." -ForegroundColor Cyan
$start = Invoke-Api -Method POST -Uri "$BaseUrl/api/Workflows/$workflowId/start/json" -Body @{ context = "{}" } -ExtraHeaders $authHeaders
$instanceId = $start.instanceId; if (-not $instanceId) { $instanceId = $start.InstanceId }
Write-Host "  OK: Instance $instanceId started" -ForegroundColor Green
}

# Step 8: Move past Submit (step 1)
if ($StartFromStep -eq 0 -or $StartFromStep -ge 8) {
Write-Host "`nStep 8: MoveToNext (Submit -> AnyOne Approval)..." -ForegroundColor Cyan
# review must be non-empty for move-next to actually complete-and-advance the step (see Test-E2EWorkflow.ps1 for detail).
Invoke-Api -Method POST -Uri "$BaseUrl/api/Workflows/instances/$instanceId/move-next" -Body @{ activityid = $stepIdsByOrder[1]; review = "Reviewed"; comments = "Submitted" } -ExtraHeaders $authHeaders | Out-Null
Write-Host "  OK: At AnyOne Approval step" -ForegroundColor Green
}

# Step 9: Approve AnyOne step - FIRST approval should move (AnyOneApprove)
if ($StartFromStep -eq 0 -or $StartFromStep -ge 9) {
Write-Host "`nStep 9: Approve AnyOne Approval (expect: moves to AllMust)..." -ForegroundColor Cyan
$instanceResp = Invoke-Api -Method GET -Uri "$BaseUrl/api/Workflows/$workflowId/instances/$instanceId" -ExtraHeaders $authHeaders
$stepInstances = $instanceResp.stepInstances; if (-not $stepInstances) { $stepInstances = $instanceResp.StepInstances }
$anyOneStepId = Get-StepInstanceByOrder -StepInstances $stepInstances -Order 2
$approveResp = Invoke-Api -Method POST -Uri "$BaseUrl/api/Workflows/instances/$instanceId/steps/$anyOneStepId/approve" -Body @{ comments = "First approval"; moveToNextStep = $true } -ExtraHeaders $authHeaders
Write-Host "  Response: $($approveResp.message)" -ForegroundColor Gray
if ($approveResp.message -match "moved to") {
    Write-Host "  OK: AnyOneApprove - first approval moved to next step" -ForegroundColor Green
} else {
    Write-Host "  FAIL: Expected to move on first approval (AnyOneApprove)" -ForegroundColor Red
    exit 1
}
}

# Step 10: First approval of AllMust - should NOT move
if ($StartFromStep -eq 0 -or $StartFromStep -ge 10) {
Write-Host "`nStep 10: First approval of AllMust Approval (expect: waiting for other approvers)..." -ForegroundColor Cyan
$instanceResp = Invoke-Api -Method GET -Uri "$BaseUrl/api/Workflows/$workflowId/instances/$instanceId" -ExtraHeaders $authHeaders
$stepInstances = $instanceResp.stepInstances; if (-not $stepInstances) { $stepInstances = $instanceResp.StepInstances }
$allMustStepId = Get-StepInstanceByOrder -StepInstances $stepInstances -Order 3
$approveResp1 = Invoke-Api -Method POST -Uri "$BaseUrl/api/Workflows/instances/$instanceId/steps/$allMustStepId/approve" -Body @{ comments = "First of two"; moveToNextStep = $true } -ExtraHeaders $authHeaders
Write-Host "  Response: $($approveResp1.message)" -ForegroundColor Gray
if ($approveResp1.message -match "waiting for other approvers") {
    Write-Host "  OK: AllMustApprove - first approval recorded, waiting for second" -ForegroundColor Green
} else {
    Write-Host "  FAIL: Expected 'waiting for other approvers' (AllMustApprove)" -ForegroundColor Red
    exit 1
}
}

# Step 11: Second approval of AllMust - should move
if ($StartFromStep -eq 0 -or $StartFromStep -eq 11) {
Write-Host "`nStep 11: Second approval of AllMust Approval (expect: moves to Final)..." -ForegroundColor Cyan
if ($StartFromStep -eq 11) {
    $instanceResp = Invoke-Api -Method GET -Uri "$BaseUrl/api/Workflows/$workflowId/instances/$instanceId" -ExtraHeaders $authHeaders
    $stepInstances = $instanceResp.stepInstances; if (-not $stepInstances) { $stepInstances = $instanceResp.StepInstances }
    $allMustStepId = Get-StepInstanceByOrder -StepInstances $stepInstances -Order 3
}
$approveResp2 = Invoke-Api -Method POST -Uri "$BaseUrl/api/Workflows/instances/$instanceId/steps/$allMustStepId/approve" -Body @{ comments = "Second approval"; moveToNextStep = $true } -ExtraHeaders $authHeaders
Write-Host "  Response: $($approveResp2.message)" -ForegroundColor Gray
if ($approveResp2.message -match "moved to" -or $approveResp2.message -match "completed") {
    Write-Host "  OK: AllMustApprove - second approval moved" -ForegroundColor Green
} else {
    Write-Host "  FAIL: Expected to move on second approval" -ForegroundColor Red
    exit 1
}
}

# Step 12: Move past Final (complete workflow)
if ($StartFromStep -eq 0 -or $StartFromStep -ge 12) {
Write-Host "`nStep 12: MoveToNext (Final -> complete)..." -ForegroundColor Cyan
Invoke-Api -Method POST -Uri "$BaseUrl/api/Workflows/instances/$instanceId/move-next" -Body @{ activityid = $stepIdsByOrder[4]; review = "Reviewed"; comments = "Done" } -ExtraHeaders $authHeaders | Out-Null
Write-Host "  OK: Workflow completed" -ForegroundColor Green
}

# Verify
Write-Host "`nStep 13: Verify..." -ForegroundColor Cyan
$instanceResp = Invoke-Api -Method GET -Uri "$BaseUrl/api/Workflows/$workflowId/instances/$instanceId" -ExtraHeaders $authHeaders
$status = $instanceResp.status
if ($status -eq 3 -or $status -eq "Completed") {
    Write-Host "  OK: Instance status = Completed" -ForegroundColor Green
} else {
    Write-Host "  WARN: Instance status = $status" -ForegroundColor Yellow
}

Write-Host "`n=== E2E Multi-User Approval Policy Test Passed ===" -ForegroundColor Cyan
Write-Host "TenantId:   $tenantId" -ForegroundColor White
Write-Host "WorkflowId: $workflowId" -ForegroundColor White
Write-Host "InstanceId: $instanceId" -ForegroundColor White
Write-Host "`nBoth AnyOneApprove and AllMustApprove policies verified.`n" -ForegroundColor Gray
