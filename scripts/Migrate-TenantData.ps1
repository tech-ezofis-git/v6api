# =============================================================================
# Migrate-TenantData.ps1  (Phase 7, Task: per-tenant schema + data migration)
#
# Copies one tenant's data from its SQL Server tenant database to an already-
# provisioned Postgres tenant database (the Postgres schema must already exist --
# run 02_CreateTenantDatabase.sql / CreateWorkflowSchemaComplete.sql /
# CreateDmsSchema.sql / dotnet ef database update against UsersDbContext first,
# same as a brand-new tenant signup does).
#
# This is a COPY, not a MOVE: the source SQL Server database is never modified
# and catalog.Tenants."ConnectionString" is only flipped to the new Postgres
# connection string when -ActivateInCatalog is passed AND verification passed.
# That is the rollback path -- if anything looks wrong, simply do not pass
# -ActivateInCatalog (or flip the catalog row back); the SQL Server tenant DB
# is still there, untouched, and the app keeps working against it exactly as
# before this script ran.
#
# Column-naming convention applied on the Postgres side (matches the rest of
# this migration -- see the casing-convention doc comment at the top of
# WorkflowTableCreator.cs for the canonical statement of this rule):
#   - Static/EF-owned schemas (workflow, users, repository, dms, activitylog,
#     catalog, support) and the legacy "dbo" v5-parity tables: PascalCase or
#     original casing, double-quoted, UNCHANGED from the SQL Server column name.
#   - Dynamic per-workflow/per-repository tables (name ends in an 8-hex-char
#     suffix, e.g. transaction_a1b2c3d4, Items_a1b2c3d4): reserved/system
#     columns go to snake_case (Id -> id, TenantId -> tenant_id, IsDeleted ->
#     is_deleted, ...); anything not in the known reserved-column list is
#     treated as a user-defined custom column and keeps its exact original
#     casing (double-quoted) -- this mirrors RepositorySqlHelper.cs /
#     RepositoryItemTableColumns.cs exactly.
#
# Prerequisites: psql CLI on PATH (Postgres side), sqlcmd CLI on PATH (SQL
# Server side) OR PowerShell's SqlServer module for Invoke-Sqlcmd. This script
# shells out to both rather than loading Npgsql/SqlClient assemblies directly,
# for the same "avoid fragile Add-Type assembly loading" reasoning documented
# in RunCatalogScripts.ps1's port.
#
# Usage:
#   .\Migrate-TenantData.ps1 -SqlServerConnectionString "Server=...;Database=...;User Id=...;Password=...;" `
#                             -PgHost localhost -PgPort 5433 -PgDatabase ezofis_tenant_1 -PgUser postgres -PgPassword postgres `
#                             -WhatIf
#
#   .\Migrate-TenantData.ps1 -SqlServerConnectionString "..." -PgHost ... -PgDatabase ... `
#                             -TenantId "3fa85f64-...-...-...-..." -ActivateInCatalog
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

    # When set, flips catalog.Tenants."ConnectionString" to the Postgres connection string for
    # this tenant AFTER a successful copy + row-count verification. Requires -TenantId and
    # -CatalogPgDatabase. Omit this switch to do a copy-only rehearsal (default; safest).
    [switch]$ActivateInCatalog,
    [string]$TenantId = "",
    [string]$CatalogPgDatabase = "ezofis_catalog_new",

    # Schemas whose tables keep their exact SQL Server column names (double-quoted) on the
    # Postgres side -- static/EF-owned schema plus the legacy "dbo" v5-parity tables.
    [string[]]$StaticSchemas = @("workflow", "users", "repository", "dms", "activitylog", "support", "dbo"),

    [switch]$WhatIf
)

$ErrorActionPreference = "Stop"
$env:PGPASSWORD = $PgPassword

