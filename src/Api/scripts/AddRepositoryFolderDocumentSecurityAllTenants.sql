-- =============================================
-- Repository Folder + Document Security tables
-- for ALL registered tenants in catalog.Tenants
--
-- Run against: CATALOG database (ezofis_catalog_*)
-- Safe to re-run (idempotent per tenant)
--
-- Fix notes:
--   - Uses USE [tenantDb] + two-part names (avoids broken multipart EXEC)
--   - Builds NVARCHAR(MAX) carefully (avoids 4000-char truncation)
--
-- Creates on each tenant DB:
--   repository.FolderSecurityPolicies
--   repository.FolderSecurityPrincipals
--   repository.DocumentSecurityRules
--   repository.DocumentSecurityPrincipals
-- =============================================

SET NOCOUNT ON;

DECLARE @TenantId         UNIQUEIDENTIFIER;
DECLARE @TenantName       NVARCHAR(256);
DECLARE @ConnectionString NVARCHAR(1024);
DECLARE @DbName           NVARCHAR(256);
DECLARE @SafeDb           NVARCHAR(256);
DECLARE @Sql              NVARCHAR(MAX);
DECLARE @Applied          INT = 0;
DECLARE @Skipped          INT = 0;
DECLARE @Failed           INT = 0;

DECLARE tenant_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT [Id], [Name], [ConnectionString]
    FROM [catalog].[Tenants]
    WHERE [IsActive] = 1
      AND [ConnectionString] IS NOT NULL
      AND LTRIM(RTRIM([ConnectionString])) <> N''
    ORDER BY [Name];

OPEN tenant_cursor;
FETCH NEXT FROM tenant_cursor INTO @TenantId, @TenantName, @ConnectionString;

