-- =============================================
-- Catalog DB: dbo.creditMaster
-- Run against catalog database (ezofis_catalog_*)
-- =============================================

IF OBJECT_ID(N'[dbo].[creditMaster]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[creditMaster] (
        [id] INT IDENTITY(1,1) NOT NULL,
        [tenantId] UNIQUEIDENTIFIER NOT NULL,
        [allocationMonth] INT NOT NULL,
        [allocationYear] INT NOT NULL,
        [creditType] NVARCHAR(100) NULL,
        [initialCredit] INT NOT NULL DEFAULT 0,
        [balanceCredit] INT NOT NULL DEFAULT 0,
        [remarks] NVARCHAR(500) NULL,
        [createdAt] DATETIME2 NULL,
        [modifiedAt] DATETIME2 NULL,
        [createdBy] NVARCHAR(50) NULL,
        [modifiedBy] NVARCHAR(50) NULL,
        [isDeleted] BIT NOT NULL DEFAULT 0,
        [ValidFrom] DATETIME2 NULL,
        [ValidTo] DATETIME2 NULL,
        [parentAllocationId] INT NULL,
        [subscriptionType] NVARCHAR(100) NULL,
        [validFromDate] DATETIME2 NULL,
        [validToDate] DATETIME2 NULL,
        [isCarryForward] BIT NULL,
        [priority] INT NULL,
        [status] NVARCHAR(50) NULL,
        [carryForwardCredit] INT NULL,
        [extraConsumedCredit] INT NULL,
        [topUpBalanceCredit] INT NULL,
        [overallConsumedCredit] INT NULL,
        CONSTRAINT [PK_creditMaster] PRIMARY KEY CLUSTERED ([id] ASC)
    );

    CREATE INDEX [IX_creditMaster_Tenant_Period]
        ON [dbo].[creditMaster] ([tenantId], [allocationYear], [allocationMonth], [creditType]);

    PRINT '✓ dbo.creditMaster created';
END
ELSE
BEGIN
    PRINT '✓ dbo.creditMaster already exists';

    IF COL_LENGTH(N'dbo.creditMaster', N'tenantId') IS NOT NULL
       AND (SELECT DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = N'dbo' AND TABLE_NAME = N'creditMaster' AND COLUMN_NAME = N'tenantId') <> 'uniqueidentifier'
    BEGIN
        PRINT 'NOTE: existing dbo.creditMaster.tenantId is not UNIQUEIDENTIFIER. V6 API expects UNIQUEIDENTIFIER tenant ids.';
    END
END
GO

-- =============================================
-- Backfill default credit allocation for EXISTING tenants
-- Seeds one creditMaster row per tenant for the CURRENT (IST) month/year.
-- Idempotent: skips tenants that already have a row for the period + creditType.
-- Adjust the @Default* variables below to match your desired allocation.
-- =============================================
DECLARE @DefaultInitialCredit   INT           = 1000;
DECLARE @DefaultCreditType      NVARCHAR(100) = N'Standard';
DECLARE @DefaultSubscription    NVARCHAR(100) = N'Trial';
DECLARE @DefaultStatus          NVARCHAR(50)  = N'Active';
DECLARE @DefaultRemarks         NVARCHAR(500) = N'Default allocation backfill for existing tenant';
DECLARE @DefaultValidDays       INT           = 0;   -- 0 = no expiry

DECLARE @NowUtc  DATETIME2 = SYSUTCDATETIME();
DECLARE @NowIst  DATETIME2 = CAST(@NowUtc AT TIME ZONE 'UTC' AT TIME ZONE 'India Standard Time' AS DATETIME2);
DECLARE @Month   INT = MONTH(@NowIst);
DECLARE @Year    INT = YEAR(@NowIst);
DECLARE @ValidTo DATETIME2 = CASE WHEN @DefaultValidDays > 0 THEN DATEADD(DAY, @DefaultValidDays, @NowUtc) ELSE NULL END;

INSERT INTO [dbo].[creditMaster]
(
    [tenantId], [allocationMonth], [allocationYear], [creditType],
    [initialCredit], [balanceCredit], [overallConsumedCredit],
    [subscriptionType], [status], [remarks],
    [createdAt], [createdBy], [isDeleted], [validFromDate], [validToDate]
)
SELECT
    t.[Id], @Month, @Year, @DefaultCreditType,
    @DefaultInitialCredit, @DefaultInitialCredit, 0,
    @DefaultSubscription, @DefaultStatus, @DefaultRemarks,
    @NowUtc, N'system', 0, @NowUtc, @ValidTo
FROM [catalog].[Tenants] t
WHERE NOT EXISTS (
    SELECT 1 FROM [dbo].[creditMaster] cm
    WHERE cm.[tenantId] = t.[Id]
      AND cm.[allocationMonth] = @Month
      AND cm.[allocationYear] = @Year
      AND cm.[creditType] = @DefaultCreditType
      AND cm.[isDeleted] = 0
);

PRINT CONCAT('✓ creditMaster backfill: ', @@ROWCOUNT, ' tenant row(s) inserted for ', @Month, '/', @Year);
GO
