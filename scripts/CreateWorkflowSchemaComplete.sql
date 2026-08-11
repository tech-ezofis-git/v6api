-- =============================================
-- Workflow Module - Complete Schema with Temporal Tables
-- Database: Tenant-specific database
-- =============================================

-- Create workflow schema
IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'workflow')
BEGIN
    EXEC('CREATE SCHEMA workflow');
END
GO

-- =============================================
-- Core Workflow Tables
-- =============================================

-- Workflows table (workflow definitions)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Workflows' AND schema_id = SCHEMA_ID('workflow'))
BEGIN
    CREATE TABLE workflow.Workflows (
        Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        TenantId UNIQUEIDENTIFIER NOT NULL,
        Name NVARCHAR(256) NOT NULL,
        Description NVARCHAR(2000) NULL,
        Status INT NOT NULL, -- 0=Draft, 1=Published, 2=Archived
        TriggerType INT NOT NULL, -- 0=Manual, 1=Scheduled, 2=Event
        TriggerConfig NVARCHAR(4000) NULL,
        Version INT NOT NULL DEFAULT 1,
        CreatedAtUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        ModifiedAtUtc DATETIME2 NULL,
        CreatedBy UNIQUEIDENTIFIER NOT NULL,
        ModifiedBy UNIQUEIDENTIFIER NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RepositoryId NVARCHAR(64) NULL,
        FormId NVARCHAR(64) NULL,
        INDEX IX_Workflows_TenantId_IsDeleted NONCLUSTERED (TenantId, IsDeleted)
    );
END
ELSE
BEGIN
    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('workflow.Workflows') AND name = 'RepositoryId')
        ALTER TABLE workflow.Workflows ADD RepositoryId NVARCHAR(64) NULL;
    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('workflow.Workflows') AND name = 'FormId')
        ALTER TABLE workflow.Workflows ADD FormId NVARCHAR(64) NULL;
END
GO

-- WorkflowSteps table
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'WorkflowSteps' AND schema_id = SCHEMA_ID('workflow'))
BEGIN
    CREATE TABLE workflow.WorkflowSteps (
        Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        WorkflowId UNIQUEIDENTIFIER NOT NULL,
        Name NVARCHAR(256) NOT NULL,
        Description NVARCHAR(2000) NULL,
        StepType INT NOT NULL, -- 0=Task, 1=Approval, 2=Notification, 3=Automation
        [Order] INT NOT NULL,
        Config NVARCHAR(4000) NULL,
        IsRequired BIT NOT NULL DEFAULT 1,
        AssignedToUserId UNIQUEIDENTIFIER NULL,
        AssignedToRole NVARCHAR(64) NULL,
        ApprovedNextStepId UNIQUEIDENTIFIER NULL,
        RejectedNextStepId UNIQUEIDENTIFIER NULL,
        ApprovalPolicy INT NOT NULL DEFAULT 1, -- 0=AllMustApprove, 1=AnyOneApprove
        ApproversJson NVARCHAR(4000) NULL,
        ActivityId NVARCHAR(128) NULL,
        StageType NVARCHAR(64) NULL,
        ActionsJson NVARCHAR(MAX) NULL,
        CreatedAtUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_WorkflowSteps_Workflow FOREIGN KEY (WorkflowId) REFERENCES workflow.Workflows(Id) ON DELETE CASCADE,
        INDEX IX_WorkflowSteps_WorkflowId_Order NONCLUSTERED (WorkflowId, [Order])
    );
END
ELSE
BEGIN
    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('workflow.WorkflowSteps') AND name = 'ApprovedNextStepId')
    BEGIN
        ALTER TABLE workflow.WorkflowSteps ADD
            ApprovedNextStepId UNIQUEIDENTIFIER NULL,
            RejectedNextStepId UNIQUEIDENTIFIER NULL,
            ApprovalPolicy INT NOT NULL DEFAULT 1,
            ApproversJson NVARCHAR(4000) NULL;
    END
    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('workflow.WorkflowSteps') AND name = 'ActivityId')
    BEGIN
        ALTER TABLE workflow.WorkflowSteps ADD ActivityId NVARCHAR(128) NULL;
    END
    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('workflow.WorkflowSteps') AND name = 'StageType')
    BEGIN
        ALTER TABLE workflow.WorkflowSteps ADD StageType NVARCHAR(64) NULL;
    END
    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('workflow.WorkflowSteps') AND name = 'ActionsJson')
    BEGIN
        ALTER TABLE workflow.WorkflowSteps ADD ActionsJson NVARCHAR(MAX) NULL;
    END
END
GO

