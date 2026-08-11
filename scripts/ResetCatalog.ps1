# Reset Catalog Database - Drop and recreate for fresh start
# Use when you want to remove all tenants and start over.
# WARNING: This deletes all tenant registrations. Tenant DBs are NOT dropped.
# Prerequisite: the psql CLI (PostgreSQL client tools) must be installed and on PATH.
#
# Usage: .\ResetCatalog.ps1 -FromAppSettings
#        .\ResetCatalog.ps1 -PgHost "localhost" -Port "5433" -Database "ezofis_catalog_new" -User "postgres" -Password "postgres"

param(
    [string]$PgHost = "localhost",
    [string]$Port = "5433",
    [string]$Database = "ezofis_catalog_new",
    [string]$User = "postgres",
    [string]$Password = "postgres",
    [switch]$FromAppSettings,
    [switch]$WhatIf
)

$ErrorActionPreference = "Stop"
$scriptDir = $PSScriptRoot
$pgScriptDir = Join-Path $scriptDir "postgres"

# Load from appsettings.Development.json if requested
if ($FromAppSettings) {
    $appSettingsPath = Join-Path (Split-Path $scriptDir -Parent) "src\Api\appsettings.Development.json"
    if (-not (Test-Path $appSettingsPath)) { $appSettingsPath = Join-Path $scriptDir "..\src\Api\appsettings.Development.json" }
    if (Test-Path $appSettingsPath) {
        $json = Get-Content $appSettingsPath -Raw | ConvertFrom-Json
        $cs = $json.ConnectionStrings.DefaultConnection
        if ($cs -match "Host=([^;]+)") { $PgHost = $Matches[1].Trim() }
        if ($cs -match "Port=([^;]+)") { $Port = $Matches[1].Trim() }
        if ($cs -match "Database=([^;]+)") { $Database = $Matches[1].Trim() }
        if ($cs -match "Username=([^;]+)") { $User = $Matches[1].Trim() }
        if ($cs -match "Password=([^;]+)") { $Password = $Matches[1].Trim() }
        Write-Host "Loaded from appsettings.Development.json: Host=$PgHost, Port=$Port, Database=$Database" -ForegroundColor Gray
    } else {
        Write-Host "appsettings.Development.json not found at $appSettingsPath" -ForegroundColor Red
        exit 1
    }
}

$env:PGPASSWORD = $Password

Write-Host "`n=== Reset Catalog Database ===" -ForegroundColor Cyan
Write-Host "Host: $PgHost`:$Port | Database: $Database" -ForegroundColor Yellow
if ($WhatIf) {
    Write-Host "WhatIf: Would drop and recreate catalog. Run without -WhatIf to execute." -ForegroundColor Gray
    exit 0
}

Write-Host "`nWARNING: This will DROP the catalog database and recreate it." -ForegroundColor Red
Write-Host "All tenant registrations will be lost. Tenant databases are NOT dropped.`n" -ForegroundColor Yellow

$confirm = Read-Host "Type 'yes' to continue"
if ($confirm -ne "yes") {
    Write-Host "Aborted." -ForegroundColor Gray
    exit 0
}

try {
    # Terminate active connections then drop (Postgres equivalent of
    # ALTER DATABASE ... SET SINGLE_USER WITH ROLLBACK IMMEDIATE), then recreate.
    $dropSql = @"
SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$Database' AND pid <> pg_backend_pid();
DROP DATABASE IF EXISTS "$Database";
CREATE DATABASE "$Database";
"@
    $dropSql | & psql -h $PgHost -p $Port -U $User -d postgres -v ON_ERROR_STOP=1
    if ($LASTEXITCODE -ne 0) { throw "psql exited with code $LASTEXITCODE" }
    Write-Host "  OK: Database dropped and recreated" -ForegroundColor Green
} catch {
    Write-Host "  FAIL: $_" -ForegroundColor Red
    exit 1
}

# Apply catalog schema (01a) and tables (01b)
Write-Host "`nApplying catalog schema (01a_CreateCatalogDatabase.sql)..." -ForegroundColor Cyan
& psql -h $PgHost -p $Port -U $User -d $Database -v ON_ERROR_STOP=1 -f "$pgScriptDir\01a_CreateCatalogDatabase.sql"
if ($LASTEXITCODE -eq 0) { Write-Host "  OK: Catalog schema created" -ForegroundColor Green } else { Write-Host "  Run: psql -h $PgHost -p $Port -U $User -d $Database -f scripts\postgres\01a_CreateCatalogDatabase.sql" -ForegroundColor Yellow }

Write-Host "`nApplying catalog tables (01b_CreateCatalogTables.sql)..." -ForegroundColor Cyan
& psql -h $PgHost -p $Port -U $User -d $Database -v ON_ERROR_STOP=1 -f "$pgScriptDir\01b_CreateCatalogTables.sql"
if ($LASTEXITCODE -eq 0) { Write-Host "  OK: Catalog tables created" -ForegroundColor Green } else { Write-Host "  Run: psql -h $PgHost -p $Port -U $User -d $Database -f scripts\postgres\01b_CreateCatalogTables.sql" -ForegroundColor Yellow }

# Hangfire -- no manual SQL install step on Postgres; Hangfire.PostgreSql creates its own "hangfire"
# schema automatically on first connection (replaces the old 01c_InstallHangfire.sql SQL Server step).
Write-Host "`nHangfire schema: created automatically by the app on first connection (Hangfire.PostgreSql), no script needed." -ForegroundColor Cyan

Write-Host "`n=== Catalog Reset Complete ===" -ForegroundColor Cyan
Write-Host "Next: Use Signup API to create new tenants. Workflow + DMS schema created automatically on signup.`n" -ForegroundColor Gray
