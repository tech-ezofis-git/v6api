-- =============================================
-- Tenant: dbo.connector (modern OAuth schema — no v5 columns)
-- Idempotent. Migrates legacy table when ProviderCode is missing.
-- =============================================
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'[dbo].[connector]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[connector] (
        [Id] UNIQUEIDENTIFIER NOT NULL CONSTRAINT [PK_connector] PRIMARY KEY,
        [Name] NVARCHAR(256) NOT NULL,
        [ProviderCode] NVARCHAR(64) NOT NULL,
        [ConfigJson] NVARCHAR(MAX) NULL,
        [AccessToken] NVARCHAR(MAX) NULL,
        [RefreshToken] NVARCHAR(MAX) NULL,
        [TokenExpiresAtUtc] DATETIME2(3) NULL,
        [ExternalAccountEmail] NVARCHAR(320) NULL,
        [ExternalAccountId] NVARCHAR(256) NULL,
        [OAuthStatus] NVARCHAR(32) NOT NULL CONSTRAINT [DF_connector_OAuthStatus] DEFAULT (N'Pending'),
        [IsDefault] BIT NOT NULL CONSTRAINT [DF_connector_IsDefault] DEFAULT (0),
        [CreatedAtUtc] DATETIME2(3) NOT NULL CONSTRAINT [DF_connector_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [ModifiedAtUtc] DATETIME2(3) NULL,
        [CreatedBy] UNIQUEIDENTIFIER NOT NULL,
        [ModifiedBy] UNIQUEIDENTIFIER NULL,
        [IsDeleted] BIT NOT NULL CONSTRAINT [DF_connector_IsDeleted] DEFAULT (0)
    );
    CREATE INDEX [IX_connector_IsDeleted] ON [dbo].[connector] ([IsDeleted]);
    CREATE INDEX [IX_connector_ProviderCode] ON [dbo].[connector] ([ProviderCode]) WHERE [IsDeleted] = 0;
    PRINT 'dbo.connector created (modern schema).';
