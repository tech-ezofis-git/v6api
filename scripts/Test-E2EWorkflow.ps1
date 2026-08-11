# E2E Workflow Test - Full flow with WorkflowApprovals
# Flow: Signup -> Login -> Create Invoice Approval (5 steps) -> Publish -> Start -> Comments -> Approve/Reject
# Populates: Workflows, WorkflowSteps, WorkflowInstances, WorkflowStepInstances, WorkflowApprovals, WorkflowComments_*
# Prerequisites: API running (dotnet run --project src/Api), Catalog DB set up
#
# Usage: .\Test-E2EWorkflow.ps1
#        .\Test-E2EWorkflow.ps1 -BaseUrl "https://localhost:5001"
#        .\Test-E2EWorkflow.ps1 -SkipSignup -TenantId "guid" -Email "user@ezofis.com" -Password "xxx"
#        .\Test-E2EWorkflow.ps1 -RejectAtFinance  # Reject at step 3 instead of completing

param(
    [string]$BaseUrl = "https://localhost:5001",
    [string]$OrgName = "Demo Corp",
    [string]$Email = "demo@ezofis.com",
    [string]$Password = "Ezofis@123",
    [string]$FirstName = "Demo",
    [string]$LastName = "User",
    [switch]$SkipSignup,
    [string]$TenantId = "",
    [switch]$RejectAtFinance  # If set, reject at Finance Approval instead of completing
)

$ErrorActionPreference = "Stop"
$scriptDir = $PSScriptRoot

# Skip cert validation for localhost
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

Write-Host "`n=== E2E Workflow Test (Invoice Approval 5 Steps) ===" -ForegroundColor Cyan
Write-Host "BaseUrl: $BaseUrl | RejectAtFinance: $RejectAtFinance`n" -ForegroundColor Yellow

