-- Catalog DB: playground API key -> tenant routing table -- Postgres
-- Ported from scripts/CreatePlaygroundApiKeyCatalog.sql -- Phase 3.

CREATE SCHEMA IF NOT EXISTS catalog;

CREATE TABLE IF NOT EXISTS catalog."PlaygroundApiKeyRoutes" (
    "ApiKey" varchar(128) NOT NULL CONSTRAINT "PK_PlaygroundApiKeyRoutes" PRIMARY KEY,
    "TenantId" uuid NOT NULL,
    "KeyId" uuid NOT NULL,
    "Email" varchar(256) NOT NULL,
    "IsActive" boolean NOT NULL DEFAULT true,
    "CreatedAtUtc" timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS "IX_PlaygroundApiKeyRoutes_TenantId" ON catalog."PlaygroundApiKeyRoutes" ("TenantId");
CREATE INDEX IF NOT EXISTS "IX_PlaygroundApiKeyRoutes_Email" ON catalog."PlaygroundApiKeyRoutes" ("Email");
