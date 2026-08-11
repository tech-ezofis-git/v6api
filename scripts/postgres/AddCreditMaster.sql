-- =============================================
-- Catalog DB: dbo.creditMaster -- Postgres
-- Ported from scripts/AddCreditMaster.sql -- Phase 3.
-- Run against catalog database (ezofis_catalog_*).
--
-- EXACT-MATCH REQUIREMENT: table name/case/schema must be dbo."creditMaster"
-- (mixed-case, not snake_case) to match CatalogDbContext.cs's ToTable("creditMaster",
-- "dbo", t => t.ExcludeFromMigrations()) (Phase 2) -- that table is script-owned,
-- never EF-owned, so this script is the only thing that creates it on Postgres.
-- =============================================

CREATE SCHEMA IF NOT EXISTS dbo;

CREATE TABLE IF NOT EXISTS dbo."creditMaster" (
    "id" bigint GENERATED ALWAYS AS IDENTITY,
    "tenantId" uuid NOT NULL,
    "allocationMonth" integer NOT NULL,
    "allocationYear" integer NOT NULL,
    "creditType" varchar(100) NULL,
    "initialCredit" integer NOT NULL DEFAULT 0,
    "balanceCredit" integer NOT NULL DEFAULT 0,
    "remarks" varchar(500) NULL,
    "createdAt" timestamptz NULL,
    "modifiedAt" timestamptz NULL,
    "createdBy" varchar(50) NULL,
    "modifiedBy" varchar(50) NULL,
    "isDeleted" boolean NOT NULL DEFAULT false,
    "ValidFrom" timestamptz NULL,
    "ValidTo" timestamptz NULL,
    "parentAllocationId" integer NULL,
    "subscriptionType" varchar(100) NULL,
    "validFromDate" timestamptz NULL,
    "validToDate" timestamptz NULL,
    "isCarryForward" boolean NULL,
    "priority" integer NULL,
    "status" varchar(50) NULL,
    "carryForwardCredit" integer NULL,
    "extraConsumedCredit" integer NULL,
    "topUpBalanceCredit" integer NULL,
    "overallConsumedCredit" integer NULL,
    CONSTRAINT "PK_creditMaster" PRIMARY KEY ("id")
);

CREATE INDEX IF NOT EXISTS "IX_creditMaster_Tenant_Period"
    ON dbo."creditMaster" ("tenantId", "allocationYear", "allocationMonth", "creditType");

-- =============================================
-- Backfill default credit allocation for EXISTING tenants
-- Seeds one creditMaster row per tenant for the CURRENT (IST) month/year.
-- Idempotent: skips tenants that already have a row for the period + creditType.
-- Adjust the constants below to match your desired allocation.
--
-- SQL Server's `... AT TIME ZONE 'UTC' AT TIME ZONE 'India Standard Time'` uses a
-- Windows timezone name; Postgres uses IANA names, so the equivalent is
-- `now() AT TIME ZONE 'Asia/Kolkata'` (a timestamptz AT TIME ZONE a zone name yields
-- the local wall-clock time in that zone, as a plain timestamp -- same semantics as
-- the SQL Server double-AT-TIME-ZONE conversion).
-- =============================================
DO $$
DECLARE
    default_initial_credit  integer := 1000;
    default_credit_type     varchar(100) := 'Standard';
    default_subscription    varchar(100) := 'Trial';
    default_status          varchar(50) := 'Active';
    default_remarks         varchar(500) := 'Default allocation backfill for existing tenant';
    default_valid_days      integer := 0;  -- 0 = no expiry

    now_utc   timestamptz := now();
    now_ist   timestamp := now() AT TIME ZONE 'Asia/Kolkata';
    v_month   integer := EXTRACT(MONTH FROM now_ist);
    v_year    integer := EXTRACT(YEAR FROM now_ist);
    valid_to  timestamptz := CASE WHEN default_valid_days > 0 THEN now_utc + (default_valid_days || ' days')::interval ELSE NULL END;
    rows_inserted integer;
BEGIN
    INSERT INTO dbo."creditMaster"
        ("tenantId", "allocationMonth", "allocationYear", "creditType",
         "initialCredit", "balanceCredit", "overallConsumedCredit",
         "subscriptionType", "status", "remarks",
         "createdAt", "createdBy", "isDeleted", "validFromDate", "validToDate")
    SELECT
        t."Id", v_month, v_year, default_credit_type,
        default_initial_credit, default_initial_credit, 0,
        default_subscription, default_status, default_remarks,
        now_utc, 'system', false, now_utc, valid_to
    FROM catalog."Tenants" t
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo."creditMaster" cm
        WHERE cm."tenantId" = t."Id"
          AND cm."allocationMonth" = v_month
          AND cm."allocationYear" = v_year
          AND cm."creditType" = default_credit_type
          AND cm."isDeleted" = false
    );
    GET DIAGNOSTICS rows_inserted = ROW_COUNT;
    RAISE NOTICE 'creditMaster backfill: % tenant row(s) inserted for %/%', rows_inserted, v_month, v_year;
END $$;
