-- Tenant DB: playground API keys + usage log -- Postgres
-- Ported from scripts/CreatePlaygroundApiKeySchema.sql -- Phase 3.

CREATE SCHEMA IF NOT EXISTS dbo;

CREATE TABLE IF NOT EXISTS dbo."playgroundApiKeys" (
    "Id" uuid NOT NULL CONSTRAINT "PK_playgroundApiKeys" PRIMARY KEY,
    "Email" varchar(256) NOT NULL,
    "ApiKey" varchar(128) NOT NULL,
    "KeyLabel" varchar(100) NULL,
    "ProtectedPassword" varchar(512) NULL,
    "CreatedAtUtc" timestamptz NOT NULL,
    "ExpiresAtUtc" timestamptz NULL,
    "IsActive" boolean NOT NULL DEFAULT true
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_playgroundApiKeys_ApiKey" ON dbo."playgroundApiKeys" ("ApiKey");
CREATE INDEX IF NOT EXISTS "IX_playgroundApiKeys_Email_CreatedAtUtc" ON dbo."playgroundApiKeys" ("Email", "CreatedAtUtc" DESC);

CREATE TABLE IF NOT EXISTS dbo."playgroundApiUsageLog" (
    "Id" uuid NOT NULL CONSTRAINT "PK_playgroundApiUsageLog" PRIMARY KEY,
    "ApiKeyId" uuid NOT NULL,
    "ApiKey" varchar(128) NOT NULL,
    "Email" varchar(256) NOT NULL,
    "Endpoint" varchar(512) NOT NULL,
    "HttpMethod" varchar(16) NOT NULL,
    "StatusCode" integer NOT NULL,
    "DurationMs" bigint NOT NULL,
    "RequestedAtUtc" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_playgroundApiUsageLog_ApiKeyId" ON dbo."playgroundApiUsageLog" ("ApiKeyId");
CREATE INDEX IF NOT EXISTS "IX_playgroundApiUsageLog_Email_RequestedAtUtc" ON dbo."playgroundApiUsageLog" ("Email", "RequestedAtUtc" DESC);
