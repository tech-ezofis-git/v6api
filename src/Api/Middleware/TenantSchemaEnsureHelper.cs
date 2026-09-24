using System.Collections.Concurrent;
using Npgsql;

namespace SaaSApp.Api.Middleware;

/// <summary>
/// Fast-path schema ensure: skip multi-thousand-line DDL when marker tables already exist.
/// Uses per-tenant semaphores to avoid duplicate apply on concurrent first requests.
/// </summary>
internal static class TenantSchemaEnsureHelper
{
    private static readonly ConcurrentDictionary<string, byte> Applied = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);

    public static Task EnsureWorkflowSchemaAsync(
        Guid tenantId,
        string connectionString,
        Func<Task> applySchema,
        CancellationToken cancellationToken) =>
        EnsureOnceAsync(
            tenantId,
            "workflow",
            connectionString,
            "SELECT 1 FROM information_schema.tables WHERE table_name = 'Workflows' AND table_schema = 'workflow'",
            applySchema,
            cancellationToken);

    public static Task EnsureDmsSchemaAsync(
        Guid tenantId,
        string connectionString,
        Func<Task> applySchema,
        CancellationToken cancellationToken) =>
        EnsureOnceAsync(
            tenantId,
            "dms",
            connectionString,
            "SELECT 1 FROM information_schema.tables WHERE table_name = 'Repository' AND table_schema = 'dms'",
            applySchema,
            cancellationToken);

    public static Task EnsureRepositorySchemaAsync(
        Guid tenantId,
        string connectionString,
        Func<Task> applySchema,
        CancellationToken cancellationToken) =>
        EnsureOnceAsync(
            tenantId,
            "repository",
            connectionString,
            "SELECT 1 FROM information_schema.tables WHERE table_name = 'Repositories' AND table_schema = 'repository'",
            applySchema,
            cancellationToken);

    public static Task EnsureActivityLogSchemaAsync(
        Guid tenantId,
        string connectionString,
        Func<Task> applySchema,
        CancellationToken cancellationToken) =>
        EnsureOnceAsync(
            tenantId,
            "activitylog",
            connectionString,
            "SELECT 1 FROM information_schema.tables WHERE table_name = 'ApiAccessLogs' AND table_schema = 'activitylog'",
            applySchema,
            cancellationToken);

    public static Task EnsureEventLogSchemaAsync(
        Guid tenantId,
        string connectionString,
        Func<Task> applySchema,
        CancellationToken cancellationToken) =>
        EnsureOnceAsync(
            tenantId,
            "activitylog-eventlogs",
            connectionString,
            "SELECT 1 FROM information_schema.tables WHERE table_name = 'EventLogs' AND table_schema = 'activitylog'",
            applySchema,
            cancellationToken);

    public static Task EnsurePermissionCategoriesAsync(
        Guid tenantId,
        string connectionString,
        Func<Task> applySchema,
        CancellationToken cancellationToken) =>
        EnsureOnceAsync(
            tenantId,
            // Require the full default catalog, including form, folder-create, portal, and report-builder.
            "users-permission-categories-v3",
            connectionString,
            """
            SELECT 1
            WHERE (
                SELECT COUNT(*)
                FROM users."PermissionCategories"
                WHERE "IsActive" = true
                  AND lower("Key") IN ('dashboard', 'workflow', 'folder', 'task', 'workspace', 'settings', 'form', 'folder-create', 'portal', 'report-builder')
            ) >= 10
            AND EXISTS (
                SELECT 1 FROM users."PermissionCategories"
                WHERE lower("Key") = 'folder-create' AND "Name" = 'Folder Create')
            AND EXISTS (
                SELECT 1 FROM users."PermissionCategories"
                WHERE lower("Key") = 'report-builder' AND "Name" = 'Report Builder')
            """,
            applySchema,
            cancellationToken);

    public static Task EnsureMenusTablesAsync(
        Guid tenantId,
        string connectionString,
        Func<Task> applySchema,
        CancellationToken cancellationToken) =>
        EnsureOnceAsync(
            tenantId,
            "users-menus-v2",
            connectionString,
            """
            SELECT 1
            WHERE (
                SELECT COUNT(*)
                FROM users."Menus"
                WHERE "IsDeleted" = false
                  AND lower("Key") IN ('form', 'folder-create', 'portal', 'report-builder')
            ) >= 4
            """,
            applySchema,
            cancellationToken);

    public static Task EnsureRoleMenusTablesAsync(
        Guid tenantId,
        string connectionString,
        Func<Task> applySchema,
        CancellationToken cancellationToken) =>
        EnsureOnceAsync(
            tenantId,
            "users-role-menus",
            connectionString,
            "SELECT 1 FROM information_schema.tables WHERE table_name = 'RoleMenus' AND table_schema = 'users'",
            applySchema,
            cancellationToken);

    public static Task EnsureExtendedUserColumnsAsync(
        Guid tenantId,
        string connectionString,
        Func<Task> applySchema,
        CancellationToken cancellationToken) =>
        EnsureOnceAsync(
            tenantId,
            "users-extended-columns",
            connectionString,
            """
            SELECT 1
            WHERE EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'users' AND table_name = 'Users' AND column_name = 'PasswordExpiryDays')
              AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'users' AND table_name = 'Users' AND column_name = 'AccountExpiryDate')
              AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'users' AND table_name = 'Users' AND column_name = 'ForcePasswordResetOnLogin')
              AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'users' AND table_name = 'Users' AND column_name = 'EmployeeId')
              AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'users' AND table_name = 'Users' AND column_name = 'BusinessUnit')
              AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'users' AND table_name = 'Users' AND column_name = 'Location')
              AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'users' AND table_name = 'Users' AND column_name = 'GroupName')
              AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'users' AND table_name = 'Users' AND column_name = 'MfaMethods')
            """,
            applySchema,
            cancellationToken);

    /// <summary>
    /// Seeds Admin/TenantUser roles and backfills any missing active permission categories and sidebar menus.
    /// Skips only when both builtins grant every active category and Admin has every seeded menu, including form, folder-create, portal, and report-builder.
    /// </summary>
    public static Task EnsureBuiltinRolesAsync(
        Guid tenantId,
        string connectionString,
        Func<Task> applySchema,
        CancellationToken cancellationToken) =>
        EnsureOnceAsync(
            tenantId,
            "users-builtin-roles-v4",
            connectionString,
            """
            SELECT 1
            WHERE EXISTS (
                SELECT 1 FROM users."Roles" r
                WHERE r."Name" = 'Admin' AND r."IsDeleted" = false
            )
            AND EXISTS (
                SELECT 1 FROM users."Roles" r
                WHERE r."Name" = 'TenantUser' AND r."IsDeleted" = false
            )
            AND NOT EXISTS (
                SELECT 1
                FROM users."PermissionCategories" pc
                WHERE pc."IsActive" = true
                  AND NOT EXISTS (
                      SELECT 1
                      FROM users."Roles" r
                      INNER JOIN users."RolePermissions" rp ON rp."RoleId" = r."Id"
                      WHERE r."Name" = 'Admin'
                        AND r."IsDeleted" = false
                        AND lower(rp."PermissionKey") = lower(pc."Key")
                  )
            )
            AND NOT EXISTS (
                SELECT 1
                FROM users."PermissionCategories" pc
                WHERE pc."IsActive" = true
                  AND NOT EXISTS (
                      SELECT 1
                      FROM users."Roles" r
                      INNER JOIN users."RolePermissions" rp ON rp."RoleId" = r."Id"
                      WHERE r."Name" = 'TenantUser'
                        AND r."IsDeleted" = false
                        AND lower(rp."PermissionKey") = lower(pc."Key")
                  )
            )
            AND EXISTS (
                SELECT 1 FROM information_schema.tables
                WHERE table_schema = 'users' AND table_name = 'RoleMenus')
            AND (
                SELECT COUNT(*)
                FROM users."Menus" m
                INNER JOIN users."RoleMenus" rm ON rm."MenuId" = m."Id"
                INNER JOIN users."Roles" r ON r."Id" = rm."RoleId"
                WHERE r."Name" = 'Admin'
                  AND r."IsDeleted" = false
                  AND m."IsDeleted" = false
            ) >= (
                SELECT COUNT(*)
                FROM users."Menus"
                WHERE "IsDeleted" = false
            )
            AND EXISTS (
                SELECT 1
                FROM users."Menus" m
                INNER JOIN users."RoleMenus" rm ON rm."MenuId" = m."Id"
                INNER JOIN users."Roles" r ON r."Id" = rm."RoleId"
                WHERE r."Name" = 'Admin'
                  AND r."IsDeleted" = false
                  AND lower(m."Key") = 'folder-create')
            AND EXISTS (
                SELECT 1
                FROM users."Menus" m
                INNER JOIN users."RoleMenus" rm ON rm."MenuId" = m."Id"
                INNER JOIN users."Roles" r ON r."Id" = rm."RoleId"
                WHERE r."Name" = 'Admin'
                  AND r."IsDeleted" = false
                  AND lower(m."Key") = 'report-builder')
            AND EXISTS (
                SELECT 1
                FROM users."Menus" m
                INNER JOIN users."RoleMenus" rm ON rm."MenuId" = m."Id"
                INNER JOIN users."Roles" r ON r."Id" = rm."RoleId"
                WHERE r."Name" = 'Admin'
                  AND r."IsDeleted" = false
                  AND lower(m."Key") = 'form')
            AND EXISTS (
                SELECT 1
                FROM users."Menus" m
                INNER JOIN users."RoleMenus" rm ON rm."MenuId" = m."Id"
                INNER JOIN users."Roles" r ON r."Id" = rm."RoleId"
                WHERE r."Name" = 'Admin'
                  AND r."IsDeleted" = false
                  AND lower(m."Key") = 'portal')
            """,
            applySchema,
            cancellationToken);

    private static async Task EnsureOnceAsync(
        Guid tenantId,
        string schemaKey,
        string connectionString,
        string existsSql,
        Func<Task> applySchema,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"{tenantId:N}:{schemaKey}";
        if (Applied.ContainsKey(cacheKey))
            return;

        if (await SchemaMarkerExistsAsync(connectionString, existsSql, cancellationToken))
        {
            Applied.TryAdd(cacheKey, 0);
            return;
        }

        var gate = Locks.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (Applied.ContainsKey(cacheKey))
                return;

            if (await SchemaMarkerExistsAsync(connectionString, existsSql, cancellationToken))
            {
                Applied.TryAdd(cacheKey, 0);
                return;
            }

            await applySchema();
            Applied.TryAdd(cacheKey, 0);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<bool> SchemaMarkerExistsAsync(
        string connectionString,
        string existsSql,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(existsSql, connection) { CommandTimeout = 5 };
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result != null && result != DBNull.Value;
    }
}
