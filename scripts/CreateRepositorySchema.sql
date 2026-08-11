-- =============================================
-- Repository module - base schema (tenant database)
-- STATIC repositories; GUID keys; per-repo Items_{suffix} created by API provisioner
-- =============================================

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'repository')
BEGIN
    EXEC('CREATE SCHEMA repository');
    PRINT 'repository schema created';
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables t INNER JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE t.name = 'StorageProviders' AND s.name = 'repository')
BEGIN
    CREATE TABLE repository.StorageProviders (
        Id              UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_StorageProviders PRIMARY KEY DEFAULT NEWID(),
        TenantId        UNIQUEIDENTIFIER NOT NULL,
        Code            NVARCHAR(32)  NOT NULL,
        Name            NVARCHAR(128) NOT NULL,
        ConfigJson      NVARCHAR(MAX) NULL,
        IsActive        BIT NOT NULL CONSTRAINT DF_StorageProviders_IsActive DEFAULT (1),
        CreatedAtUtc    DATETIME2(3) NOT NULL CONSTRAINT DF_StorageProviders_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        ModifiedAtUtc   DATETIME2(3) NULL,
        CreatedBy       UNIQUEIDENTIFIER NULL,
        ModifiedBy      UNIQUEIDENTIFIER NULL,
        IsDeleted       BIT NOT NULL CONSTRAINT DF_StorageProviders_IsDeleted DEFAULT (0),
        CONSTRAINT UQ_StorageProviders_TenantId_Code UNIQUE (TenantId, Code)
    );
    CREATE INDEX IX_StorageProviders_TenantId_IsDeleted ON repository.StorageProviders (TenantId, IsDeleted);
    PRINT 'repository.StorageProviders created';
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables t INNER JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE t.name = 'Repositories' AND s.name = 'repository')
BEGIN
    CREATE TABLE repository.Repositories (
        Id                  UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Repositories PRIMARY KEY DEFAULT NEWID(),
        TenantId            UNIQUEIDENTIFIER NOT NULL,
        Name                NVARCHAR(256) NOT NULL,
        Description         NVARCHAR(2000) NULL,
        FieldsType          NVARCHAR(32)  NOT NULL CONSTRAINT DF_Repositories_FieldsType DEFAULT ('STATIC'),
        StorageProviderId   UNIQUEIDENTIFIER NOT NULL,
        StorageDrive        NVARCHAR(500) NULL,
        ItemsTableName      NVARCHAR(128) NOT NULL,
        StageTableName      NVARCHAR(128) NOT NULL,
        IsDefaultRepository BIT NOT NULL CONSTRAINT DF_Repositories_IsDefaultRepository DEFAULT (1),
        CreatedAtUtc        DATETIME2(3) NOT NULL CONSTRAINT DF_Repositories_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        ModifiedAtUtc       DATETIME2(3) NULL,
        CreatedBy           UNIQUEIDENTIFIER NULL,
        ModifiedBy          UNIQUEIDENTIFIER NULL,
        IsDeleted           BIT NOT NULL CONSTRAINT DF_Repositories_IsDeleted DEFAULT (0),
        CONSTRAINT FK_Repositories_StorageProvider FOREIGN KEY (StorageProviderId) REFERENCES repository.StorageProviders (Id),
        CONSTRAINT CK_Repositories_FieldsType CHECK (FieldsType = 'STATIC')
    );
    CREATE INDEX IX_Repositories_TenantId_IsDeleted ON repository.Repositories (TenantId, IsDeleted);
    CREATE UNIQUE INDEX UX_Repositories_TenantId_Name ON repository.Repositories (TenantId, Name) WHERE IsDeleted = 0;
    PRINT 'repository.Repositories created';
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables t INNER JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE t.name = 'RepositoryFields' AND s.name = 'repository')
BEGIN
    CREATE TABLE repository.RepositoryFields (
        Id                          UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_RepositoryFields PRIMARY KEY DEFAULT NEWID(),
        RepositoryId                UNIQUEIDENTIFIER NOT NULL,
        Name                        NVARCHAR(200) NOT NULL,
        SqlColumnName               NVARCHAR(200) NOT NULL,
        DataType                    NVARCHAR(64)  NULL,
        Level                       INT NOT NULL CONSTRAINT DF_RepositoryFields_Level DEFAULT (0),
        IsMandatory                 BIT NOT NULL CONSTRAINT DF_RepositoryFields_IsMandatory DEFAULT (0),
        IncludeInFolderStructure    BIT NOT NULL CONSTRAINT DF_RepositoryFields_IncludeInFolderStructure DEFAULT (0),
        OptionsJson                 NVARCHAR(MAX) NULL,
        OrderId                     INT NULL,
        IsReadOnly                  BIT NOT NULL CONSTRAINT DF_RepositoryFields_IsReadOnly DEFAULT (0),
        CreatedAtUtc                  DATETIME2(3) NOT NULL CONSTRAINT DF_RepositoryFields_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        ModifiedAtUtc               DATETIME2(3) NULL,
        CreatedBy                   UNIQUEIDENTIFIER NULL,
        ModifiedBy                  UNIQUEIDENTIFIER NULL,
        IsDeleted                   BIT NOT NULL CONSTRAINT DF_RepositoryFields_IsDeleted DEFAULT (0),
        CONSTRAINT FK_RepositoryFields_Repository FOREIGN KEY (RepositoryId) REFERENCES repository.Repositories (Id) ON DELETE CASCADE
    );
    CREATE INDEX IX_RepositoryFields_RepositoryId_IsDeleted ON repository.RepositoryFields (RepositoryId, IsDeleted);
    PRINT 'repository.RepositoryFields created';
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables t INNER JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE t.name = 'Folders' AND s.name = 'repository')
BEGIN
    CREATE TABLE repository.Folders (
        Id              UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Folders PRIMARY KEY DEFAULT NEWID(),
        TenantId        UNIQUEIDENTIFIER NOT NULL,
        RepositoryId    UNIQUEIDENTIFIER NOT NULL,
        Name            NVARCHAR(256) NOT NULL,
        ParentId        UNIQUEIDENTIFIER NULL,
        LevelId         INT NOT NULL CONSTRAINT DF_Folders_LevelId DEFAULT (0),
        PathId          NVARCHAR(512) NULL,
        CreatedAtUtc    DATETIME2(3) NOT NULL CONSTRAINT DF_Folders_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        ModifiedAtUtc   DATETIME2(3) NULL,
        CreatedBy       UNIQUEIDENTIFIER NULL,
        ModifiedBy      UNIQUEIDENTIFIER NULL,
        IsDeleted       BIT NOT NULL CONSTRAINT DF_Folders_IsDeleted DEFAULT (0),
        CONSTRAINT FK_Folders_Repository FOREIGN KEY (RepositoryId) REFERENCES repository.Repositories (Id) ON DELETE CASCADE
    );
    CREATE INDEX IX_Folders_RepositoryId_ParentId_IsDeleted ON repository.Folders (RepositoryId, ParentId, IsDeleted);
    PRINT 'repository.Folders created';
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables t INNER JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE t.name = 'SavedViews' AND s.name = 'repository')
BEGIN
    CREATE TABLE repository.SavedViews (
        Id              UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_SavedViews PRIMARY KEY DEFAULT NEWID(),
        TenantId        UNIQUEIDENTIFIER NOT NULL,
        RepositoryId    UNIQUEIDENTIFIER NOT NULL,
        UserId          UNIQUEIDENTIFIER NOT NULL,
        Name            NVARCHAR(256) NOT NULL,
        FilterJson      NVARCHAR(MAX) NOT NULL,
        SortJson        NVARCHAR(MAX) NULL,
        CreatedAtUtc    DATETIME2(3) NOT NULL CONSTRAINT DF_SavedViews_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        ModifiedAtUtc   DATETIME2(3) NULL,
        IsDeleted       BIT NOT NULL CONSTRAINT DF_SavedViews_IsDeleted DEFAULT (0),
        CONSTRAINT FK_SavedViews_Repository FOREIGN KEY (RepositoryId) REFERENCES repository.Repositories (Id) ON DELETE CASCADE
    );
    CREATE INDEX IX_SavedViews_RepositoryId_UserId ON repository.SavedViews (RepositoryId, UserId, IsDeleted);
    PRINT 'repository.SavedViews created';
