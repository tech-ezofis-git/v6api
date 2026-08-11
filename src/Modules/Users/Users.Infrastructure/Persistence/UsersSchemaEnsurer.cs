using Npgsql;
using Microsoft.EntityFrameworkCore;

namespace SaaSApp.Users.Infrastructure.Persistence;

/// <summary>Idempotent users schema patches for tenant provisioning and upgrades.</summary>
public static class UsersSchemaEnsurer
{
    // PHASE 4: users."Users" is 100% EF-managed on Postgres (Phase 2) -- all of these columns
    // already exist as properties on Users.Domain/Entities/User.cs, so `dotnet ef database
    // update` against UsersDbContext already creates users."Users" with them from the start.
    // Kept as a cheap idempotent safety net (ALTER TABLE IF EXISTS ... ADD COLUMN IF NOT EXISTS,
    // Postgres's native equivalent of the SQL Server COL_LENGTH-guarded ALTER) rather than
    // dropped outright, since older tenant DBs provisioned before a column was added still need it.
    internal const string EnsureExtendedUserColumnsSql = """
        ALTER TABLE IF EXISTS users."Users" ADD COLUMN IF NOT EXISTS "PasswordExpiryDays" integer NOT NULL DEFAULT 90;
        ALTER TABLE IF EXISTS users."Users" ADD COLUMN IF NOT EXISTS "AccountExpiryDate" timestamptz NULL;
        ALTER TABLE IF EXISTS users."Users" ADD COLUMN IF NOT EXISTS "ForcePasswordResetOnLogin" boolean NOT NULL DEFAULT false;
        ALTER TABLE IF EXISTS users."Users" ADD COLUMN IF NOT EXISTS "EmployeeId" varchar(128) NULL;
        ALTER TABLE IF EXISTS users."Users" ADD COLUMN IF NOT EXISTS "BusinessUnit" varchar(128) NULL;
        ALTER TABLE IF EXISTS users."Users" ADD COLUMN IF NOT EXISTS "Location" varchar(128) NULL;
        ALTER TABLE IF EXISTS users."Users" ADD COLUMN IF NOT EXISTS "GroupName" varchar(128) NULL;
        ALTER TABLE IF EXISTS users."Users" ADD COLUMN IF NOT EXISTS "MfaMethods" varchar(64) NULL;
        ALTER TABLE IF EXISTS users."Users" ADD COLUMN IF NOT EXISTS "Configuration" integer NOT NULL DEFAULT 0;
        """;

    public static async Task EnsureExtendedUserColumnsAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(EnsureExtendedUserColumnsSql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task EnsureExtendedUserColumnsAsync(
        UsersDbContext context,
        CancellationToken cancellationToken = default)
    {
        await context.Database.ExecuteSqlRawAsync(EnsureExtendedUserColumnsSql, cancellationToken);
    }

    // PHASE 4: users."Groups"/"UserGroups" are already created by the EF Postgres migration
    // (Migrations/20260810110117_InitialPostgres.cs). Kept as a defensive CREATE TABLE/INDEX IF
    // NOT EXISTS safety net for older tenant DBs provisioned before that migration existed.
    internal const string EnsureGroupsTablesSql = """
        CREATE TABLE IF NOT EXISTS users."Groups" (
            "Id" uuid NOT NULL,
            "TenantId" uuid NOT NULL,
            "Name" varchar(128) NOT NULL,
            "Description" varchar(512) NULL,
            "CreatedAtUtc" timestamptz NOT NULL,
            "IsDeleted" boolean NOT NULL,
            CONSTRAINT "PK_Groups" PRIMARY KEY ("Id")
        );

        CREATE TABLE IF NOT EXISTS users."UserGroups" (
            "GroupId" uuid NOT NULL,
            "UserId" uuid NOT NULL,
            "TenantId" uuid NOT NULL,
            CONSTRAINT "PK_UserGroups" PRIMARY KEY ("GroupId", "UserId"),
            CONSTRAINT "FK_UserGroups_Groups_GroupId" FOREIGN KEY ("GroupId")
                REFERENCES users."Groups" ("Id") ON DELETE CASCADE
        );

        CREATE UNIQUE INDEX IF NOT EXISTS "IX_Groups_TenantId_Name" ON users."Groups" ("TenantId", "Name");
        """;

    public static async Task EnsureGroupsTablesAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(EnsureGroupsTablesSql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task EnsureGroupsTablesAsync(
        UsersDbContext context,
        CancellationToken cancellationToken = default)
    {
        await context.Database.ExecuteSqlRawAsync(EnsureGroupsTablesSql, cancellationToken);
    }

    // PHASE 4: users."PermissionCategories" table/seed rows are already created by the EF
    // Postgres migration; the MERGE below is kept (ported to INSERT ... ON CONFLICT DO UPDATE)
    // as the ongoing idempotent sync for categories added/renamed in code after that migration
    // shipped -- this is genuinely live data-sync, not legacy-drift detection, so it stays.
    internal const string EnsurePermissionCategoriesSql = """
        CREATE TABLE IF NOT EXISTS users."PermissionCategories" (
            "Id" uuid NOT NULL,
            "Key" varchar(64) NOT NULL,
            "Name" varchar(128) NOT NULL,
            "SortOrder" integer NOT NULL,
            "IsActive" boolean NOT NULL DEFAULT true,
            CONSTRAINT "PK_PermissionCategories" PRIMARY KEY ("Id")
        );

        CREATE UNIQUE INDEX IF NOT EXISTS "IX_PermissionCategories_Key" ON users."PermissionCategories" ("Key");

        DELETE FROM users."PermissionCategories"
        WHERE "Id" IN (
            'a1000001-0000-4000-8000-000000000007'::uuid,
            'a1000001-0000-4000-8000-000000000008'::uuid);

        DELETE FROM users."PermissionCategories"
        WHERE "Key" IN (
            'invoices',
            'ocr-document-processing',
            'workflow-approvals',
            'reports-analytics',
            'user-management',
            'integrations',
            'system-settings')
           OR ("Key" = 'dashboard' AND "Id" = 'a1000001-0000-4000-8000-000000000001'::uuid);

        INSERT INTO users."PermissionCategories" ("Id", "Key", "Name", "SortOrder", "IsActive")
        VALUES
            ('a1000001-0000-4000-8000-000000000006'::uuid, 'dashboard', 'Dashboard', 1, true),
            ('a1000001-0000-4000-8000-000000000001'::uuid, 'workflow', 'Workflow', 2, true),
            ('a1000001-0000-4000-8000-000000000002'::uuid, 'folder', 'Folder', 3, true),
            ('a1000001-0000-4000-8000-000000000003'::uuid, 'task', 'Task', 4, true),
            ('a1000001-0000-4000-8000-000000000004'::uuid, 'workspace', 'Workspace', 5, true),
            ('a1000001-0000-4000-8000-000000000005'::uuid, 'settings', 'Settings', 6, true)
        ON CONFLICT ("Id") DO UPDATE SET
            "Key" = EXCLUDED."Key",
            "Name" = EXCLUDED."Name",
            "SortOrder" = EXCLUDED."SortOrder",
            "IsActive" = true
        WHERE users."PermissionCategories"."Key" <> EXCLUDED."Key"
           OR users."PermissionCategories"."Name" <> EXCLUDED."Name"
           OR users."PermissionCategories"."SortOrder" <> EXCLUDED."SortOrder"
           OR users."PermissionCategories"."IsActive" <> true;
        """;

    public static async Task EnsurePermissionCategoriesAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(EnsurePermissionCategoriesSql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task EnsurePermissionCategoriesAsync(
        UsersDbContext context,
        CancellationToken cancellationToken = default)
    {
        await context.Database.ExecuteSqlRawAsync(EnsurePermissionCategoriesSql, cancellationToken);
    }

    // PHASE 4: users."Menus" table/seed rows are already created by the EF Postgres migration;
    // kept for the same ongoing-seed reason as PermissionCategories above. ON CONFLICT ("Key")
    // DO NOTHING replaces the IF NOT EXISTS (SELECT ...) INSERT guard (Key has a unique index).
    internal const string EnsureMenusTablesSql = """
        CREATE TABLE IF NOT EXISTS users."Menus" (
            "Id" uuid NOT NULL,
            "Key" varchar(64) NOT NULL,
            "Label" varchar(128) NOT NULL,
            "RoutePath" varchar(256) NOT NULL,
            "SortOrder" integer NOT NULL,
            "IsSystem" boolean NOT NULL,
            "IsDeleted" boolean NOT NULL,
            "CreatedAtUtc" timestamptz NOT NULL,
            CONSTRAINT "PK_Menus" PRIMARY KEY ("Id")
        );

        CREATE UNIQUE INDEX IF NOT EXISTS "IX_Menus_Key" ON users."Menus" ("Key");

        INSERT INTO users."Menus" ("Id", "Key", "Label", "RoutePath", "SortOrder", "IsSystem", "IsDeleted", "CreatedAtUtc")
        VALUES
            ('b2000001-0000-4000-8000-000000000001'::uuid, 'dashboard', 'Dashboard', '/dashboard', 1, true, false, '2025-07-02T00:00:00'::timestamptz),
            ('b2000001-0000-4000-8000-000000000002'::uuid, 'inbox', 'Inbox', '/inbox', 2, true, false, '2025-07-02T00:00:00'::timestamptz),
            ('b2000001-0000-4000-8000-000000000003'::uuid, 'ocr-review', 'OCR.Review', '/ocr-review', 3, true, false, '2025-07-02T00:00:00'::timestamptz),
            ('b2000001-0000-4000-8000-000000000004'::uuid, 'processed-invoices', 'Processed Invoices', '/processed-invoices', 4, true, false, '2025-07-02T00:00:00'::timestamptz),
            ('b2000001-0000-4000-8000-000000000005'::uuid, 'approval-queue', 'Approval Queue', '/approval-queue', 5, true, false, '2025-07-02T00:00:00'::timestamptz),
            ('b2000001-0000-4000-8000-000000000006'::uuid, 'vendors', 'Vendors', '/vendors', 6, true, false, '2025-07-02T00:00:00'::timestamptz)
        ON CONFLICT ("Key") DO NOTHING;
        """;

    internal const string EnsureRoleMenusTableSql = """
        CREATE TABLE IF NOT EXISTS users."RoleMenus" (
            "RoleId" uuid NOT NULL,
            "MenuId" uuid NOT NULL,
            "TenantId" uuid NOT NULL,
            "IsDefaultLanding" boolean NOT NULL,
            CONSTRAINT "PK_RoleMenus" PRIMARY KEY ("RoleId", "MenuId"),
            CONSTRAINT "FK_RoleMenus_Menus_MenuId" FOREIGN KEY ("MenuId")
                REFERENCES users."Menus" ("Id") ON DELETE CASCADE,
            CONSTRAINT "FK_RoleMenus_Roles_RoleId" FOREIGN KEY ("RoleId")
                REFERENCES users."Roles" ("Id") ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS "IX_RoleMenus_MenuId" ON users."RoleMenus" ("MenuId");
        """;

    public static async Task EnsureMenusTablesAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(EnsureMenusTablesSql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task EnsureMenusTablesAsync(
        UsersDbContext context,
        CancellationToken cancellationToken = default)
    {
        await context.Database.ExecuteSqlRawAsync(EnsureMenusTablesSql, cancellationToken);
    }

    internal const string EnsureRoleMenusTablesSql = EnsureMenusTablesSql + EnsureRoleMenusTableSql;

    public static async Task EnsureRoleMenusTablesAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(EnsureRoleMenusTablesSql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task EnsureRoleMenusTablesAsync(
        UsersDbContext context,
        CancellationToken cancellationToken = default)
    {
        await context.Database.ExecuteSqlRawAsync(EnsureRoleMenusTablesSql, cancellationToken);
    }
}
