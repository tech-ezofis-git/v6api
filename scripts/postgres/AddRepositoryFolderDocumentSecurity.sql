-- =============================================
-- Repository Folder + Document Security tables -- Postgres
-- Ported from scripts/AddRepositoryFolderDocumentSecurity.sql -- Phase 3.
-- For ONE existing tenant database. Run against: TENANT database.
--
-- Creates: repository.FolderSecurityPolicies, repository.FolderSecurityPrincipals,
-- repository.DocumentSecurityRules (+ Source column), repository.DocumentSecurityPrincipals,
-- repository.ShareRecipients. First 4 tables also created by CreateRepositorySchema.sql
-- (postgres/) but WITHOUT the Source column or ShareRecipients table -- those two are
-- new here and not yet in that file, so this script adds real incremental content, not
-- just a redundant re-create.
-- =============================================

CREATE SCHEMA IF NOT EXISTS repository;

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

CREATE TABLE IF NOT EXISTS repository."DocumentSecurityPrincipals" (
    "Id" uuid NOT NULL CONSTRAINT "PK_DocumentSecurityPrincipals" PRIMARY KEY,
    "RuleId" uuid NOT NULL REFERENCES repository."DocumentSecurityRules" ("Id") ON DELETE CASCADE,
    "PrincipalType" varchar(16) NOT NULL,
    "PrincipalId" uuid NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_DocumentSecurityPrincipals_Rule" ON repository."DocumentSecurityPrincipals" ("RuleId");
CREATE INDEX IF NOT EXISTS "IX_DocumentSecurityPrincipals_Principal" ON repository."DocumentSecurityPrincipals" ("PrincipalType", "PrincipalId");

ALTER TABLE repository."DocumentSecurityRules" ADD COLUMN IF NOT EXISTS "Source" varchar(32) NULL;

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

-- 'Repository folder/document security schema complete.'