-- WorkflowInstanceLookup: Maps InstanceId -> WorkflowId for per-workflow tables.
-- Used for inbox/sent/completed queries. Instances live in WorkflowInstances_{suffix}.
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'WorkflowInstanceLookup' AND schema_id = SCHEMA_ID('workflow'))
BEGIN
    CREATE TABLE workflow.WorkflowInstanceLookup (
        InstanceId UNIQUEIDENTIFIER PRIMARY KEY,
        WorkflowId UNIQUEIDENTIFIER NOT NULL,
        TenantId UNIQUEIDENTIFIER NOT NULL,
        WorkflowName NVARCHAR(256) NOT NULL,
        Status INT NOT NULL,
        AssignedToUserId UNIQUEIDENTIFIER NULL,
        StartedBy UNIQUEIDENTIFIER NOT NULL,
        CreatedAtUtc DATETIME2 NOT NULL,
        LastActivityAtUtc DATETIME2 NULL,
        CompletedAtUtc DATETIME2 NULL,
        IsArchived BIT NOT NULL DEFAULT 0,
        Priority INT NOT NULL DEFAULT 1,
        CurrentStepInstanceId UNIQUEIDENTIFIER NULL,
        SlaPriority INT NULL,
        ResponseStatus INT NULL,
        ResolutionStatus INT NULL,
        ResponseDeadline DATETIME2 NULL,
        ResolutionDeadline DATETIME2 NULL,
        IsEscalated BIT NOT NULL DEFAULT 0,
        INDEX IX_WorkflowInstanceLookup_WorkflowId NONCLUSTERED (WorkflowId),
        INDEX IX_WorkflowInstanceLookup_AssignedTo_Status NONCLUSTERED (AssignedToUserId, Status, IsArchived),
        INDEX IX_WorkflowInstanceLookup_StartedBy NONCLUSTERED (StartedBy, IsArchived),
        INDEX IX_WorkflowInstanceLookup_SlaBreach NONCLUSTERED (ResponseStatus, ResolutionStatus)
    );
END
GO

-- NOTE: WorkflowInstances and WorkflowStepInstances are PER-WORKFLOW (WorkflowInstances_{suffix}, WorkflowStepInstances_{suffix}).
-- Created by WorkflowTableCreator when a workflow is published.
-- WorkflowInstanceLookup above maps InstanceId -> WorkflowId for cross-workflow queries.
GO

-- =============================================
-- Approval & SLA Tables
-- =============================================

-- WorkflowApprovals table
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'WorkflowApprovals' AND schema_id = SCHEMA_ID('workflow'))
BEGIN
    CREATE TABLE workflow.WorkflowApprovals (
        Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        TenantId UNIQUEIDENTIFIER NOT NULL,
        WorkflowInstanceId UNIQUEIDENTIFIER NOT NULL,
        StepInstanceId UNIQUEIDENTIFIER NOT NULL,
        RequestedBy UNIQUEIDENTIFIER NOT NULL,
        AssignedToUserId UNIQUEIDENTIFIER NULL,
        AssignedToRole NVARCHAR(64) NULL,
        Status INT NOT NULL, -- 0=Pending, 1=Approved, 2=Rejected
        CreatedAtUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        RespondedAtUtc DATETIME2 NULL,
        RespondedBy UNIQUEIDENTIFIER NULL,
        Comments NVARCHAR(2000) NULL,
        INDEX IX_WorkflowApprovals_TenantId_AssignedToUserId_Status NONCLUSTERED (TenantId, AssignedToUserId, Status)
    );
END
GO

-- WorkflowSlas table (SLA policies)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'WorkflowSlas' AND schema_id = SCHEMA_ID('workflow'))
BEGIN
    CREATE TABLE workflow.WorkflowSlas (
        Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        TenantId UNIQUEIDENTIFIER NOT NULL,
        WorkflowId UNIQUEIDENTIFIER NOT NULL,
        Priority INT NOT NULL, -- 0=Low, 1=Normal, 2=High, 3=Critical
        ResponseTimeMinutes INT NOT NULL,
        ResolutionTimeMinutes INT NOT NULL,
        EscalationTimeMinutes INT NULL,
        EscalateToUserId UNIQUEIDENTIFIER NULL,
        EscalateToRole NVARCHAR(64) NULL,
        SendNotificationOnBreach BIT NOT NULL DEFAULT 1,
        NotificationEmails NVARCHAR(1000) NULL,
        CreatedAtUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        ModifiedAtUtc DATETIME2 NULL,
        CONSTRAINT FK_WorkflowSlas_Workflow FOREIGN KEY (WorkflowId) REFERENCES workflow.Workflows(Id) ON DELETE CASCADE,
        CONSTRAINT UQ_WorkflowSlas_WorkflowId UNIQUE (WorkflowId)
    );
END
GO

-- NOTE: WorkflowInstanceSlas is per-workflow (WorkflowInstanceSlas_{suffix}), created by WorkflowTableCreator.
GO