END
GO

-- Optional item activity (timeline/comments). Not required for repository create or file upload.
IF NOT EXISTS (SELECT * FROM sys.tables t INNER JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE t.name = 'ItemTimelineEvents' AND s.name = 'repository')
BEGIN
    CREATE TABLE repository.ItemTimelineEvents (
        Id              UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_ItemTimelineEvents PRIMARY KEY DEFAULT NEWID(),
        TenantId        UNIQUEIDENTIFIER NOT NULL,
        RepositoryId    UNIQUEIDENTIFIER NOT NULL,
        ItemId          UNIQUEIDENTIFIER NOT NULL,
        EventType       NVARCHAR(64)  NOT NULL,
        Title           NVARCHAR(500) NOT NULL,
        Description     NVARCHAR(MAX) NULL,
        ActorType       NVARCHAR(64)  NULL,
        ActorName       NVARCHAR(256) NULL,
        ActorUserId     UNIQUEIDENTIFIER NULL,
        CreatedBy       UNIQUEIDENTIFIER NULL,
        CreatedAtUtc    DATETIME2(3) NOT NULL CONSTRAINT DF_ItemTimelineEvents_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        IsDeleted       BIT NOT NULL CONSTRAINT DF_ItemTimelineEvents_IsDeleted DEFAULT (0)
    );
    CREATE INDEX IX_ItemTimelineEvents_Item ON repository.ItemTimelineEvents (TenantId, RepositoryId, ItemId, IsDeleted, CreatedAtUtc);
    PRINT 'repository.ItemTimelineEvents created';
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables t INNER JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE t.name = 'ItemComments' AND s.name = 'repository')
BEGIN
    CREATE TABLE repository.ItemComments (
        Id              UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_ItemComments PRIMARY KEY DEFAULT NEWID(),
        TenantId        UNIQUEIDENTIFIER NOT NULL,
        RepositoryId    UNIQUEIDENTIFIER NOT NULL,
        ItemId          UNIQUEIDENTIFIER NOT NULL,
        Body            NVARCHAR(MAX) NOT NULL,
        CreatedBy       UNIQUEIDENTIFIER NOT NULL,
        CreatedAtUtc    DATETIME2(3) NOT NULL CONSTRAINT DF_ItemComments_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        ModifiedAtUtc   DATETIME2(3) NULL,
        IsDeleted       BIT NOT NULL CONSTRAINT DF_ItemComments_IsDeleted DEFAULT (0)
    );
    CREATE INDEX IX_ItemComments_Item ON repository.ItemComments (TenantId, RepositoryId, ItemId, IsDeleted, CreatedAtUtc);
    PRINT 'repository.ItemComments created';
