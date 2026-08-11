-- =============================================
-- Create DMS Repository Items Table (per-repository) -- Postgres
-- Ported from scripts/CreateDmsRepositoryItemsTable.sql -- Phase 3.
-- Run when creating a new repository. Replace the suffix below with the repo code
-- (e.g. 'ezca_156'). Table structure: Year/InvoiceType/VendorName/FileName for folder
-- archive path.
--
-- Uses \gexec, same as CreateLegacyTransactionTable_Manual.sql -- psql's :'var'
-- substitution does not apply inside DO $$ ... $$ bodies (confirmed by testing
-- earlier in this phase), so the dynamic per-suffix table name is built via a query
-- result executed with \gexec instead.
-- =============================================

\set suffix 'sample'

SELECT format($fmt$
    CREATE TABLE dms.%I (
        "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
        "TenantId" uuid NOT NULL,
        "RepositoryId" uuid NOT NULL,
        "Year" smallint NOT NULL,
        "InvoiceType" varchar(64) NOT NULL,
        "VendorName" varchar(256) NOT NULL,
        "FileName" varchar(512) NOT NULL,
        "Status" smallint NOT NULL DEFAULT 0,
        "SignStatus" smallint NOT NULL DEFAULT 0,
        "CreatedAt" timestamptz NOT NULL DEFAULT now(),
        "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
        "CreatedBy" uuid NOT NULL,
        "UpdatedBy" uuid NULL,
        "IsDeleted" boolean NOT NULL DEFAULT false,
        "DeletedAt" timestamptz NULL,
        "Version" integer NOT NULL DEFAULT 1,
        "WorkflowInstanceId" uuid NULL,
        "ReportNo" varchar(128) NULL,
        "ReferenceNo" varchar(64) NULL
    )
$fmt$, :'suffix' || '_items')
WHERE NOT EXISTS (
    SELECT 1 FROM information_schema.tables
    WHERE table_schema = 'dms' AND table_name = :'suffix' || '_items'
)
\gexec

SELECT format(
    'CREATE INDEX %I ON dms.%I ("RepositoryId", "IsDeleted", "Year", "InvoiceType", "VendorName")',
    'IX_' || :'suffix' || '_Folder', :'suffix' || '_items')
WHERE EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'dms' AND table_name = :'suffix' || '_items')
  AND NOT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'dms' AND indexname = 'IX_' || :'suffix' || '_Folder')
\gexec

SELECT format(
    'CREATE INDEX %I ON dms.%I ("WorkflowInstanceId") WHERE "WorkflowInstanceId" IS NOT NULL',
    'IX_' || :'suffix' || '_Workflow', :'suffix' || '_items')
WHERE EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'dms' AND table_name = :'suffix' || '_items')
  AND NOT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'dms' AND indexname = 'IX_' || :'suffix' || '_Workflow')
\gexec
