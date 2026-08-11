-- =============================================
-- Repository Folder + Document Security tables
-- For ONE existing tenant database
--
-- Run against: TENANT database (e.g. ezofis_Tenant_xxx)
-- Safe to re-run (idempotent)
--
-- Creates:
--   repository.FolderSecurityPolicies
--   repository.FolderSecurityPrincipals
--   repository.DocumentSecurityRules
--   repository.DocumentSecurityPrincipals
-- =============================================

SET NOCOUNT ON;

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'repository')
BEGIN
    EXEC(N'CREATE SCHEMA repository');
    PRINT 'schema repository created';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.tables t
    INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = N'repository' AND t.name = N'FolderSecurityPolicies')
BEGIN
    CREATE TABLE repository.FolderSecurityPolicies (
        Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_FolderSecurityPolicies PRIMARY KEY,
        RepositoryId UNIQUEIDENTIFIER NOT NULL,
        FolderId UNIQUEIDENTIFIER NULL,
        CanView BIT NOT NULL CONSTRAINT DF_FolderSecurityPolicies_CanView DEFAULT (1),
        CanUpload BIT NOT NULL CONSTRAINT DF_FolderSecurityPolicies_CanUpload DEFAULT (0),
        CanDownload BIT NOT NULL CONSTRAINT DF_FolderSecurityPolicies_CanDownload DEFAULT (0),
        CanPrint BIT NOT NULL CONSTRAINT DF_FolderSecurityPolicies_CanPrint DEFAULT (0),
        CanDelete BIT NOT NULL CONSTRAINT DF_FolderSecurityPolicies_CanDelete DEFAULT (0),
        CanEditMetadata BIT NOT NULL CONSTRAINT DF_FolderSecurityPolicies_CanEditMetadata DEFAULT (0),
        CanEditDocument BIT NOT NULL CONSTRAINT DF_FolderSecurityPolicies_CanEditDocument DEFAULT (0),
        CanCheckOut BIT NOT NULL CONSTRAINT DF_FolderSecurityPolicies_CanCheckOut DEFAULT (0),
        CanCheckIn BIT NOT NULL CONSTRAINT DF_FolderSecurityPolicies_CanCheckIn DEFAULT (0),
        CanSendForSignature BIT NOT NULL CONSTRAINT DF_FolderSecurityPolicies_CanSendForSignature DEFAULT (0),
        CreatedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_FolderSecurityPolicies_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        ModifiedAtUtc DATETIME2(3) NULL,
        CreatedBy UNIQUEIDENTIFIER NULL,
        ModifiedBy UNIQUEIDENTIFIER NULL,
        IsDeleted BIT NOT NULL CONSTRAINT DF_FolderSecurityPolicies_IsDeleted DEFAULT (0)
    );
    CREATE INDEX IX_FolderSecurityPolicies_Repo_Folder
        ON repository.FolderSecurityPolicies (RepositoryId, FolderId, IsDeleted);
    PRINT 'repository.FolderSecurityPolicies created';
END
ELSE
    PRINT 'repository.FolderSecurityPolicies already exists';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.tables t
    INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = N'repository' AND t.name = N'FolderSecurityPrincipals')
BEGIN
    CREATE TABLE repository.FolderSecurityPrincipals (
        Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_FolderSecurityPrincipals PRIMARY KEY,
        PolicyId UNIQUEIDENTIFIER NOT NULL,
        PrincipalType NVARCHAR(16) NOT NULL,
        PrincipalId UNIQUEIDENTIFIER NOT NULL,
        CONSTRAINT FK_FolderSecurityPrincipals_Policy
            FOREIGN KEY (PolicyId) REFERENCES repository.FolderSecurityPolicies (Id) ON DELETE CASCADE
    );
    CREATE INDEX IX_FolderSecurityPrincipals_Policy ON repository.FolderSecurityPrincipals (PolicyId);
    CREATE INDEX IX_FolderSecurityPrincipals_Principal
        ON repository.FolderSecurityPrincipals (PrincipalType, PrincipalId);
    PRINT 'repository.FolderSecurityPrincipals created';