# Reserved/system columns on dynamic per-workflow/per-repository tables that get snake_case
# names on Postgres. Anything else on a dynamic table is a user-defined custom column and keeps
# its original casing. This list mirrors WorkflowTableCreator.cs / RepositorySqlHelper.cs.
$ReservedColumnMap = @{
    "Id" = "id"; "TenantId" = "tenant_id"; "WorkflowId" = "workflow_id"; "WorkflowInstanceId" = "workflow_instance_id"
    "RepositoryId" = "repository_id"; "ItemId" = "item_id"; "StepInstanceId" = "step_instance_id"
    "ActivityId" = "activity_id"; "RuleId" = "rule_id"; "StageType" = "stage_type"; "StageName" = "stage_name"
    "Review" = "review"; "ActionStatus" = "action_status"; "ActivityUserId" = "activity_user_id"
    "ActivityGroupId" = "activity_group_id"; "TransactionGuid" = "transaction_guid"; "UserIds" = "user_ids"
    "GroupIds" = "group_ids"; "SlaTransactionId" = "sla_transaction_id"; "CreatedAt" = "created_at"
    "CreatedAtUtc" = "created_at_utc"; "CreatedBy" = "created_by"; "ModifiedAt" = "modified_at"
    "ModifiedAtUtc" = "modified_at_utc"; "ModifiedBy" = "modified_by"; "IsDeleted" = "is_deleted"
    "Status" = "status"; "WorkflowName" = "workflow_name"; "WorkflowVersion" = "workflow_version"
    "StartedAtUtc" = "started_at_utc"; "StartedBy" = "started_by"; "CompletedAtUtc" = "completed_at_utc"
    "ReferenceNumber" = "reference_number"; "Priority" = "priority"; "ViewCount" = "view_count"
    "IsArchived" = "is_archived"; "FormJsonId" = "form_json_id"; "FileName" = "file_name"; "FilePath" = "file_path"
    "FileSize" = "file_size"; "ContentType" = "content_type"; "Comments" = "comments"
    "ExternalCommentsBy" = "external_comments_by"; "ShowTo" = "show_to"; "EmbedJson" = "embed_json"
    "EmbedStatus" = "embed_status"; "WFormId" = "w_form_id"; "FormEntryId" = "form_entry_id"
    "FormData" = "form_data"; "HasFormPdf" = "has_form_pdf"
}

function Test-IsDynamicTable {
    param([string]$TableName)
    return $TableName -match '_[a-f0-9]{8}$'
}

function Get-PgColumnName {
    param([string]$Schema, [string]$TableName, [string]$SourceColumn)
    if ($StaticSchemas -contains $Schema -and -not (Test-IsDynamicTable $TableName)) {
        return '"' + $SourceColumn + '"'
    }
    if (Test-IsDynamicTable $TableName) {
        if ($ReservedColumnMap.ContainsKey($SourceColumn)) {
            return $ReservedColumnMap[$SourceColumn]
        }
        return '"' + $SourceColumn + '"'   # custom/user-defined column: keep original casing
    }
    return '"' + $SourceColumn + '"'
}

function Invoke-SqlServerQuery {
    param([string]$Sql)
    # Requires either the SqlServer module (Invoke-Sqlcmd) or sqlcmd.exe on PATH.
    if (Get-Command Invoke-Sqlcmd -ErrorAction SilentlyContinue) {
        return Invoke-Sqlcmd -ConnectionString $SqlServerConnectionString -Query $Sql -As DataTable
    }
    throw "Invoke-Sqlcmd not found. Install the 'SqlServer' PowerShell module (Install-Module SqlServer) to run this script."
}

Write-Host "`n=== Tenant Data Migration: SQL Server -> Postgres ===" -ForegroundColor Cyan
Write-Host "Target: $PgHost`:$PgPort/$PgDatabase" -ForegroundColor Yellow
if ($WhatIf) { Write-Host "Mode: WhatIf (dry run, no writes)" -ForegroundColor Gray }

# 1) Enumerate source tables (SQL Server side)
$tablesSql = @"
SELECT s.name AS SchemaName, t.name AS TableName
FROM sys.tables t
JOIN sys.schemas s ON t.schema_id = s.schema_id
WHERE t.is_ms_shipped = 0
ORDER BY s.name, t.name;
"@
$tables = Invoke-SqlServerQuery -Sql $tablesSql
Write-Host "`nFound $($tables.Rows.Count) source tables.`n" -ForegroundColor Cyan

$results = @()