-- =============================================
-- Extended Feature Tables (per-workflow: WorkflowComments_{suffix}, etc. - created by WorkflowTableCreator)
-- =============================================

-- groupUser table (legacy group membership for workflow access checks)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'groupUser' AND schema_id = SCHEMA_ID('workflow'))
BEGIN
    CREATE TABLE workflow.groupUser (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        GroupId INT NOT NULL,
        UserId UNIQUEIDENTIFIER NOT NULL,
        CreatedAtUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy UNIQUEIDENTIFIER NULL,
        ModifiedAtUtc DATETIME2 NULL,
        ModifiedBy UNIQUEIDENTIFIER NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        INDEX IX_groupUser_GroupId_UserId_IsDeleted NONCLUSTERED (GroupId, UserId, IsDeleted)
    );
END
GO

-- jiraCreateIssue table (legacy jira integration links)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'jiraCreateIssue' AND schema_id = SCHEMA_ID('workflow'))
BEGIN
    CREATE TABLE workflow.jiraCreateIssue (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        WorkflowId UNIQUEIDENTIFIER NOT NULL,
        ProcessId INT NOT NULL,
        IssueId NVARCHAR(128) NULL,
        [Key] NVARCHAR(128) NULL,
        [Self] NVARCHAR(512) NULL,
        Assignee NVARCHAR(256) NULL,
        [Status] NVARCHAR(128) NULL,
        CreatedAtUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CreatedBy UNIQUEIDENTIFIER NULL,
        ModifiedAtUtc DATETIME2 NULL,
        ModifiedBy UNIQUEIDENTIFIER NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        INDEX IX_jiraCreateIssue_WorkflowId_ProcessId_IsDeleted NONCLUSTERED (WorkflowId, ProcessId, IsDeleted)
    );
END
GO

-- WorkflowUsers table (old API parity + workflow assignment)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'WorkflowUsers' AND schema_id = SCHEMA_ID('workflow'))
BEGIN
    CREATE TABLE workflow.WorkflowUsers (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        TenantId UNIQUEIDENTIFIER NULL,
        WorkflowId UNIQUEIDENTIFIER NOT NULL,
        UserId UNIQUEIDENTIFIER NULL,
        GroupId INT NULL,
        UserCategory NVARCHAR(128) NULL,
        CreatedAtUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        ModifiedAtUtc DATETIME2 NULL,
        CreatedBy UNIQUEIDENTIFIER NULL,
        ModifiedBy UNIQUEIDENTIFIER NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        INDEX IX_WorkflowUsers_WorkflowId_UserId_IsDeleted NONCLUSTERED (WorkflowId, UserId, IsDeleted),
        INDEX IX_WorkflowUsers_WorkflowId_GroupId_IsDeleted NONCLUSTERED (WorkflowId, GroupId, IsDeleted)
    );
END
GO

-- WorkflowSecurity table (old API parity)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'WorkflowSecurity' AND schema_id = SCHEMA_ID('workflow'))
BEGIN
    CREATE TABLE workflow.WorkflowSecurity (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        TenantId UNIQUEIDENTIFIER NULL,
        WorkflowId UNIQUEIDENTIFIER NOT NULL,
        UserId UNIQUEIDENTIFIER NOT NULL,
        UserCategory NVARCHAR(128) NULL,
        CreatedAtUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        ModifiedAtUtc DATETIME2 NULL,
        CreatedBy UNIQUEIDENTIFIER NULL,
        ModifiedBy UNIQUEIDENTIFIER NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        INDEX IX_WorkflowSecurity_WorkflowId_UserId_IsDeleted NONCLUSTERED (WorkflowId, UserId, IsDeleted)
    );
END
GO

-- WorkflowDocuments table (checklist)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'WorkflowDocuments' AND schema_id = SCHEMA_ID('workflow'))
BEGIN
    CREATE TABLE workflow.WorkflowDocuments (
        Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        TenantId UNIQUEIDENTIFIER NOT NULL,
        WorkflowId UNIQUEIDENTIFIER NOT NULL,
        WorkflowInstanceId UNIQUEIDENTIFIER NULL,
        FileName NVARCHAR(512) NOT NULL,
        Description NVARCHAR(2000) NULL,
        Type NVARCHAR(64) NULL,
        Status INT NOT NULL DEFAULT 0, -- 0=Pending, 1=Uploaded, 2=Approved, 3=Rejected
        IsMandatory BIT NOT NULL DEFAULT 0,
        FilePath NVARCHAR(1024) NULL,
        UploadedAt DATETIME2 NULL,
        CreatedAtUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        ModifiedAtUtc DATETIME2 NULL,
        CreatedBy UNIQUEIDENTIFIER NOT NULL,
        ModifiedBy UNIQUEIDENTIFIER NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        INDEX IX_WorkflowDocuments_TenantId_WorkflowId_IsDeleted NONCLUSTERED (TenantId, WorkflowId, IsDeleted),
        INDEX IX_WorkflowDocuments_TenantId_WorkflowInstanceId_IsDeleted NONCLUSTERED (TenantId, WorkflowInstanceId, IsDeleted)
    );
