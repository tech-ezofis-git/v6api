# Quick table verification - lists key tables in Catalog and Tenant DBs
# Usage: .\Verify-Tables.ps1
#        .\Verify-Tables.ps1 -TenantId "guid"   (checks tenant DB from catalog)
#        .\Verify-Tables.ps1 -TenantDatabase "ezofis_tenant_1"

param(
    [string]$PgHost = "localhost",
    [string]$Port = "5433",
    [string]$User = "postgres",
    [string]$Password = "postgres",
    [string]$CatalogDatabase = "ezofis_catalog_new",
    [string]$TenantId = "",
    [string]$TenantDatabase = ""
)

$ErrorActionPreference = "Stop"
$env:PGPASSWORD = $Password

function Invoke-Query {
    param([string]$Database, [string]$Sql)
    $rows = & psql -h $PgHost -p $Port -U $User -d $Database -v ON_ERROR_STOP=1 -t -A -F "|" -c $Sql
    if ($LASTEXITCODE -ne 0) { throw "psql query failed against $Database" }
    return $rows | Where-Object { $_.Trim().Length -gt 0 }
}

$tablesSql = @"
SELECT table_schema || '.' || table_name
FROM information_schema.tables
WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
ORDER BY table_schema, table_name;
"@

# Resolve tenant DB from catalog if TenantId provided
if ($TenantId -and -not $TenantDatabase) {
    $cs = (Invoke-Query -Database $CatalogDatabase -Sql "SELECT ""ConnectionString"" FROM catalog.""Tenants"" WHERE ""Id"" = '$TenantId'") -join ""
    if ($cs -match 'Database=([^;]+)') { $TenantDatabase = $Matches[1].Trim() }
}

Write-Host "`n=== Table Verification ===" -ForegroundColor Cyan

# Catalog
Write-Host "`n[CATALOG] $CatalogDatabase" -ForegroundColor Yellow
$catalogTables = Invoke-Query -Database $CatalogDatabase -Sql $tablesSql
$catalogTables | ForEach-Object { Write-Host $_ }
$keyCatalog = @('catalog.Tenants', 'catalog.UserTenants')
$found = ($catalogTables | Where-Object { $keyCatalog -contains $_ }).Count
Write-Host "Key tables (catalog.Tenants, catalog.UserTenants): $(if ($found -eq 2) { 'OK' } else { 'MISSING' })" -ForegroundColor $(if ($found -eq 2) { 'Green' } else { 'Red' })

# Tenant (if specified)
if ($TenantDatabase) {
    Write-Host "`n[TENANT] $TenantDatabase" -ForegroundColor Yellow
    $tenantTables = Invoke-Query -Database $TenantDatabase -Sql $tablesSql
    $tenantTables | ForEach-Object { Write-Host $_ }
    $keyTenant = @('workflow.Workflows', 'workflow.WorkflowSteps', 'users.Users')
    $foundTenant = ($tenantTables | Where-Object { $keyTenant -contains $_ }).Count
    Write-Host "Key tables (workflow.Workflows, workflow.WorkflowSteps, users.Users): $(if ($foundTenant -ge 3) { 'OK' } else { 'MISSING' })" -ForegroundColor $(if ($foundTenant -ge 3) { 'Green' } else { 'Red' })
} else {
    Write-Host "`n[TENANT] Skipped - use -TenantId or -TenantDatabase to check tenant tables" -ForegroundColor Gray
}

Write-Host ""