foreach ($row in $tables.Rows) {
    $schema = $row.SchemaName
    $table = $row.TableName
    $srcFull = "[$schema].[$table]"

    # Column list + row count from source
    $colsSql = "SELECT COLUMN_NAME, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = '$schema' AND TABLE_NAME = '$table' ORDER BY ORDINAL_POSITION;"
    $cols = Invoke-SqlServerQuery -Sql $colsSql
    if ($cols.Rows.Count -eq 0) { continue }

    $countSql = "SELECT COUNT(*) AS Cnt FROM $srcFull;"
    $srcCount = (Invoke-SqlServerQuery -Sql $countSql).Rows[0].Cnt

    $pgTable = if (Test-IsDynamicTable $table) { $table.ToLowerInvariant() } else { $table }
    $pgSchema = $schema.ToLowerInvariant()
    $pgFull = if ($StaticSchemas -contains $pgSchema -and -not (Test-IsDynamicTable $table)) {
        "$pgSchema.`"$table`""
    } else {
        "$pgSchema.$pgTable"
    }

    Write-Host "[$srcFull] -> [$pgFull]  ($srcCount rows)" -ForegroundColor White

    if ($WhatIf) {
        $results += [pscustomobject]@{ Table = $srcFull; Target = $pgFull; SourceRows = $srcCount; CopiedRows = "(dry run)"; Match = "n/a" }
        continue
    }

    # 2) Export source rows as CSV, then COPY into Postgres via psql \copy (handles quoting/escaping
    #    consistently and avoids a custom Npgsql binary-import implementation here).
    $pgColNames = ($cols.Rows | ForEach-Object { Get-PgColumnName -Schema $pgSchema -TableName $table -SourceColumn $_.COLUMN_NAME }) -join ", "
    $srcColNames = ($cols.Rows | ForEach-Object { "[$($_.COLUMN_NAME)]" }) -join ", "

    $tmpCsv = [System.IO.Path]::GetTempFileName()
    try {
        $exportSql = "SET NOCOUNT ON; SELECT $srcColNames FROM $srcFull;"
        $dt = Invoke-SqlServerQuery -Sql $exportSql
        $dt | Export-Csv -Path $tmpCsv -NoTypeInformation -Encoding UTF8

        if ($dt.Rows.Count -gt 0) {
            $copySql = "\copy $pgFull ($pgColNames) FROM '$tmpCsv' WITH (FORMAT csv, HEADER true, NULL '')"
            $copySql | & psql -h $PgHost -p $PgPort -U $PgUser -d $PgDatabase -v ON_ERROR_STOP=1
            if ($LASTEXITCODE -ne 0) { throw "psql \copy failed for $pgFull" }
        }
    } finally {
        Remove-Item $tmpCsv -ErrorAction SilentlyContinue
    }

    # 3) Verify row count
    $pgCount = (& psql -h $PgHost -p $PgPort -U $PgUser -d $PgDatabase -t -A -c "SELECT COUNT(*) FROM $pgFull;") -join ""
    $match = ($pgCount -eq "$srcCount")
    $results += [pscustomobject]@{ Table = $srcFull; Target = $pgFull; SourceRows = $srcCount; CopiedRows = $pgCount; Match = $match }
    if (-not $match) {
        Write-Host "  MISMATCH: source=$srcCount postgres=$pgCount" -ForegroundColor Red
    }
}

Write-Host "`n=== Migration Summary ===" -ForegroundColor Cyan
$results | Format-Table -AutoSize
$mismatches = $results | Where-Object { $_.Match -eq $false }
if ($mismatches.Count -gt 0) {
    Write-Host "`n$($mismatches.Count) table(s) with row-count mismatch. Review before activating this tenant on Postgres." -ForegroundColor Red
} elseif (-not $WhatIf) {
    Write-Host "`nAll tables copied with matching row counts." -ForegroundColor Green
}

# 4) Optionally flip the catalog connection string (the only "point of no return" step -- and
#    even this is reversible by flipping it back, since the SQL Server tenant DB is untouched).
if ($ActivateInCatalog -and -not $WhatIf) {
    if ($mismatches.Count -gt 0) {
        Write-Host "`nRefusing to activate in catalog: row-count mismatches present." -ForegroundColor Red
        exit 1
    }
    if (-not $TenantId) {
        Write-Host "`n-ActivateInCatalog requires -TenantId." -ForegroundColor Red
        exit 1
    }
    $newConnString = "Host=$PgHost;Port=$PgPort;Database=$PgDatabase;Username=$PgUser;Password=$PgPassword;Pooling=true"
    $updateSql = "UPDATE catalog.""Tenants"" SET ""ConnectionString"" = '$newConnString' WHERE ""Id"" = '$TenantId';"
    $updateSql | & psql -h $PgHost -p $PgPort -U $PgUser -d $CatalogPgDatabase -v ON_ERROR_STOP=1
    if ($LASTEXITCODE -ne 0) { throw "Failed to update catalog.Tenants.ConnectionString" }
    Write-Host "`nActivated: catalog.Tenants[$TenantId].ConnectionString now points at Postgres." -ForegroundColor Green
    Write-Host "Rollback: re-run the equivalent UPDATE with the original SQL Server connection string to revert." -ForegroundColor Gray
}

Write-Host ""
