-- =============================================
-- Repository module - base schema (tenant database) -- Postgres
-- Ported from scripts/CreateRepositorySchema.sql -- Phase 3.
-- STATIC repositories; uuid keys; per-repo Items_{suffix} created by API provisioner
-- (Repository.Infrastructure raw ADO.NET, Phase 4).
-- =============================================

CREATE SCHEMA IF NOT EXISTS repository;

CREATE TABLE IF NOT EXISTS repository."StorageProviders" (
    "Id" uuid NOT NULL DEFAULT gen_random_uuid() CONSTRAINT "PK_StorageProviders" PRIMARY KEY,
    "TenantId" uuid NOT NULL,
    "Code" varchar(32) NOT NULL,
    "Name" varchar(128) NOT NULL,
    "ConfigJson" text NULL,
    "IsActive" boolean NOT NULL DEFAULT true,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "ModifiedAtUtc" timestamptz NULL,
    "CreatedBy" uuid NULL,
    "ModifiedBy" uuid NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false,
    CONSTRAINT "UQ_StorageProviders_TenantId_Code" UNIQUE ("TenantId", "Code")
);
CREATE INDEX IF NOT EXISTS "IX_StorageProviders_TenantId_IsDeleted" ON repository."StorageProviders" ("TenantId", "IsDeleted");

CREATE TABLE IF NOT EXISTS repository."Repositories" (
    "Id" uuid NOT NULL DEFAULT gen_random_uuid() CONSTRAINT "PK_Repositories" PRIMARY KEY,
    "TenantId" uuid NOT NULL,
    "Name" varchar(256) NOT NULL,
    "Description" varchar(2000) NULL,
    "FieldsType" varchar(32) NOT NULL DEFAULT 'STATIC',
    "StorageProviderId" uuid NOT NULL REFERENCES repository."StorageProviders" ("Id"),
    "StorageDrive" varchar(500) NULL,
    "ItemsTableName" varchar(128) NOT NULL,
    "StageTableName" varchar(128) NOT NULL,
    "IsDefaultRepository" boolean NOT NULL DEFAULT true,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "ModifiedAtUtc" timestamptz NULL,
    "CreatedBy" uuid NULL,
    "ModifiedBy" uuid NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false,
    CONSTRAINT "CK_Repositories_FieldsType" CHECK ("FieldsType" = 'STATIC')
);
CREATE INDEX IF NOT EXISTS "IX_Repositories_TenantId_IsDeleted" ON repository."Repositories" ("TenantId", "IsDeleted");
CREATE UNIQUE INDEX IF NOT EXISTS "UX_Repositories_TenantId_Name" ON repository."Repositories" ("TenantId", "Name") WHERE "IsDeleted" = false;
ALTER TABLE repository."Repositories" ADD COLUMN IF NOT EXISTS "IsDefaultRepository" boolean NOT NULL DEFAULT true;

