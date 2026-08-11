-- Backfill users.Users.Configuration existing rows 0 -> 1, for one tenant database -- Postgres
-- Ported from scripts/SetUserConfigurationDefaultOneAllTenants.sql -- Phase 3.
--
-- PARTIALLY SUPERSEDED, partially ported: the column-existence guard and the
-- DEFAULT-constraint manipulation are dropped -- users.Users is 100% EF-managed on
-- Postgres (Phase 2), Configuration already exists as a plain int column with no
-- Fluent-API-configured store default, so there is no SQL Server-style DEFAULT
-- constraint object to find/drop/recreate here. What's kept is the one genuinely
-- meaningful piece: the one-time DATA backfill (existing rows with Configuration = 0
-- get set to 1). This is an AllTenants-style script -- see
-- AddRepositoryFolderDocumentSecurityAllTenants.sql's header comment for why the
-- cross-tenant loop itself is Phase 6's job, not this file's; run this against ONE
-- tenant database.
--
-- Flag for a human: confirm this backfill (existing users default to "configuration
-- completed") is still the intended current behavior before running it against real
-- tenant data during Phase 7/9 -- it was not re-derived from any current C# business
-- rule, only carried over from the SQL Server script's stated intent.

UPDATE users."Users"
SET "Configuration" = 1
WHERE "Configuration" <> 1;
