# Run Catalog DB scripts.
# Prerequisite: the psql CLI (PostgreSQL client tools) must be installed and on PATH --
# e.g. `winget install PostgreSQL.PostgreSQL` or install alongside a local Postgres server.
#
# Usage: .\RunCatalogScripts.ps1
# For a different server: .\RunCatalogScripts.ps1 -PgHost "yourhost" -Port "5432" -User "user" -Password "pass"

param(
    [string]$PgHost = "localhost",
    [string]$Port = "5433",
    [string]$User = "postgres",
    [string]$Password = "postgres",
    [string]$Database = "ezofis_catalog_new"
)

$ErrorActionPreference = "Stop"
$scriptDir = $PSScriptRoot
$pgScriptDir = Join-Path $scriptDir "postgres"
$env:PGPASSWORD = $Password

function Run-SqlFile {
    param([string]$Database, [string]$FilePath)
    & psql -h $PgHost -p $Port -U $User -d $Database -v ON_ERROR_STOP=1 -f $FilePath
    if ($LASTEXITCODE -ne 0) {
        throw "psql failed on $FilePath (exit code $LASTEXITCODE)"
    }
}

Write-Host "`n=== Catalog Database Setup ===" -ForegroundColor Cyan
Write-Host "Host: $PgHost`:$Port | Database: $Database`n" -ForegroundColor Yellow

# Step 1: Create database (connect to the default "postgres" admin database first)
Write-Host "Step 1: Creating database..." -ForegroundColor Cyan
try {
    $createDbSql = "SELECT 'CREATE DATABASE ""$Database""' WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = '$Database')\gexec"
    $createDbSql | & psql -h $PgHost -p $Port -U $User -d postgres -v ON_ERROR_STOP=1
    if ($LASTEXITCODE -ne 0) { throw "psql exited with code $LASTEXITCODE" }
    Write-Host "  OK: Database ready`n" -ForegroundColor Green
} catch {
    Write-Host "  Error: $_" -ForegroundColor Red
    exit 1
}

# Step 2: Catalog schema + tables (Tenants, UserTenants, ConnectorProviders, etc.)
Write-Host "Step 2: Creating catalog schema (01a) and Tenants/UserTenants tables (01b)..." -ForegroundColor Cyan
Run-SqlFile -Database $Database -FilePath "$pgScriptDir\01a_CreateCatalogDatabase.sql"
Run-SqlFile -Database $Database -FilePath "$pgScriptDir\01b_CreateCatalogTables.sql"
Write-Host "  OK`n" -ForegroundColor Green

# Step 3: Hangfire -- no manual SQL install step on Postgres. Hangfire.PostgreSql creates its own
# "hangfire" schema automatically on first connection (PostgreSqlStorageOptions.PrepareSchemaIfNecessary
# defaults to true), replacing the old 01c_InstallHangfire.sql SQL Server install script.
Write-Host "Step 3: Hangfire schema -- created automatically by the app on first connection (Hangfire.PostgreSql), no script needed.`n" -ForegroundColor Cyan

Write-Host "=== Done ===" -ForegroundColor Cyan
Write-Host "Catalog database is ready. Start with: dotnet run --project src/Api`n" -ForegroundColor White
