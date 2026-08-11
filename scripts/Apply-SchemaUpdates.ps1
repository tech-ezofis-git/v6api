# Apply schema updates for existing deployments
# Run this if you have existing catalog/tenant DBs before testing new signup
#
# Usage: .\Apply-SchemaUpdates.ps1
#        .\Apply-SchemaUpdates.ps1 -CatalogOnly
#        .\Apply-SchemaUpdates.ps1 -TenantId "guid"

param(
    [string]$PgHost = "localhost",
    [string]$Port = "5433",
    [string]$User = "postgres",
    [string]$Password = "postgres",
    [string]$CatalogDatabase = "ezofis_catalog_new",
    [string]$TenantId = "",
    [switch]$CatalogOnly
)

$ErrorActionPreference = "Stop"
$scriptDir = $PSScriptRoot
$env:PGPASSWORD = $Password

$catalogSql = @"
ALTER TABLE catalog."UserTenants" ADD COLUMN IF NOT EXISTS "IsSuperuser" boolean NOT NULL DEFAULT false;
"@

Write-Host "`n=== Applying Schema Updates ===" -ForegroundColor Cyan

# Apply catalog update
Write-Host "`n[Catalog] Adding IsSuperuser to UserTenants..." -ForegroundColor Yellow
try {
    $catalogSql | & psql -h $PgHost -p $Port -U $User -d $CatalogDatabase -v ON_ERROR_STOP=1
    if ($LASTEXITCODE -ne 0) { throw "psql exited with code $LASTEXITCODE" }
    Write-Host "  OK: Catalog updated" -ForegroundColor Green
} catch {
    Write-Host "  Error: $_" -ForegroundColor Red
}

# Tenant: Re-run workflow schema for existing tenants (adds new columns via ALTER)
if (-not $CatalogOnly -and $TenantId) {
    Write-Host "`n[Tenant] Run: .\ApplyWorkflowSchemaToTenant.ps1 -TenantId $TenantId" -ForegroundColor Yellow
    Write-Host "[Tenant] For DMS StagingItems (temp indexing): Run scripts\postgres\CreateDmsSchema.sql or signup creates it." -ForegroundColor Gray
}

Write-Host "`n=== Done ===" -ForegroundColor Cyan
Write-Host "For NEW signup: Rebuild API (dotnet build), then signup. New tenants get full schema." -ForegroundColor Gray
Write-Host "For existing catalog: Run this script to add IsSuperuser." -ForegroundColor Gray
Write-Host ""
