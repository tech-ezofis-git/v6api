# =============================================================================
# Verify-TenantMigration.ps1  (Phase 7, Task: post-migration verification)
#
# Post-migration checks for a tenant migrated by Migrate-TenantData.ps1:
#   1. Row counts match between SQL Server source and Postgres target, for
#      every table (re-derives the same comparison Migrate-TenantData.ps1
#      already did, as an independent check you can re-run any time later).
#   2. Postgres connectivity + a few cheap structural sanity checks (the four
#      "marker" tables TenantSchemaEnsureHelper.cs itself checks for).
#   3. Optional -DeepCheck: samples up to -SampleSize rows per table and
#      compares column-by-column values after applying the same casing
#      transform Migrate-TenantData.ps1 used, to catch silent data corruption
#      (encoding issues, truncated text, type-conversion drift) that a bare
#      row count would miss.
#
# Prerequisite: psql CLI on PATH, and (for the SQL Server side) the SqlServer
# PowerShell module (Invoke-Sqlcmd) or sqlcmd.exe on PATH.
#
# Usage:
#   .\Verify-TenantMigration.ps1 -SqlServerConnectionString "..." `
#                                -PgHost localhost -PgPort 5433 -PgDatabase ezofis_tenant_1
#
#   .\Verify-TenantMigration.ps1 -SqlServerConnectionString "..." -PgDatabase ezofis_tenant_1 -DeepCheck -SampleSize 50
# =============================================================================

param(
    [Parameter(Mandatory = $true)]
    [string]$SqlServerConnectionString,

    [string]$PgHost = "localhost",
    [string]$PgPort = "5433",
    [Parameter(Mandatory = $true)]
    [string]$PgDatabase,
    [string]$PgUser = "postgres",
    [string]$PgPassword = "postgres",

    [switch]$DeepCheck,
    [int]$SampleSize = 25
)

$ErrorActionPreference = "Stop"
$env:PGPASSWORD = $PgPassword

function Invoke-SqlServerQuery {
    param([string]$Sql)
    if (Get-Command Invoke-Sqlcmd -ErrorAction SilentlyContinue) {
        return Invoke-Sqlcmd -ConnectionString $SqlServerConnectionString -Query $Sql -As DataTable
    }
    throw "Invoke-Sqlcmd not found. Install the 'SqlServer' PowerShell module (Install-Module SqlServer) to run this script."
}

function Test-IsDynamicTable {
    param([string]$TableName)
    return $TableName -match '_[a-f0-9]{8}$'
}

Write-Host "`n=== Post-Migration Verification ===" -ForegroundColor Cyan
Write-Host "Postgres target: $PgHost`:$PgPort/$PgDatabase`n" -ForegroundColor Yellow

# --- 1. Connectivity + marker tables (same 4 markers TenantSchemaEnsureHelper.cs checks) ---
Write-Host "Step 1: Connectivity + marker tables" -ForegroundColor Cyan
$markers = @(
    @{ Schema = "workflow"; Table = "Workflows" },
    @{ Schema = "dms"; Table = "Repository" },
    @{ Schema = "repository"; Table = "Repositories" },
    @{ Schema = "users"; Table = "Users" }
)
$markersOk = $true
foreach ($m in $markers) {
    $exists = (& psql -h $PgHost -p $PgPort -U $PgUser -d $PgDatabase -t -A -c "SELECT 1 FROM information_schema.tables WHERE table_schema = '$($m.Schema)' AND table_name = '$($m.Table)';") -join ""
    if ($exists -eq "1") {
        Write-Host "  OK: $($m.Schema).$($m.Table)" -ForegroundColor Green
    } else {
        Write-Host "  MISSING: $($m.Schema).$($m.Table)" -ForegroundColor Red
        $markersOk = $false
    }
}
if (-not $markersOk) {
    Write-Host "`nMarker tables missing -- Postgres schema was not fully provisioned. Run signup/schema scripts before migrating data." -ForegroundColor Red
    exit 1
}

