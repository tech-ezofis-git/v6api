namespace SaaSApp.Reporting.Infrastructure.Persistence;

internal static class ReportSchemaSql
{
    public const string Ensure = """
        CREATE SCHEMA IF NOT EXISTS reporting;

        CREATE TABLE IF NOT EXISTS reporting."ReportDefinitions" (
            "Id"              uuid NOT NULL CONSTRAINT "PK_ReportDefinitions" PRIMARY KEY,
            "TenantId"        uuid NOT NULL,
            "Name"            varchar(256) NOT NULL,
            "Domain"          varchar(256) NOT NULL,
            "Description"     text NULL,
            "SourceType"      varchar(64) NULL,
            "SourceFormId"    varchar(64) NULL,
            "WorkflowId"      uuid NULL,
            "Status"          varchar(32) NOT NULL,
            "Visibility"      varchar(64) NULL,
            "OwnerUserId"     uuid NOT NULL,
            "Scheduled"       boolean NOT NULL DEFAULT false,
            "Runs"            integer NOT NULL DEFAULT 0,
            "ConfigJson"      text NOT NULL,
            "HangfireJobId"   varchar(128) NULL,
            "CreatedAtUtc"    timestamptz NOT NULL DEFAULT now(),
            "ModifiedAtUtc"   timestamptz NULL,
            "CreatedBy"       uuid NOT NULL,
            "ModifiedBy"      uuid NULL,
            "IsDeleted"       boolean NOT NULL DEFAULT false
        );

        CREATE INDEX IF NOT EXISTS "IX_ReportDefinitions_Tenant"
            ON reporting."ReportDefinitions" ("TenantId", "IsDeleted", "Status", "Domain");
        CREATE INDEX IF NOT EXISTS "IX_ReportDefinitions_Owner"
            ON reporting."ReportDefinitions" ("TenantId", "OwnerUserId", "IsDeleted");

        CREATE TABLE IF NOT EXISTS reporting."ReportAiDefinitions" (
            "Id"              uuid NOT NULL CONSTRAINT "PK_ReportAiDefinitions" PRIMARY KEY,
            "TenantId"        uuid NOT NULL,
            "OwnerUserId"     uuid NOT NULL,
            "SessionId"       varchar(128) NOT NULL DEFAULT '',
            "Name"            varchar(256) NOT NULL,
            "Description"     text NOT NULL DEFAULT '',
            "ReportType"      varchar(64) NOT NULL,
            "WorkflowName"    varchar(256) NULL,
            "RepositoryName"  varchar(256) NULL,
            "ReportPrompt"    text NOT NULL DEFAULT '',
            "ReportSpecJson"  text NOT NULL DEFAULT '{}',
            "Status"          varchar(32) NOT NULL DEFAULT 'Draft',
            "Runs"            integer NOT NULL DEFAULT 0,
            "LastRunAtUtc"    timestamptz NULL,
            "CreatedAtUtc"    timestamptz NOT NULL DEFAULT now(),
            "ModifiedAtUtc"   timestamptz NULL,
            "CreatedBy"       uuid NOT NULL,
            "ModifiedBy"      uuid NULL,
            "IsDeleted"       boolean NOT NULL DEFAULT false
        );

        CREATE INDEX IF NOT EXISTS "IX_ReportAiDefinitions_Tenant"
            ON reporting."ReportAiDefinitions" ("TenantId", "IsDeleted", "ModifiedAtUtc");
        CREATE INDEX IF NOT EXISTS "IX_ReportAiDefinitions_Owner"
            ON reporting."ReportAiDefinitions" ("TenantId", "OwnerUserId", "IsDeleted");
        """;
}