END
GO

-- WorkflowInitiateInfo: auto-initiation config (email, document, master form) per workflow
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'WorkflowInitiateInfo' AND schema_id = SCHEMA_ID('workflow'))
BEGIN
    CREATE TABLE workflow.WorkflowInitiateInfo (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        TenantId UNIQUEIDENTIFIER NOT NULL,
        WorkflowId UNIQUEIDENTIFIER NOT NULL,
        InputType NVARCHAR(256) NOT NULL,
        InputJson NVARCHAR(MAX) NULL,
        Status INT NOT NULL DEFAULT 0,
        Remarks NVARCHAR(2000) NOT NULL DEFAULT '',
        CreatedBy UNIQUEIDENTIFIER NOT NULL,
        CreatedAtUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        RepositoryId INT NULL,
        INDEX IX_WorkflowInitiateInfo_WorkflowId NONCLUSTERED (WorkflowId),
        INDEX IX_WorkflowInitiateInfo_TenantId_WorkflowId NONCLUSTERED (TenantId, WorkflowId)
    );
END
GO

-- =============================================
-- Workflow EF Migrations History (WorkflowDbContext expects workflow.__EFMigrationsHistory)
-- =============================================
IF NOT EXISTS (SELECT * FROM sys.tables t INNER JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE t.name = '__EFMigrationsHistory' AND s.name = 'workflow')
BEGIN
    CREATE TABLE workflow.[__EFMigrationsHistory] (
        [MigrationId] NVARCHAR(150) NOT NULL,
        [ProductVersion] NVARCHAR(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END
IF NOT EXISTS (SELECT 1 FROM workflow.[__EFMigrationsHistory] WHERE MigrationId = '20260226000001_WorkflowModuleComplete')
BEGIN
    INSERT INTO workflow.[__EFMigrationsHistory] (MigrationId, ProductVersion)
    VALUES ('20260226000001_WorkflowModuleComplete', '8.0.0');
END
GO

-- AP Agent job progress (Hangfire + Python PATCH polling)
IF OBJECT_ID(N'workflow.ApAgentJobProgress', N'U') IS NOT NULL
   AND COL_LENGTH(N'workflow.ApAgentJobProgress', N'ProgressPercent') IS NULL
BEGIN
    DROP TABLE workflow.ApAgentJobProgress;
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.tables t
    INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = N'workflow' AND t.name = N'ApAgentJobProgress')
BEGIN
    CREATE TABLE workflow.ApAgentJobProgress (
        JobId NVARCHAR(64) NOT NULL PRIMARY KEY,
        TenantId UNIQUEIDENTIFIER NOT NULL,
        WorkflowId UNIQUEIDENTIFIER NOT NULL,
        InstanceId UNIQUEIDENTIFIER NOT NULL,
        HangfireState NVARCHAR(32) NULL,
        Stage NVARCHAR(64) NULL,
        Message NVARCHAR(2000) NULL,
        ProgressPercent INT NULL,
        ErrorMessage NVARCHAR(MAX) NULL,
        FormData NVARCHAR(MAX) NULL,
        CreatedAtUtc DATETIME2 NOT NULL CONSTRAINT DF_ApAgentJobProgress_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAtUtc DATETIME2 NOT NULL CONSTRAINT DF_ApAgentJobProgress_UpdatedAt DEFAULT SYSUTCDATETIME()
    );
END
GO

IF EXISTS (
    SELECT 1
    FROM sys.tables t
    INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = N'workflow' AND t.name = N'ApAgentJobProgress')
   AND COL_LENGTH(N'workflow.ApAgentJobProgress', N'FormData') IS NULL
BEGIN
    ALTER TABLE workflow.ApAgentJobProgress ADD FormData NVARCHAR(MAX) NULL;
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    INNER JOIN sys.tables t ON t.object_id = i.object_id
    INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = N'workflow'
      AND t.name = N'ApAgentJobProgress'
      AND i.name = N'IX_ApAgentJobProgress_InstanceId_Updated')
BEGIN
    CREATE INDEX IX_ApAgentJobProgress_InstanceId_Updated
        ON workflow.ApAgentJobProgress (InstanceId, UpdatedAtUtc DESC);
END
GO

PRINT 'Workflow schema created successfully with all tables and temporal support.';
GO
