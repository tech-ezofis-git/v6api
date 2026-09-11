-- =============================================
-- Catalog: ConnectorProviders (global OAuth app config)
-- Run against catalog database (DefaultConnection).
-- Secrets: UPDATE ClientId/ClientSecret after create — do not commit real secrets.
-- =============================================

IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'catalog')
    EXEC('CREATE SCHEMA [catalog]');
GO

IF OBJECT_ID(N'[catalog].[ConnectorProviders]', N'U') IS NULL
BEGIN
    CREATE TABLE [catalog].[ConnectorProviders] (
        [Id] UNIQUEIDENTIFIER NOT NULL CONSTRAINT [PK_ConnectorProviders] PRIMARY KEY DEFAULT NEWID(),
        [ProviderCode] NVARCHAR(64) NOT NULL,
        [DisplayName] NVARCHAR(128) NOT NULL,
        [ClientId] NVARCHAR(512) NOT NULL CONSTRAINT [DF_ConnectorProviders_ClientId] DEFAULT (N''),
        [ClientSecret] NVARCHAR(1024) NOT NULL CONSTRAINT [DF_ConnectorProviders_ClientSecret] DEFAULT (N''),
        [AuthUrl] NVARCHAR(1024) NOT NULL,
        [TokenUrl] NVARCHAR(1024) NOT NULL,
        [Scopes] NVARCHAR(2000) NOT NULL CONSTRAINT [DF_ConnectorProviders_Scopes] DEFAULT (N''),
        [RedirectUri] NVARCHAR(1024) NOT NULL CONSTRAINT [DF_ConnectorProviders_RedirectUri] DEFAULT (N''),
        [ExtraConfigJson] NVARCHAR(MAX) NULL,
        [IsActive] BIT NOT NULL CONSTRAINT [DF_ConnectorProviders_IsActive] DEFAULT (1),
        [CreatedAtUtc] DATETIME2(3) NOT NULL CONSTRAINT [DF_ConnectorProviders_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [ModifiedAtUtc] DATETIME2(3) NULL,
        CONSTRAINT [UQ_ConnectorProviders_ProviderCode] UNIQUE ([ProviderCode])
    );
    PRINT 'catalog.ConnectorProviders created';
END
GO

-- Seed providers (empty ClientId/Secret — fill via UPDATE; do not commit secrets)
MERGE [catalog].[ConnectorProviders] AS t
USING (VALUES
    (N'GCP', N'Google Cloud Storage',
     N'https://accounts.google.com/o/oauth2/v2/auth',
     N'https://oauth2.googleapis.com/token',
     N'https://www.googleapis.com/auth/devstorage.read_write https://www.googleapis.com/auth/userinfo.email openid'),
    (N'GMAIL', N'Gmail',
     N'https://accounts.google.com/o/oauth2/v2/auth',
     N'https://oauth2.googleapis.com/token',
     N'https://www.googleapis.com/auth/gmail.modify https://www.googleapis.com/auth/userinfo.email openid'),
    (N'OUTLOOK', N'Office 365 Outlook',
     N'https://login.microsoftonline.com/common/oauth2/v2.0/authorize',
     N'https://login.microsoftonline.com/common/oauth2/v2.0/token',
     N'offline_access openid profile email Mail.ReadWrite User.Read'),
    (N'ONEDRIVE', N'Microsoft OneDrive',
     N'https://login.microsoftonline.com/common/oauth2/v2.0/authorize',
     N'https://login.microsoftonline.com/common/oauth2/v2.0/token',
     N'offline_access openid profile email Files.ReadWrite.All User.Read'),
    (N'TEAMS', N'Microsoft Teams',
     N'https://login.microsoftonline.com/common/oauth2/v2.0/authorize',
     N'https://login.microsoftonline.com/common/oauth2/v2.0/token',
     N'offline_access openid profile email Files.ReadWrite.All Sites.ReadWrite.All User.Read'),
    (N'DROPBOX', N'Dropbox',
     N'https://www.dropbox.com/oauth2/authorize',
     N'https://api.dropboxapi.com/oauth2/token',
     N''),
    (N'QUICKBOOKS', N'QuickBooks',
     N'https://appcenter.intuit.com/connect/oauth2',
     N'https://oauth.platform.intuit.com/oauth2/v1/tokens/bearer',
     N'com.intuit.quickbooks.accounting openid profile email'),
    (N'SAP', N'SAP',
     N'',
     N'',
     N'')
) AS s ([ProviderCode], [DisplayName], [AuthUrl], [TokenUrl], [Scopes])
ON t.[ProviderCode] = s.[ProviderCode]
WHEN MATCHED AND t.[ProviderCode] IN (N'GMAIL', N'OUTLOOK') THEN
    UPDATE SET [Scopes] = s.[Scopes], [ModifiedAtUtc] = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT ([Id], [ProviderCode], [DisplayName], [ClientId], [ClientSecret], [AuthUrl], [TokenUrl], [Scopes], [RedirectUri], [IsActive], [CreatedAtUtc])
    VALUES (NEWID(), s.[ProviderCode], s.[DisplayName], N'', N'', s.[AuthUrl], s.[TokenUrl], s.[Scopes], N'', 1, SYSUTCDATETIME());
GO

-- Prefer renaming legacy email provider; if QUICKBOOKS already exists, deactivate the old code
IF EXISTS (SELECT 1 FROM catalog.ConnectorProviders WHERE ProviderCode = N'QUICKBOOKS_EMAIL')
   AND NOT EXISTS (SELECT 1 FROM catalog.ConnectorProviders WHERE ProviderCode = N'QUICKBOOKS')
BEGIN
    UPDATE catalog.ConnectorProviders
    SET [ProviderCode] = N'QUICKBOOKS',
        [DisplayName] = N'QuickBooks',
        [Scopes] = N'com.intuit.quickbooks.accounting openid profile email',
        [ModifiedAtUtc] = SYSUTCDATETIME()
    WHERE [ProviderCode] = N'QUICKBOOKS_EMAIL';
END
ELSE IF EXISTS (SELECT 1 FROM catalog.ConnectorProviders WHERE ProviderCode = N'QUICKBOOKS_EMAIL')
BEGIN
    UPDATE catalog.ConnectorProviders
    SET [IsActive] = 0,
        [ModifiedAtUtc] = SYSUTCDATETIME()
    WHERE [ProviderCode] = N'QUICKBOOKS_EMAIL';
END
GO

PRINT 'Seed complete. Set ClientId, ClientSecret, RedirectUri per provider.';
GO
