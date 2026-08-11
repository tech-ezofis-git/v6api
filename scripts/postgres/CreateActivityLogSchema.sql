-- Tenant DB: activity/event logging -- Postgres
-- Ported from scripts/CreateActivityLogSchema.sql -- Phase 3.

CREATE SCHEMA IF NOT EXISTS activitylog;

CREATE TABLE IF NOT EXISTS activitylog."ApiAccessLogs" (
    "Id" uuid NOT NULL CONSTRAINT "PK_ApiAccessLogs" PRIMARY KEY,
    "TenantId" uuid NOT NULL,
    "UserId" uuid NULL,
    "UserEmail" varchar(256) NULL,
    "HttpMethod" varchar(10) NOT NULL,
    "Path" varchar(512) NOT NULL,
    "QueryString" varchar(1024) NULL,
    "StatusCode" integer NOT NULL,
    "DurationMs" integer NOT NULL,
    "CorrelationId" varchar(64) NULL,
    "ClientIp" varchar(64) NULL,
    "UserAgent" varchar(512) NULL,
    "CreatedAtUtc" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_ApiAccessLogs_TenantId_CreatedAtUtc" ON activitylog."ApiAccessLogs" ("TenantId", "CreatedAtUtc" DESC);
CREATE INDEX IF NOT EXISTS "IX_ApiAccessLogs_TenantId_UserId_CreatedAtUtc" ON activitylog."ApiAccessLogs" ("TenantId", "UserId", "CreatedAtUtc" DESC);
CREATE INDEX IF NOT EXISTS "IX_ApiAccessLogs_TenantId_Path" ON activitylog."ApiAccessLogs" ("TenantId", "Path");

CREATE TABLE IF NOT EXISTS activitylog."EventLogs" (
    "Id" uuid NOT NULL CONSTRAINT "PK_EventLogs" PRIMARY KEY,
    "TenantId" uuid NOT NULL,
    "UserId" uuid NULL,
    "UserDisplayName" varchar(256) NULL,
    "UserEmail" varchar(256) NULL,
    "EventTitle" varchar(512) NOT NULL,
    "EventType" varchar(128) NOT NULL,
    "Category" varchar(64) NOT NULL,
    "Severity" varchar(32) NOT NULL,
    "IpAddress" varchar(64) NULL,
    "HttpMethod" varchar(10) NULL,
    "Path" varchar(512) NULL,
    "StatusCode" integer NULL,
    "CorrelationId" varchar(64) NULL,
    "CreatedAtUtc" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_EventLogs_TenantId_CreatedAtUtc" ON activitylog."EventLogs" ("TenantId", "CreatedAtUtc" DESC);
CREATE INDEX IF NOT EXISTS "IX_EventLogs_TenantId_Category_CreatedAtUtc" ON activitylog."EventLogs" ("TenantId", "Category", "CreatedAtUtc" DESC);
CREATE INDEX IF NOT EXISTS "IX_EventLogs_TenantId_Severity" ON activitylog."EventLogs" ("TenantId", "Severity");