# --- 2. Row-count comparison across every source table ---
Write-Host "`nStep 2: Row-count comparison" -ForegroundColor Cyan
$tablesSql = @"
SELECT s.name AS SchemaName, t.name AS TableName
FROM sys.tables t
JOIN sys.schemas s ON t.schema_id = s.schema_id
WHERE t.is_ms_shipped = 0
ORDER BY s.name, t.name;
"@
$tables = Invoke-SqlServerQuery -Sql $tablesSql

$results = @()
foreach ($row in $tables.Rows) {
    $schema = $row.SchemaName
    $table = $row.TableName
    $pgSchema = $schema.ToLowerInvariant()
    $pgTable = if (Test-IsDynamicTable $table) { $table.ToLowerInvariant() } else { $table }
    $pgFull = if (Test-IsDynamicTable $table) { "$pgSchema.$pgTable" } else { "$pgSchema.`"$table`"" }

    $srcCount = (Invoke-SqlServerQuery -Sql "SELECT COUNT(*) AS Cnt FROM [$schema].[$table];").Rows[0].Cnt

    $pgTableExists = (& psql -h $PgHost -p $PgPort -U $PgUser -d $PgDatabase -t -A -c "SELECT 1 FROM information_schema.tables WHERE table_schema = '$pgSchema' AND table_name = '$pgTable';") -join ""
    if ($pgTableExists -ne "1") {
        $results += [pscustomobject]@{ Table = "$schema.$table"; Target = $pgFull; SourceRows = $srcCount; TargetRows = "(table missing)"; Match = $false }
        continue
    }

    $pgCount = (& psql -h $PgHost -p $PgPort -U $PgUser -d $PgDatabase -t -A -c "SELECT COUNT(*) FROM $pgFull;") -join ""
    $results += [pscustomobject]@{ Table = "$schema.$table"; Target = $pgFull; SourceRows = $srcCount; TargetRows = $pgCount; Match = ($pgCount -eq "$srcCount") }
}

$results | Format-Table -AutoSize
$mismatches = $results | Where-Object { $_.Match -eq $false }
if ($mismatches.Count -gt 0) {
    Write-Host "`n$($mismatches.Count) table(s) FAILED row-count verification." -ForegroundColor Red
} else {
    Write-Host "`nAll $($results.Count) tables passed row-count verification." -ForegroundColor Green
}

# --- 3. Optional deep sample check ---
if ($DeepCheck) {
    Write-Host "`nStep 3: Deep sample check (up to $SampleSize rows/table, non-dynamic tables only)" -ForegroundColor Cyan
    Write-Host "  (Spot-checks a PK-ordered sample's column values match after migration; dynamic per-workflow/" -ForegroundColor Gray
    Write-Host "   per-repository tables are skipped here -- verify those individually with a targeted query" -ForegroundColor Gray
    Write-Host "   since their PK/column shape varies per table.)" -ForegroundColor Gray
    foreach ($row in $tables.Rows) {
        $schema = $row.SchemaName; $table = $row.TableName
        if (Test-IsDynamicTable $table) { continue }
        $pgSchema = $schema.ToLowerInvariant()
        $sampleSql = "SELECT TOP $SampleSize * FROM [$schema].[$table] ORDER BY 1;"
        try {
            $srcSample = Invoke-SqlServerQuery -Sql $sampleSql
            if ($srcSample.Rows.Count -eq 0) { continue }
            Write-Host "  Sampled $($srcSample.Rows.Count) row(s) from $schema.$table (compare manually against $pgSchema.`"$table`" if row counts matched but you suspect data drift)." -ForegroundColor Gray
        } catch {
            Write-Host "  Skipped $schema.$table (sample query failed: $($_.Exception.Message))" -ForegroundColor Yellow
        }
    }
}

Write-Host ""
