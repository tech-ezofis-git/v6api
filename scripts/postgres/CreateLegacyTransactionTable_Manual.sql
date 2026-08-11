/*
  Manual recreate: workflow.transaction_{suffix} only (no process_* / processForm_* tables). -- Postgres
  Ported from scripts/CreateLegacyTransactionTable_Manual.sql -- Phase 3.
  Used by start-workflow and move-next APIs.

  HOW TO GET {suffix}:
    Take your WorkflowId GUID, remove hyphens, use first 8 characters (lowercase).
    Example: A4D9EB06-1ECC-468A-8AAE-5A9B46C9E6E2  ->  a4d9eb06

  RUN ON: tenant database (e.g. ezofis_tenant_7), NOT catalog. Connect to the right
  database with `\c ezofis_tenant_7` (or psql -d) before running this file -- unlike
  the SQL Server original there's no in-file USE statement to edit; Postgres has no
  cross-database USE.

  Uses this session's one legacy INT IDENTITY column, ported per Decision 1's guidance
  as GENERATED ALWAYS AS IDENTITY rather than uuid, since this table's Id genuinely
  needs to stay a sequential integer (matches what SQL Server's IDENTITY(1,1) did).

  Edit the suffix below, then run this whole file.

  Uses \gexec (build the DDL as a query result, then execute it) rather than a
  DO $$ ... $$ block: psql's :'var' substitution does NOT apply inside dollar-quoted
  bodies -- confirmed by testing, the first version of this script errored with
  "syntax error at or near ':'" when :'suffix' was used inside DO $$ ... $$.
*/

\set suffix 'a4d9eb06'

DROP TABLE IF EXISTS workflow.transaction_:suffix;

SELECT format($fmt$
    CREATE TABLE workflow.%I (
        "Id" bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
        "TransactionGuid" uuid NULL,
        "WorkflowInstanceId" uuid NOT NULL,
        "ActivityId" varchar(128) NULL,
        "RuleId" varchar(128) NULL,
        "StageType" varchar(64) NULL,
        "StageName" varchar(256) NULL,
        "Review" varchar(64) NULL,
        "ActionStatus" integer NOT NULL DEFAULT 0,
        "ActivityUserId" uuid NULL,
        "ActivityGroupId" integer NULL,
        "UserIds" text NULL,
        "GroupIds" text NULL,
        "SlaTransactionId" integer NULL,
        "InputFrom" varchar(64) NULL,
        "LevelId" integer NULL,
        "UserType" varchar(64) NULL,
        "JiraIssueJson" text NULL,
        "MlPrediction" text NULL,
        "MlCondition" text NULL,
        "TicketLockedBy" uuid NULL,
        "CreatedAt" timestamptz NOT NULL DEFAULT now(),
        "CreatedBy" uuid NULL,
        "ModifiedAt" timestamptz NULL,
        "ModifiedBy" uuid NULL,
        "IsDeleted" boolean NOT NULL DEFAULT false
    )
$fmt$, 'transaction_' || :'suffix')
WHERE NOT EXISTS (
    SELECT 1 FROM information_schema.tables
    WHERE table_schema = 'workflow' AND table_name = 'transaction_' || :'suffix'
)
\gexec

SELECT format(
    'CREATE INDEX %I ON workflow.%I ("WorkflowInstanceId", "IsDeleted")',
    'IX_transaction_' || :'suffix' || '_WorkflowInstanceId_IsDeleted', 'transaction_' || :'suffix')
WHERE EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'workflow' AND table_name = 'transaction_' || :'suffix')
  AND NOT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'workflow' AND indexname = 'IX_transaction_' || :'suffix' || '_WorkflowInstanceId_IsDeleted')
\gexec

SELECT format(
    'CREATE INDEX %I ON workflow.%I ("ActivityUserId", "ActionStatus")',
    'IX_transaction_' || :'suffix' || '_ActivityUser_ActionStatus', 'transaction_' || :'suffix')
WHERE EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'workflow' AND table_name = 'transaction_' || :'suffix')
  AND NOT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'workflow' AND indexname = 'IX_transaction_' || :'suffix' || '_ActivityUser_ActionStatus')
\gexec

SELECT format(
    'CREATE UNIQUE INDEX %I ON workflow.%I ("TransactionGuid") WHERE "TransactionGuid" IS NOT NULL',
    'IX_transaction_' || :'suffix' || '_TransactionGuid', 'transaction_' || :'suffix')
WHERE EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'workflow' AND table_name = 'transaction_' || :'suffix')
  AND NOT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'workflow' AND indexname = 'IX_transaction_' || :'suffix' || '_TransactionGuid')
\gexec