WHILE @@FETCH_STATUS = 0
BEGIN
    SET @DbName = NULL;

    IF @ConnectionString LIKE N'%Initial Catalog=%'
    BEGIN
        SET @DbName = SUBSTRING(
            @ConnectionString,
            CHARINDEX(N'Initial Catalog=', @ConnectionString) + LEN(N'Initial Catalog='),
            1024);
        SET @DbName = LTRIM(RTRIM(LEFT(
            @DbName,
            CASE WHEN CHARINDEX(N';', @DbName) > 0 THEN CHARINDEX(N';', @DbName) - 1 ELSE LEN(@DbName) END)));
    END
    ELSE IF @ConnectionString LIKE N'%Database=%'
    BEGIN
        SET @DbName = SUBSTRING(
            @ConnectionString,
            CHARINDEX(N'Database=', @ConnectionString) + LEN(N'Database='),
            1024);
        SET @DbName = LTRIM(RTRIM(LEFT(
            @DbName,
            CASE WHEN CHARINDEX(N';', @DbName) > 0 THEN CHARINDEX(N';', @DbName) - 1 ELSE LEN(@DbName) END)));
    END

    IF @DbName IS NULL OR @DbName = N''
    BEGIN
        SET @Skipped += 1;
        PRINT CONCAT(N'⊘ SKIP  ', @TenantName, N' (', @TenantId, N') — could not parse database name from ConnectionString');
        GOTO NextTenant;
    END

    IF DB_ID(@DbName) IS NULL
    BEGIN
        SET @Skipped += 1;
        PRINT CONCAT(N'⊘ SKIP  ', @TenantName, N' — database not found: [', @DbName, N']');
        GOTO NextTenant;
    END

    SET @SafeDb = REPLACE(@DbName, N']', N']]');

    BEGIN TRY
        -- CAST each piece to NVARCHAR(MAX) so concatenation is not truncated at 4000 chars.
        SET @Sql = CAST(N'' AS NVARCHAR(MAX))
            + CAST(N'USE [' AS NVARCHAR(MAX)) + @SafeDb + CAST(N'];
' AS NVARCHAR(MAX))
            + CAST(N'
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N''repository'')
    EXEC(N''CREATE SCHEMA [repository]'');

IF NOT EXISTS (
    SELECT 1 FROM sys.tables t
    INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = N''repository'' AND t.name = N''FolderSecurityPolicies'')
BEGIN
    CREATE TABLE [repository].[FolderSecurityPolicies] (
        [Id] UNIQUEIDENTIFIER NOT NULL CONSTRAINT [PK_FolderSecurityPolicies] PRIMARY KEY,
        [RepositoryId] UNIQUEIDENTIFIER NOT NULL,
        [FolderId] UNIQUEIDENTIFIER NULL,
        [CanView] BIT NOT NULL CONSTRAINT [DF_FolderSecurityPolicies_CanView] DEFAULT (1),
        [CanUpload] BIT NOT NULL CONSTRAINT [DF_FolderSecurityPolicies_CanUpload] DEFAULT (0),
        [CanDownload] BIT NOT NULL CONSTRAINT [DF_FolderSecurityPolicies_CanDownload] DEFAULT (0),
        [CanPrint] BIT NOT NULL CONSTRAINT [DF_FolderSecurityPolicies_CanPrint] DEFAULT (0),
        [CanDelete] BIT NOT NULL CONSTRAINT [DF_FolderSecurityPolicies_CanDelete] DEFAULT (0),
        [CanEditMetadata] BIT NOT NULL CONSTRAINT [DF_FolderSecurityPolicies_CanEditMetadata] DEFAULT (0),
        [CanEditDocument] BIT NOT NULL CONSTRAINT [DF_FolderSecurityPolicies_CanEditDocument] DEFAULT (0),
        [CanCheckOut] BIT NOT NULL CONSTRAINT [DF_FolderSecurityPolicies_CanCheckOut] DEFAULT (0),
        [CanCheckIn] BIT NOT NULL CONSTRAINT [DF_FolderSecurityPolicies_CanCheckIn] DEFAULT (0),
        [CanSendForSignature] BIT NOT NULL CONSTRAINT [DF_FolderSecurityPolicies_CanSendForSignature] DEFAULT (0),
        [CreatedAtUtc] DATETIME2(3) NOT NULL CONSTRAINT [DF_FolderSecurityPolicies_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [ModifiedAtUtc] DATETIME2(3) NULL,
        [CreatedBy] UNIQUEIDENTIFIER NULL,
        [ModifiedBy] UNIQUEIDENTIFIER NULL,
        [IsDeleted] BIT NOT NULL CONSTRAINT [DF_FolderSecurityPolicies_IsDeleted] DEFAULT (0)
    );
    CREATE INDEX [IX_FolderSecurityPolicies_Repo_Folder]
        ON [repository].[FolderSecurityPolicies] ([RepositoryId], [FolderId], [IsDeleted]);
END
' AS NVARCHAR(MAX))
            + CAST(N'
IF NOT EXISTS (
    SELECT 1 FROM sys.tables t
    INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = N''repository'' AND t.name = N''FolderSecurityPrincipals'')
BEGIN
    CREATE TABLE [repository].[FolderSecurityPrincipals] (
        [Id] UNIQUEIDENTIFIER NOT NULL CONSTRAINT [PK_FolderSecurityPrincipals] PRIMARY KEY,
        [PolicyId] UNIQUEIDENTIFIER NOT NULL,
        [PrincipalType] NVARCHAR(16) NOT NULL,
        [PrincipalId] UNIQUEIDENTIFIER NOT NULL,
        CONSTRAINT [FK_FolderSecurityPrincipals_Policy]
            FOREIGN KEY ([PolicyId]) REFERENCES [repository].[FolderSecurityPolicies] ([Id]) ON DELETE CASCADE
    );
    CREATE INDEX [IX_FolderSecurityPrincipals_Policy]
        ON [repository].[FolderSecurityPrincipals] ([PolicyId]);
    CREATE INDEX [IX_FolderSecurityPrincipals_Principal]
        ON [repository].[FolderSecurityPrincipals] ([PrincipalType], [PrincipalId]);
END
' AS NVARCHAR(MAX))
            + CAST(N'
IF NOT EXISTS (
    SELECT 1 FROM sys.tables t
    INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = N''repository'' AND t.name = N''DocumentSecurityRules'')
BEGIN
    CREATE TABLE [repository].[DocumentSecurityRules] (
        [Id] UNIQUEIDENTIFIER NOT NULL CONSTRAINT [PK_DocumentSecurityRules] PRIMARY KEY,
        [RepositoryId] UNIQUEIDENTIFIER NOT NULL,
        [Action] NVARCHAR(16) NOT NULL,
        [MatchMode] NVARCHAR(8) NOT NULL CONSTRAINT [DF_DocumentSecurityRules_MatchMode] DEFAULT (N''all''),
        [ConditionsJson] NVARCHAR(MAX) NOT NULL,
        [SortOrder] INT NOT NULL CONSTRAINT [DF_DocumentSecurityRules_SortOrder] DEFAULT (0),
        [CreatedAtUtc] DATETIME2(3) NOT NULL CONSTRAINT [DF_DocumentSecurityRules_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [ModifiedAtUtc] DATETIME2(3) NULL,
        [CreatedBy] UNIQUEIDENTIFIER NULL,
        [ModifiedBy] UNIQUEIDENTIFIER NULL,
        [IsDeleted] BIT NOT NULL CONSTRAINT [DF_DocumentSecurityRules_IsDeleted] DEFAULT (0)
    );
    CREATE INDEX [IX_DocumentSecurityRules_Repo]
        ON [repository].[DocumentSecurityRules] ([RepositoryId], [IsDeleted], [SortOrder]);
END
' AS NVARCHAR(MAX))
            + CAST(N'
IF NOT EXISTS (
    SELECT 1 FROM sys.tables t
    INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = N''repository'' AND t.name = N''DocumentSecurityPrincipals'')
BEGIN
    CREATE TABLE [repository].[DocumentSecurityPrincipals] (
        [Id] UNIQUEIDENTIFIER NOT NULL CONSTRAINT [PK_DocumentSecurityPrincipals] PRIMARY KEY,
        [RuleId] UNIQUEIDENTIFIER NOT NULL,
        [PrincipalType] NVARCHAR(16) NOT NULL,
        [PrincipalId] UNIQUEIDENTIFIER NOT NULL,
        CONSTRAINT [FK_DocumentSecurityPrincipals_Rule]
            FOREIGN KEY ([RuleId]) REFERENCES [repository].[DocumentSecurityRules] ([Id]) ON DELETE CASCADE
    );
    CREATE INDEX [IX_DocumentSecurityPrincipals_Rule]
        ON [repository].[DocumentSecurityPrincipals] ([RuleId]);
    CREATE INDEX [IX_DocumentSecurityPrincipals_Principal]
        ON [repository].[DocumentSecurityPrincipals] ([PrincipalType], [PrincipalId]);
END
' AS NVARCHAR(MAX))
            + CAST(N'
IF COL_LENGTH(''repository.DocumentSecurityRules'', ''Source'') IS NULL
    ALTER TABLE [repository].[DocumentSecurityRules] ADD [Source] NVARCHAR(32) NULL;

IF NOT EXISTS (
    SELECT 1 FROM sys.tables t
    INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = N''repository'' AND t.name = N''ShareRecipients'')
BEGIN
    CREATE TABLE [repository].[ShareRecipients] (
        [Id] UNIQUEIDENTIFIER NOT NULL CONSTRAINT [PK_ShareRecipients] PRIMARY KEY,
        [RepositoryId] UNIQUEIDENTIFIER NOT NULL,
        [UserId] UNIQUEIDENTIFIER NOT NULL,
        [CanUpload] BIT NOT NULL CONSTRAINT [DF_ShareRecipients_CanUpload] DEFAULT (0),
        [CreatedAtUtc] DATETIME2(3) NOT NULL CONSTRAINT [DF_ShareRecipients_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [ModifiedAtUtc] DATETIME2(3) NULL,
        [CreatedBy] UNIQUEIDENTIFIER NULL,
        [IsDeleted] BIT NOT NULL CONSTRAINT [DF_ShareRecipients_IsDeleted] DEFAULT (0)
    );
    CREATE UNIQUE INDEX [IX_ShareRecipients_Repo_User]
        ON [repository].[ShareRecipients] ([RepositoryId], [UserId]);
END
' AS NVARCHAR(MAX));

        EXEC sys.sp_executesql @Sql;
        SET @Applied += 1;
        PRINT CONCAT(N'✓ OK    ', @TenantName, N' → [', @DbName, N']');
    END TRY
    BEGIN CATCH
        SET @Failed += 1;
        PRINT CONCAT(N'✗ FAIL  ', @TenantName, N' → [', @DbName, N'] — ', ERROR_MESSAGE());
    END CATCH

NextTenant:
    FETCH NEXT FROM tenant_cursor INTO @TenantId, @TenantName, @ConnectionString;
END

CLOSE tenant_cursor;
DEALLOCATE tenant_cursor;

PRINT N'';
PRINT CONCAT(N'Done. Applied=', @Applied, N'  Skipped=', @Skipped, N'  Failed=', @Failed);
GO
