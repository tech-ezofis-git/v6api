-- Tenant: Email ingest mailbox config + processed message dedup -- Postgres
-- Ported from scripts/Create-EmailIngest-Tables.sql -- Phase 3.
-- Run against tenant database (or auto-created by EmailIngestService.EnsureSchemaAsync
-- once Phase 4 ports that file's SqlConnection/SqlCommand usage).

CREATE SCHEMA IF NOT EXISTS dbo;

CREATE TABLE IF NOT EXISTS dbo."EmailIngestMailbox" (
    "Id" uuid NOT NULL CONSTRAINT "PK_EmailIngestMailbox" PRIMARY KEY,
    "ConnectorId" uuid NOT NULL,
    "WorkflowId" uuid NOT NULL,
    "IsEnabled" boolean NOT NULL DEFAULT true,
    "PollIntervalMinutes" integer NOT NULL DEFAULT 5,
    "QueryFilter" varchar(512) NULL,
    "MasterSource" varchar(32) NOT NULL DEFAULT 'InternalForm',
    "MasterFormId" varchar(128) NULL,
    "MasterConnectorId" uuid NULL,
    "AttachmentExtensions" varchar(256) NOT NULL DEFAULT '.pdf,.tif,.tiff',
    "LastPolledAtUtc" timestamptz NULL,
    "LastError" varchar(2000) NULL,
    "CreatedAtUtc" timestamptz NOT NULL DEFAULT now(),
    "ModifiedAtUtc" timestamptz NULL,
    "CreatedBy" uuid NULL,
    "ModifiedBy" uuid NULL,
    "IsDeleted" boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS "IX_EmailIngestMailbox_Enabled" ON dbo."EmailIngestMailbox" ("IsEnabled", "IsDeleted") WHERE "IsDeleted" = false;

CREATE TABLE IF NOT EXISTS dbo."EmailIngestProcessed" (
    "Id" uuid NOT NULL CONSTRAINT "PK_EmailIngestProcessed" PRIMARY KEY,
    "MailboxId" uuid NOT NULL,
    "ProviderMessageId" varchar(450) NOT NULL,
    "AttachmentId" varchar(256) NOT NULL,
    "WorkflowInstanceId" uuid NULL,
    "ProcessedAtUtc" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "UQ_EmailIngestProcessed" UNIQUE ("MailboxId", "ProviderMessageId", "AttachmentId")
);
CREATE INDEX IF NOT EXISTS "IX_EmailIngestProcessed_Mailbox" ON dbo."EmailIngestProcessed" ("MailboxId", "ProcessedAtUtc" DESC);