END
ELSE IF COL_LENGTH('dbo.connector', 'ProviderCode') IS NULL
BEGIN
    IF OBJECT_ID(N'[dbo].[connector_legacy_backup]', N'U') IS NOT NULL
        DROP TABLE [dbo].[connector_legacy_backup];

    SELECT * INTO [dbo].[connector_legacy_backup] FROM [dbo].[connector];
    DROP TABLE [dbo].[connector];

    CREATE TABLE [dbo].[connector] (
        [Id] UNIQUEIDENTIFIER NOT NULL CONSTRAINT [PK_connector] PRIMARY KEY,
        [Name] NVARCHAR(256) NOT NULL,
        [ProviderCode] NVARCHAR(64) NOT NULL,
        [ConfigJson] NVARCHAR(MAX) NULL,
        [AccessToken] NVARCHAR(MAX) NULL,
        [RefreshToken] NVARCHAR(MAX) NULL,
        [TokenExpiresAtUtc] DATETIME2(3) NULL,
        [ExternalAccountEmail] NVARCHAR(320) NULL,
        [ExternalAccountId] NVARCHAR(256) NULL,
        [OAuthStatus] NVARCHAR(32) NOT NULL CONSTRAINT [DF_connector_OAuthStatus] DEFAULT (N'Pending'),
        [IsDefault] BIT NOT NULL CONSTRAINT [DF_connector_IsDefault] DEFAULT (0),
        [CreatedAtUtc] DATETIME2(3) NOT NULL CONSTRAINT [DF_connector_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [ModifiedAtUtc] DATETIME2(3) NULL,
        [CreatedBy] UNIQUEIDENTIFIER NOT NULL,
        [ModifiedBy] UNIQUEIDENTIFIER NULL,
        [IsDeleted] BIT NOT NULL CONSTRAINT [DF_connector_IsDeleted] DEFAULT (0)
    );
    CREATE INDEX [IX_connector_IsDeleted] ON [dbo].[connector] ([IsDeleted]);
    CREATE INDEX [IX_connector_ProviderCode] ON [dbo].[connector] ([ProviderCode]) WHERE [IsDeleted] = 0;

    DECLARE @sql NVARCHAR(MAX) = N'
    INSERT INTO [dbo].[connector] (
        [Id], [Name], [ProviderCode], [ConfigJson],
        [AccessToken], [RefreshToken], [TokenExpiresAtUtc],
        [ExternalAccountEmail], [ExternalAccountId], [OAuthStatus], [IsDefault],
        [CreatedAtUtc], [ModifiedAtUtc], [CreatedBy], [ModifiedBy], [IsDeleted])
    SELECT
        COALESCE(TRY_CONVERT(UNIQUEIDENTIFIER, b.[id]), NEWID()),
        COALESCE(NULLIF(LTRIM(RTRIM(CONVERT(NVARCHAR(256), b.[name]))), N''''), N''Connector''),
        COALESCE(NULLIF(UPPER(LTRIM(RTRIM(CONVERT(NVARCHAR(64), b.[connectorType])))), N''''), N''UNKNOWN''),
        CONVERT(NVARCHAR(MAX), b.[credentialJson]),
        ' + CASE WHEN COL_LENGTH('dbo.connector_legacy_backup', 'accessToken') IS NOT NULL
                 THEN N'CONVERT(NVARCHAR(MAX), b.[accessToken])'
                 ELSE N'NULL' END + N',
        ' + CASE WHEN COL_LENGTH('dbo.connector_legacy_backup', 'refreshToken') IS NOT NULL
                 THEN N'CONVERT(NVARCHAR(MAX), b.[refreshToken])'
                 ELSE N'NULL' END + N',
        ' + CASE WHEN COL_LENGTH('dbo.connector_legacy_backup', 'tokenExpiresAtUtc') IS NOT NULL
                 THEN N'TRY_CONVERT(DATETIME2(3), b.[tokenExpiresAtUtc])'
                 ELSE N'NULL' END + N',
        ' + CASE WHEN COL_LENGTH('dbo.connector_legacy_backup', 'externalAccountEmail') IS NOT NULL
                 THEN N'CONVERT(NVARCHAR(320), b.[externalAccountEmail])'
                 ELSE N'NULL' END + N',
        ' + CASE WHEN COL_LENGTH('dbo.connector_legacy_backup', 'externalAccountId') IS NOT NULL
                 THEN N'CONVERT(NVARCHAR(256), b.[externalAccountId])'
                 ELSE N'NULL' END + N',
        COALESCE(
            ' + CASE WHEN COL_LENGTH('dbo.connector_legacy_backup', 'oauthStatus') IS NOT NULL
                     THEN N'NULLIF(LTRIM(RTRIM(CONVERT(NVARCHAR(32), b.[oauthStatus]))), N'''')'
                     ELSE N'NULL' END + N',
            N''Pending''),
        ' + CASE WHEN COL_LENGTH('dbo.connector_legacy_backup', 'Preference') IS NOT NULL
                 THEN N'CASE WHEN TRY_CONVERT(BIT, b.[Preference]) = 1 THEN 1 ELSE 0 END'
                 ELSE N'0' END + N',
        COALESCE(TRY_CONVERT(DATETIME2(3), b.[createdAt]), SYSUTCDATETIME()),
        TRY_CONVERT(DATETIME2(3), b.[modifiedAt]),
        COALESCE(TRY_CONVERT(UNIQUEIDENTIFIER, b.[createdBy]), ''00000000-0000-0000-0000-000000000000''),
        TRY_CONVERT(UNIQUEIDENTIFIER, b.[modifiedBy]),
        COALESCE(TRY_CONVERT(BIT, b.[isDeleted]), 0)
    FROM [dbo].[connector_legacy_backup] b;';

    EXEC sp_executesql @sql;
    PRINT 'dbo.connector migrated to modern schema. Legacy copy: dbo.connector_legacy_backup';
END
ELSE
BEGIN
    PRINT 'dbo.connector already on modern schema.';
END
GO

IF OBJECT_ID(N'[dbo].[connector]', N'U') IS NOT NULL
   AND COL_LENGTH('dbo.connector', 'HanaDatabaseJson') IS NULL
BEGIN
    ALTER TABLE [dbo].[connector] ADD [HanaDatabaseJson] NVARCHAR(MAX) NULL;
    PRINT 'dbo.connector.HanaDatabaseJson added.';
END
GO