END
ELSE
    PRINT 'repository.FolderSecurityPrincipals already exists';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.tables t
    INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = N'repository' AND t.name = N'DocumentSecurityRules')
BEGIN
    CREATE TABLE repository.DocumentSecurityRules (
        Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_DocumentSecurityRules PRIMARY KEY,
        RepositoryId UNIQUEIDENTIFIER NOT NULL,
        Action NVARCHAR(16) NOT NULL,
        MatchMode NVARCHAR(8) NOT NULL CONSTRAINT DF_DocumentSecurityRules_MatchMode DEFAULT (N'all'),
        ConditionsJson NVARCHAR(MAX) NOT NULL,
        SortOrder INT NOT NULL CONSTRAINT DF_DocumentSecurityRules_SortOrder DEFAULT (0),
        CreatedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_DocumentSecurityRules_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        ModifiedAtUtc DATETIME2(3) NULL,
        CreatedBy UNIQUEIDENTIFIER NULL,
        ModifiedBy UNIQUEIDENTIFIER NULL,
        IsDeleted BIT NOT NULL CONSTRAINT DF_DocumentSecurityRules_IsDeleted DEFAULT (0)
    );
    CREATE INDEX IX_DocumentSecurityRules_Repo
        ON repository.DocumentSecurityRules (RepositoryId, IsDeleted, SortOrder);
    PRINT 'repository.DocumentSecurityRules created';
END
ELSE
    PRINT 'repository.DocumentSecurityRules already exists';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.tables t
    INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = N'repository' AND t.name = N'DocumentSecurityPrincipals')
BEGIN
    CREATE TABLE repository.DocumentSecurityPrincipals (
        Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_DocumentSecurityPrincipals PRIMARY KEY,
        RuleId UNIQUEIDENTIFIER NOT NULL,
        PrincipalType NVARCHAR(16) NOT NULL,
        PrincipalId UNIQUEIDENTIFIER NOT NULL,
        CONSTRAINT FK_DocumentSecurityPrincipals_Rule
            FOREIGN KEY (RuleId) REFERENCES repository.DocumentSecurityRules (Id) ON DELETE CASCADE
    );
    CREATE INDEX IX_DocumentSecurityPrincipals_Rule ON repository.DocumentSecurityPrincipals (RuleId);
    CREATE INDEX IX_DocumentSecurityPrincipals_Principal
        ON repository.DocumentSecurityPrincipals (PrincipalType, PrincipalId);
    PRINT 'repository.DocumentSecurityPrincipals created';
END
ELSE
    PRINT 'repository.DocumentSecurityPrincipals already exists';
GO

IF COL_LENGTH('repository.DocumentSecurityRules', 'Source') IS NULL
BEGIN
    ALTER TABLE repository.DocumentSecurityRules ADD Source NVARCHAR(32) NULL;
    PRINT 'repository.DocumentSecurityRules.Source added';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.tables t
    INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = N'repository' AND t.name = N'ShareRecipients')
BEGIN
    CREATE TABLE repository.ShareRecipients (
        Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_ShareRecipients PRIMARY KEY,
        RepositoryId UNIQUEIDENTIFIER NOT NULL,
        UserId UNIQUEIDENTIFIER NOT NULL,
        CanUpload BIT NOT NULL CONSTRAINT DF_ShareRecipients_CanUpload DEFAULT (0),
        CreatedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_ShareRecipients_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        ModifiedAtUtc DATETIME2(3) NULL,
        CreatedBy UNIQUEIDENTIFIER NULL,
        IsDeleted BIT NOT NULL CONSTRAINT DF_ShareRecipients_IsDeleted DEFAULT (0)
    );
    CREATE UNIQUE INDEX IX_ShareRecipients_Repo_User
        ON repository.ShareRecipients (RepositoryId, UserId);
    PRINT 'repository.ShareRecipients created';
END
ELSE
    PRINT 'repository.ShareRecipients already exists';
GO

PRINT 'Repository folder/document security schema complete.';
GO