END
GO

-- Saved related documents for an open item (replace-on-save semantics).
IF NOT EXISTS (SELECT * FROM sys.tables t INNER JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE t.name = 'ItemRelatedDocuments' AND s.name = 'repository')
BEGIN
    CREATE TABLE repository.ItemRelatedDocuments (
        Id                      UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_ItemRelatedDocuments PRIMARY KEY DEFAULT NEWID(),
        TenantId                UNIQUEIDENTIFIER NOT NULL,
        RepositoryId            UNIQUEIDENTIFIER NOT NULL,
        ItemId                  UNIQUEIDENTIFIER NOT NULL,
        RelatedRepositoryId     UNIQUEIDENTIFIER NOT NULL,
        RelatedItemId           UNIQUEIDENTIFIER NOT NULL,
        MatchField              NVARCHAR(128) NULL,
        MatchValue              NVARCHAR(450) NULL,
        MatchScore              INT NULL,
        CreatedBy               UNIQUEIDENTIFIER NULL,
        CreatedAtUtc            DATETIME2(3) NOT NULL CONSTRAINT DF_ItemRelatedDocuments_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        IsDeleted               BIT NOT NULL CONSTRAINT DF_ItemRelatedDocuments_IsDeleted DEFAULT (0)
    );
    CREATE INDEX IX_ItemRelatedDocuments_Source
        ON repository.ItemRelatedDocuments (TenantId, RepositoryId, ItemId, IsDeleted, CreatedAtUtc);
    PRINT 'repository.ItemRelatedDocuments created';
END
GO

PRINT 'Repository base schema complete.';
GO

IF COL_LENGTH('repository.Repositories', 'IsDefaultRepository') IS NULL
BEGIN
    ALTER TABLE repository.Repositories
        ADD IsDefaultRepository BIT NOT NULL CONSTRAINT DF_Repositories_IsDefaultRepository DEFAULT (1);
    PRINT 'repository.Repositories.IsDefaultRepository added';
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables t INNER JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE t.name = 'FolderSecurityPolicies' AND s.name = 'repository')
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
GO

IF NOT EXISTS (SELECT * FROM sys.tables t INNER JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE t.name = 'FolderSecurityPrincipals' AND s.name = 'repository')
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
GO

IF NOT EXISTS (SELECT * FROM sys.tables t INNER JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE t.name = 'DocumentSecurityRules' AND s.name = 'repository')
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
GO

IF NOT EXISTS (SELECT * FROM sys.tables t INNER JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE t.name = 'DocumentSecurityPrincipals' AND s.name = 'repository')
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
GO