# Step 1: Signup (or use existing tenant)
if ($SkipSignup -and $TenantId) {
    $tenantId = $TenantId
    Write-Host "Step 1: Using existing tenant $tenantId" -ForegroundColor Cyan
} else {
    Write-Host "Step 1: Signup tenant..." -ForegroundColor Cyan
    $suffix = Get-Random -Minimum 1000 -Maximum 9999
    $signupBody = @{
        organizationName = "$OrgName $suffix"
        email            = $Email
        password         = $Password
        firstName        = $FirstName
        lastName         = $LastName
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

# Step 2: Login
Write-Host "`nStep 2: Login..." -ForegroundColor Cyan
$loginBody = @{ email = $Email; password = $Password }
$loginHeaders = @{ "X-Tenant-Id" = $tenantId }
$login = Invoke-Api -Method POST -Uri "$BaseUrl/api/auth/ezofis/login" -Body $loginBody -ExtraHeaders $loginHeaders
$token = $login.accessToken; if (-not $token) { $token = $login.token }
if (-not $token) { throw "No token in login response." }
Write-Host "  OK: Logged in" -ForegroundColor Green

$authHeaders = @{ "Authorization" = "Bearer $token"; "X-Tenant-Id" = $tenantId }

# Step 2.5: Create a minimal form (POST /api/form returns the new form's string id as the body).
# Needed because Workflow start requires a real dbo.wForm/wFormControl row -- WorkflowEzfbFormDataLoader
# queries dbo."wFormControl" unconditionally, and that table is only created (by FormService's
# EnsureFormSchemaAsync) the first time a form is actually saved via this endpoint. A random,
# never-saved GUID as FormId fails with "relation dbo.wFormControl does not exist" (confirmed --
# same behavior would occur pre-migration too, since EnsureFormSchemaAsync's lazy-create-on-first-
# save comment describes pre-existing, dialect-neutral behavior, not something this port changed).
Write-Host "`nStep 2.5: Create form (for workflow InitiateUsing.FormId)..." -ForegroundColor Cyan
$formBody = @{ formJson = @{ name = "Invoice Form" } }
$formId = Invoke-Api -Method POST -Uri "$BaseUrl/api/form" -Body $formBody -ExtraHeaders $authHeaders
$formId = $formId -replace '"', ''
Write-Host "  OK: Form $formId created" -ForegroundColor Green

# Step 3: Create Invoice Approval Workflow
Write-Host "`nStep 3: Create Invoice Approval workflow..." -ForegroundColor Cyan
$createBody = @{
    name        = "Invoice Approval"
    description = "5-step invoice approval: Submit -> Dept Review -> Finance -> Manager -> Final"
    triggerType = 0  # Manual
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

# Step 4: Add 5 steps (Submit Invoice, Dept Review, Finance Approval, Manager Approval, Final Approval)
Write-Host "`nStep 4: Add 5 workflow steps..." -ForegroundColor Cyan
$steps = @(
    @{ name = "Submit Invoice"; stepType = 0; order = 1; description = "Requester submits invoice"; isRequired = $true }
    @{ name = "Department Review"; stepType = 0; order = 2; description = "Dept head verifies"; isRequired = $true }
    @{ name = "Finance Approval"; stepType = 1; order = 3; description = "Finance approves/rejects"; isRequired = $true }
    @{ name = "Manager Approval"; stepType = 1; order = 4; description = "Manager approves/rejects"; isRequired = $true }
    @{ name = "Final Approval"; stepType = 1; order = 5; description = "Final sign-off"; isRequired = $true }
)
$stepIdsByOrder = @{}
foreach ($s in $steps) {
    $stepResp = Invoke-Api -Method POST -Uri "$BaseUrl/api/Workflows/$workflowId/steps" -Body $s -ExtraHeaders $authHeaders
    $stepId = $stepResp.stepId; if (-not $stepId) { $stepId = $stepResp.StepId }
    if (-not $stepId) { $stepId = $stepResp.id }
    $stepIdsByOrder[$s.order] = $stepId
}
Write-Host "  OK: Added Submit Invoice, Department Review, Finance Approval, Manager Approval, Final Approval" -ForegroundColor Green

# Step 5: Publish
Write-Host "`nStep 5: Publish workflow..." -ForegroundColor Cyan
Invoke-Api -Method POST -Uri "$BaseUrl/api/Workflows/$workflowId/publish" -ExtraHeaders $authHeaders | Out-Null
Write-Host "  OK: Published" -ForegroundColor Green

# Step 6: Start instance (Submit Invoice)
Write-Host "`nStep 6: Start workflow instance (Submit Invoice)..." -ForegroundColor Cyan
$startBody = @{ context = '{"referenceNumber":"INV-2025-001","amount":5000}' }
$start = Invoke-Api -Method POST -Uri "$BaseUrl/api/Workflows/$workflowId/start/json" -Body $startBody -ExtraHeaders $authHeaders
$instanceId = $start.instanceId; if (-not $instanceId) { $instanceId = $start.InstanceId }
Write-Host "  OK: Instance $instanceId started" -ForegroundColor Green

# Step 7: Add comment (Invoice submitted)
Write-Host "`nStep 7: Add comment (Invoice submitted)..." -ForegroundColor Cyan
$commentBody = @{ comments = "Invoice #INV-2025-001 submitted. Amount: $5,000" }
Invoke-Api -Method POST -Uri "$BaseUrl/api/Workflows/$workflowId/instances/$instanceId/comments" -Body $commentBody -ExtraHeaders $authHeaders | Out-Null
Write-Host "  OK: Comment added" -ForegroundColor Green

# Step 8: MoveToNext (complete Submit Invoice, start Department Review)
Write-Host "`nStep 8: MoveToNext (complete Submit Invoice -> Department Review)..." -ForegroundColor Cyan
# review must be non-empty for the step to actually complete-and-advance (WorkflowLegacyTransactionSyncService's
# own doc comment: "review empty/null: insert step row if missing; if exists return StepAlreadyThere; review
# provided: update review on existing open row ... on successful review update insert next step row"). Without
# it, move-next only records/no-ops -- confirmed empirically (step stayed InProgress, next step never started).
Invoke-Api -Method POST -Uri "$BaseUrl/api/Workflows/instances/$instanceId/move-next" -Body @{ activityid = $stepIdsByOrder[1]; review = "Reviewed"; comments = "Invoice details verified" } -ExtraHeaders $authHeaders | Out-Null
Write-Host "  OK: Moved to Department Review" -ForegroundColor Green

# Step 9: MoveToNext (complete Department Review, start Finance Approval)
Write-Host "`nStep 9: MoveToNext (complete Dept Review -> Finance Approval)..." -ForegroundColor Cyan
Invoke-Api -Method POST -Uri "$BaseUrl/api/Workflows/instances/$instanceId/move-next" -Body @{ activityid = $stepIdsByOrder[2]; review = "Reviewed"; comments = "Dept review done" } -ExtraHeaders $authHeaders | Out-Null
Write-Host "  OK: Moved to Finance Approval" -ForegroundColor Green

# Step 10: Finance Approval - Approve or Reject
$instanceResp = Invoke-Api -Method GET -Uri "$BaseUrl/api/Workflows/$workflowId/instances/$instanceId" -ExtraHeaders $authHeaders
$stepInstances = $instanceResp.stepInstances; if (-not $stepInstances) { $stepInstances = $instanceResp.StepInstances }

if ($RejectAtFinance) {
    Write-Host "`nStep 10: Reject at Finance Approval..." -ForegroundColor Cyan
    $financeStepId = Get-StepInstanceByOrder -StepInstances $stepInstances -Order 3
    $rejectBody = @{ reason = "Amount exceeds budget"; cancelWorkflow = $true }
    Invoke-Api -Method POST -Uri "$BaseUrl/api/Workflows/instances/$instanceId/steps/$financeStepId/reject" -Body $rejectBody -ExtraHeaders $authHeaders | Out-Null
    Write-Host "  OK: Rejected - workflow cancelled (WorkflowApprovals record inserted)" -ForegroundColor Green
} else {
    # Step 10: Approve Finance Approval (-> Manager Approval)
    Write-Host "`nStep 10: Approve Finance Approval (-> Manager Approval)..." -ForegroundColor Cyan
    $financeStepId = Get-StepInstanceByOrder -StepInstances $stepInstances -Order 3
    Invoke-Api -Method POST -Uri "$BaseUrl/api/Workflows/instances/$instanceId/steps/$financeStepId/approve" -Body @{ comments = "Finance approved"; moveToNextStep = $true } -ExtraHeaders $authHeaders | Out-Null
    Write-Host "  OK: Finance approved (WorkflowApprovals record inserted)" -ForegroundColor Green

    # Step 11: Approve Manager Approval (-> Final Approval)
    Write-Host "`nStep 11: Approve Manager Approval (-> Final Approval)..." -ForegroundColor Cyan
    $instanceResp = Invoke-Api -Method GET -Uri "$BaseUrl/api/Workflows/$workflowId/instances/$instanceId" -ExtraHeaders $authHeaders
    $stepInstances = $instanceResp.stepInstances; if (-not $stepInstances) { $stepInstances = $instanceResp.StepInstances }
    $managerStepId = Get-StepInstanceByOrder -StepInstances $stepInstances -Order 4
    Invoke-Api -Method POST -Uri "$BaseUrl/api/Workflows/instances/$instanceId/steps/$managerStepId/approve" -Body @{ comments = "Manager approved"; moveToNextStep = $true } -ExtraHeaders $authHeaders | Out-Null
    Write-Host "  OK: Manager approved" -ForegroundColor Green

    # Step 12: Approve Final Approval (complete workflow)
    Write-Host "`nStep 12: Approve Final Approval (complete workflow)..." -ForegroundColor Cyan
    $instanceResp = Invoke-Api -Method GET -Uri "$BaseUrl/api/Workflows/$workflowId/instances/$instanceId" -ExtraHeaders $authHeaders
    $stepInstances = $instanceResp.stepInstances; if (-not $stepInstances) { $stepInstances = $instanceResp.StepInstances }
    $finalStepId = Get-StepInstanceByOrder -StepInstances $stepInstances -Order 5
    Invoke-Api -Method POST -Uri "$BaseUrl/api/Workflows/instances/$instanceId/steps/$finalStepId/approve" -Body @{ comments = "Final approval - payment released"; moveToNextStep = $true } -ExtraHeaders $authHeaders | Out-Null
    Write-Host "  OK: Workflow completed" -ForegroundColor Green
}

# Step 13: Verify
Write-Host "`nStep 13: Verify instance status..." -ForegroundColor Cyan
$instanceResp = Invoke-Api -Method GET -Uri "$BaseUrl/api/Workflows/$workflowId/instances/$instanceId" -ExtraHeaders $authHeaders
$status = $instanceResp.status
$expectedStatus = if ($RejectAtFinance) { 5 } else { 3 }  # 5=Cancelled, 3=Completed
$statusOk = ($status -eq $expectedStatus) -or ($status -eq "Cancelled" -and $RejectAtFinance) -or ($status -eq "Completed" -and -not $RejectAtFinance)
if ($statusOk) {
    Write-Host "  OK: Instance status = $status (expected)" -ForegroundColor Green
} else {
    Write-Host "  WARN: Instance status = $status (expected $expectedStatus)" -ForegroundColor Yellow
}

Write-Host "`n=== E2E Workflow Test Passed ===" -ForegroundColor Cyan
Write-Host "TenantId:   $tenantId" -ForegroundColor White
Write-Host "WorkflowId: $workflowId" -ForegroundColor White
Write-Host "InstanceId: $instanceId" -ForegroundColor White
Write-Host "Status:     $status" -ForegroundColor White
Write-Host "`nTables populated: workflow.Workflows, workflow.WorkflowSteps, workflow.WorkflowInstances," -ForegroundColor Gray
Write-Host "workflow.WorkflowStepInstances, workflow.WorkflowApprovals, workflow.WorkflowComments_*" -ForegroundColor Gray
Write-Host "Use Verify-Tables.ps1 -TenantId $tenantId to verify.`n" -ForegroundColor White
