-- =============================================
-- CATALOG DATABASE - CREATE DATABASE + CONNECTOR PROVIDERS
-- Run against master (or default connection)
-- Idempotent: safe to re-run.
-- =============================================

IF NOT EXISTS (SELECT * FROM sys.databases WHERE name = 'ezofis_catalog_Dev')
BEGIN
    PRINT 'Creating catalog database: ezofis_catalog_Dev';
    CREATE DATABASE [ezofis_catalog_Dev];
    PRINT '✓ Catalog database created';
END
ELSE
BEGIN
    PRINT '✓ Catalog database already exists';
END
GO

USE [ezofis_catalog_Dev];
GO

PRINT '';
PRINT '=== Ensuring catalog.ConnectorProviders (OAuth apps) ===';
PRINT '';

IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'catalog')
BEGIN
    EXEC('CREATE SCHEMA [catalog]');
    PRINT '✓ catalog schema created';
END
ELSE
BEGIN
    PRINT '✓ catalog schema already exists';
END
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
    PRINT '✓ catalog.ConnectorProviders created';
END
ELSE
BEGIN
    PRINT '✓ catalog.ConnectorProviders already exists';
END
GO

-- Seed Phase-1 providers (empty ClientId/Secret — fill after create; do not commit secrets)
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
    (N'OUTLOOK', N'Office 365 Outlook',
     N'https://login.microsoftonline.com/common/oauth2/v2.0/authorize',
     N'https://login.microsoftonline.com/common/oauth2/v2.0/token',
     N'offline_access openid profile email Mail.ReadWrite User.Read'),
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
WHEN NOT MATCHED THEN
    INSERT ([Id], [ProviderCode], [DisplayName], [ClientId], [ClientSecret], [AuthUrl], [TokenUrl], [Scopes], [RedirectUri], [IsActive], [CreatedAtUtc])
    VALUES (NEWID(), s.[ProviderCode], s.[DisplayName], N'', N'', s.[AuthUrl], s.[TokenUrl], s.[Scopes], N'', 1, SYSUTCDATETIME());
GO

PRINT '✓ ConnectorProviders seed ensured (GCP, GMAIL, OUTLOOK, ONEDRIVE, TEAMS, DROPBOX, QUICKBOOKS, SAP)';
PRINT '  Set ClientId / ClientSecret / RedirectUri, e.g.:';
PRINT '  UPDATE catalog.ConnectorProviders SET ClientId=N''...'', ClientSecret=N''...'', RedirectUri=N''https://host/V6API/api/connector/oauth/callback'' WHERE ProviderCode=N''GCP'';';
PRINT '';
PRINT 'Next: run 01b_CreateCatalogTables.sql for Tenants / UserTenants.';
GO
