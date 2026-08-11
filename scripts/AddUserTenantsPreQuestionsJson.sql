/*
  Add onboarding pre-questions JSON storage to catalog.UserTenants.
  Run once on the CATALOG database.

  UserTenants.Id is the catalog membership row id.
  UserTenants.UserId stores the tenant users.Users.Id.

  Safe to re-run.
*/

IF SCHEMA_ID(N'catalog') IS NULL
    EXEC(N'CREATE SCHEMA [catalog];');

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'UserTenants' AND schema_id = SCHEMA_ID(N'catalog'))
BEGIN
    RAISERROR('Table [catalog].[UserTenants] does not exist. Run 01b_CreateCatalogTables.sql first.', 16, 1);
    RETURN;
END

IF COL_LENGTH('catalog.UserTenants', 'UserId') IS NULL
    ALTER TABLE [catalog].[UserTenants] ADD [UserId] uniqueidentifier NULL;

IF COL_LENGTH('catalog.UserTenants', 'PreQuestionsJson') IS NULL
    ALTER TABLE [catalog].[UserTenants] ADD [PreQuestionsJson] nvarchar(max) NULL;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_UserTenants_UserId_TenantId'
      AND object_id = OBJECT_ID(N'catalog.UserTenants'))
BEGIN
    CREATE INDEX [IX_UserTenants_UserId_TenantId]
        ON [catalog].[UserTenants] ([UserId], [TenantId]);
END

PRINT 'UserTenants UserId and PreQuestionsJson columns ensured.';