CREATE TABLE IF NOT EXISTS repository."RepositoryFields" (
    "Id" uuid NOT NULL DEFAULT gen_random_uuid() CONSTRAINT "PK_RepositoryFields" PRIMARY KEY,
    "RepositoryId" uuid NOT NULL REFERENCES repository."Repositories" ("Id") ON DELETE CASCADE,
    "Name" varchar(200) NOT NULL,
    "SqlColumnName" varchar(200) NOT NULL,
    "DataType" varchar(64) NULL,
    "Level" integer NOT NULL DEFAULT 0,
    "IsMandatory" boolean NOT NULL DEFAULT false,
    "IncludeInFolderStructure" boolean NOT NULL DEFAULT false,
    "OptionsJson" text NULL,
    "OrderId" integer NULL,
    "IsReadOnly" boolean NOT NULL DEFAULT false,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "ModifiedAtUtc" timestamptz NULL,
    "CreatedBy" uuid NULL,
    "ModifiedBy" uuid NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS "IX_RepositoryFields_RepositoryId_IsDeleted" ON repository."RepositoryFields" ("RepositoryId", "IsDeleted");

CREATE TABLE IF NOT EXISTS repository."Folders" (
    "Id" uuid NOT NULL DEFAULT gen_random_uuid() CONSTRAINT "PK_Folders" PRIMARY KEY,
    "TenantId" uuid NOT NULL,
    "RepositoryId" uuid NOT NULL REFERENCES repository."Repositories" ("Id") ON DELETE CASCADE,
    "Name" varchar(256) NOT NULL,
    "ParentId" uuid NULL,
    "LevelId" integer NOT NULL DEFAULT 0,
    "PathId" varchar(512) NULL,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "ModifiedAtUtc" timestamptz NULL,
    "CreatedBy" uuid NULL,
    "ModifiedBy" uuid NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS "IX_Folders_RepositoryId_ParentId_IsDeleted" ON repository."Folders" ("RepositoryId", "ParentId", "IsDeleted");

CREATE TABLE IF NOT EXISTS repository."SavedViews" (
    "Id" uuid NOT NULL DEFAULT gen_random_uuid() CONSTRAINT "PK_SavedViews" PRIMARY KEY,
    "TenantId" uuid NOT NULL,
    "RepositoryId" uuid NOT NULL REFERENCES repository."Repositories" ("Id") ON DELETE CASCADE,
    "UserId" uuid NOT NULL,
    "Name" varchar(256) NOT NULL,
    "FilterJson" text NOT NULL,
    "SortJson" text NULL,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "ModifiedAtUtc" timestamptz NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS "IX_SavedViews_RepositoryId_UserId" ON repository."SavedViews" ("RepositoryId", "UserId", "IsDeleted");

-- Optional item activity (timeline/comments). Not required for repository create or file upload.
CREATE TABLE IF NOT EXISTS repository."ItemTimelineEvents" (
    "Id" uuid NOT NULL DEFAULT gen_random_uuid() CONSTRAINT "PK_ItemTimelineEvents" PRIMARY KEY,
    "TenantId" uuid NOT NULL,
    "RepositoryId" uuid NOT NULL,
    "ItemId" uuid NOT NULL,
    "EventType" varchar(64) NOT NULL,
    "Title" varchar(500) NOT NULL,
    "Description" text NULL,
    "ActorType" varchar(64) NULL,
    "ActorName" varchar(256) NULL,
    "ActorUserId" uuid NULL,
    "CreatedBy" uuid NULL,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "IsDeleted" boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS "IX_ItemTimelineEvents_Item" ON repository."ItemTimelineEvents" ("TenantId", "RepositoryId", "ItemId", "IsDeleted", "CreatedAtUtc");

CREATE TABLE IF NOT EXISTS repository."ItemComments" (
    "Id" uuid NOT NULL DEFAULT gen_random_uuid() CONSTRAINT "PK_ItemComments" PRIMARY KEY,
    "TenantId" uuid NOT NULL,
    "RepositoryId" uuid NOT NULL,
    "ItemId" uuid NOT NULL,
    "Body" text NOT NULL,
    "CreatedBy" uuid NOT NULL,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "ModifiedAtUtc" timestamptz NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS "IX_ItemComments_Item" ON repository."ItemComments" ("TenantId", "RepositoryId", "ItemId", "IsDeleted", "CreatedAtUtc");

CREATE TABLE IF NOT EXISTS repository."FolderSecurityPolicies" (
    "Id" uuid NOT NULL CONSTRAINT "PK_FolderSecurityPolicies" PRIMARY KEY,
    "RepositoryId" uuid NOT NULL,
    "FolderId" uuid NULL,
    "CanView" boolean NOT NULL DEFAULT true,
    "CanUpload" boolean NOT NULL DEFAULT false,
    "CanDownload" boolean NOT NULL DEFAULT false,
    "CanPrint" boolean NOT NULL DEFAULT false,
    "CanDelete" boolean NOT NULL DEFAULT false,
    "CanEditMetadata" boolean NOT NULL DEFAULT false,
    "CanEditDocument" boolean NOT NULL DEFAULT false,
    "CanCheckOut" boolean NOT NULL DEFAULT false,
    "CanCheckIn" boolean NOT NULL DEFAULT false,
    "CanSendForSignature" boolean NOT NULL DEFAULT false,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "ModifiedAtUtc" timestamptz NULL,
    "CreatedBy" uuid NULL,
    "ModifiedBy" uuid NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS "IX_FolderSecurityPolicies_Repo_Folder" ON repository."FolderSecurityPolicies" ("RepositoryId", "FolderId", "IsDeleted");

CREATE TABLE IF NOT EXISTS repository."FolderSecurityPrincipals" (
    "Id" uuid NOT NULL CONSTRAINT "PK_FolderSecurityPrincipals" PRIMARY KEY,
    "PolicyId" uuid NOT NULL REFERENCES repository."FolderSecurityPolicies" ("Id") ON DELETE CASCADE,
    "PrincipalType" varchar(16) NOT NULL,
    "PrincipalId" uuid NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_FolderSecurityPrincipals_Policy" ON repository."FolderSecurityPrincipals" ("PolicyId");
CREATE INDEX IF NOT EXISTS "IX_FolderSecurityPrincipals_Principal" ON repository."FolderSecurityPrincipals" ("PrincipalType", "PrincipalId");

CREATE TABLE IF NOT EXISTS repository."DocumentSecurityRules" (
    "Id" uuid NOT NULL CONSTRAINT "PK_DocumentSecurityRules" PRIMARY KEY,
    "RepositoryId" uuid NOT NULL,
    "Action" varchar(16) NOT NULL,
    "MatchMode" varchar(8) NOT NULL DEFAULT 'all',
    "ConditionsJson" text NOT NULL,
    "SortOrder" integer NOT NULL DEFAULT 0,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "ModifiedAtUtc" timestamptz NULL,
    "CreatedBy" uuid NULL,
    "ModifiedBy" uuid NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS "IX_DocumentSecurityRules_Repo" ON repository."DocumentSecurityRules" ("RepositoryId", "IsDeleted", "SortOrder");

-- PHASE 4 FINDING: "Source" was never in this table's original CREATE TABLE either -- only
-- ever added via RepositorySecurityService.cs's inline COL_LENGTH-guarded ALTER at app
-- startup (same preexisting-drift pattern as RepositoryItemShares' 3 extra columns above).
ALTER TABLE repository."DocumentSecurityRules" ADD COLUMN IF NOT EXISTS "Source" varchar(32) NULL;

CREATE TABLE IF NOT EXISTS repository."DocumentSecurityPrincipals" (
    "Id" uuid NOT NULL CONSTRAINT "PK_DocumentSecurityPrincipals" PRIMARY KEY,
    "RuleId" uuid NOT NULL REFERENCES repository."DocumentSecurityRules" ("Id") ON DELETE CASCADE,
    "PrincipalType" varchar(16) NOT NULL,
    "PrincipalId" uuid NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_DocumentSecurityPrincipals_Rule" ON repository."DocumentSecurityPrincipals" ("RuleId");
CREATE INDEX IF NOT EXISTS "IX_DocumentSecurityPrincipals_Principal" ON repository."DocumentSecurityPrincipals" ("PrincipalType", "PrincipalId");

-- PHASE 4 FINDING: repository.ShareRecipients did not exist anywhere in this script either --
-- it too was only ever created via RepositorySecurityService.cs's inline idempotent DDL.
CREATE TABLE IF NOT EXISTS repository."ShareRecipients" (
    "Id" uuid NOT NULL CONSTRAINT "PK_ShareRecipients" PRIMARY KEY,
    "RepositoryId" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "CanUpload" boolean NOT NULL DEFAULT false,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "ModifiedAtUtc" timestamptz NULL,
    "CreatedBy" uuid NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_ShareRecipients_Repo_User" ON repository."ShareRecipients" ("RepositoryId", "UserId");

-- 'Repository base schema complete.'
