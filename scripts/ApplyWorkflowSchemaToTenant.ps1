# Apply workflow schema to an existing tenant database
# Use when tenant was created without workflow tables (e.g. workflow."Workflows" missing)
#
# Usage: .\ApplyWorkflowSchemaToTenant.ps1 -TenantDatabase "ezofis_tenant_1"
# Or:    .\ApplyWorkflowSchemaToTenant.ps1 -TenantId "fccf34b5-5588-4334-869a-e4c7b10b244d"
#        (queries catalog for connection string)

param(
    [string]$PgHost = "localhost",
    [string]$Port = "5433",
    [string]$User = "postgres",
    [string]$Password = "postgres",
    [string]$CatalogDatabase = "ezofis_catalog_new",
    [string]$TenantDatabase = "",
    [string]$TenantId = ""
)

$ErrorActionPreference = "Stop"
$scriptDir = $PSScriptRoot
$pgScriptDir = Join-Path $scriptDir "postgres"
$env:PGPASSWORD = $Password

# Resolve tenant database from catalog if TenantId provided
if ($TenantId -and -not $TenantDatabase) {
    $cs = (& psql -h $PgHost -p $Port -U $User -d $CatalogDatabase -v ON_ERROR_STOP=1 -t -A -c "SELECT ""ConnectionString"" FROM catalog.""Tenants"" WHERE ""Id"" = '$TenantId'") -join ""
    if ($LASTEXITCODE -ne 0) { Write-Host "Failed to query catalog." -ForegroundColor Red; exit 1 }
    if ($cs) {
        if ($cs -match 'Database=([^;]+)') { $TenantDatabase = $Matches[1].Trim() }
        if (-not $TenantDatabase) {
            Write-Host "Could not parse database name from connection string" -ForegroundColor Red
            exit 1
        }
        Write-Host "Resolved tenant DB: $TenantDatabase" -ForegroundColor Cyan
    } else {
        Write-Host "Tenant not found in catalog: $TenantId" -ForegroundColor Red
        exit 1
    }
}

if (-not $TenantDatabase) {
    Write-Host "Usage: -TenantDatabase 'ezofis_tenant_1' OR -TenantId 'guid'" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== Apply Workflow Schema to Tenant ===" -ForegroundColor Cyan
Write-Host "Database: $TenantDatabase`n" -ForegroundColor Yellow

# Ported Postgres script uses CREATE SCHEMA/TABLE/INDEX IF NOT EXISTS throughout (no SQL Server
# "GO" batch separators), so it runs as a single psql -f invocation.
& psql -h $PgHost -p $Port -U $User -d $TenantDatabase -v ON_ERROR_STOP=1 -f "$pgScriptDir\CreateWorkflowSchemaComplete.sql"
if ($LASTEXITCODE -ne 0) {
    Write-Host "  Error applying workflow schema (exit code $LASTEXITCODE)" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== Done ===" -ForegroundColor Cyan
Write-Host 'Workflow schema applied. workflow."Workflows" and related tables are ready.' -ForegroundColor White
