using Microsoft.EntityFrameworkCore;
using SaaSApp.MultiTenancy;
using SaaSApp.Users.Infrastructure.Persistence;

namespace SaaSApp.Api.Middleware;

/// <summary>
/// Ensures users schema objects exist before permission- and role-menu-related API calls.
/// Applies once per tenant (cached). Required for tenants created before these tables were added.
/// </summary>
public sealed class UsersPermissionSchemaEnsuringMiddleware
{
    private readonly RequestDelegate _next;

    public UsersPermissionSchemaEnsuringMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext context,
        ITenantProvider tenantProvider,
        ITenantConnectionProvider connectionProvider)
    {
        var path = context.Request.Path.Value;
        var method = context.Request.Method;
        var needsPermissionCategories = RequiresPermissionCategories(method, path);
        var needsBuiltinRoles = RequiresBuiltinRoles(method, path);
        var needsMenus = RequiresMenus(method, path);
        var needsRoleMenus = RequiresRoleMenus(method, path);
        var needsExtendedUserColumns = RequiresExtendedUserColumns(path);

        if (!needsPermissionCategories && !needsBuiltinRoles && !needsMenus && !needsRoleMenus && !needsExtendedUserColumns)
        {
            await _next(context);
            return;
        }

        var tenantId = tenantProvider.GetTenantId();
        var conn = connectionProvider.ConnectionString;
        if (tenantId == null || string.IsNullOrEmpty(conn))
        {
            await _next(context);
            return;
        }

        if (needsExtendedUserColumns)
        {
            await TenantSchemaEnsureHelper.EnsureExtendedUserColumnsAsync(
                tenantId.Value,
                conn,
                () => UsersSchemaEnsurer.EnsureExtendedUserColumnsAsync(conn, context.RequestAborted),
                context.RequestAborted);
        }

        if (needsPermissionCategories || needsBuiltinRoles)
        {
            await TenantSchemaEnsureHelper.EnsurePermissionCategoriesAsync(
                tenantId.Value,
                conn,
                () => UsersSchemaEnsurer.EnsurePermissionCategoriesAsync(conn, context.RequestAborted),
                context.RequestAborted);
        }

        if (needsBuiltinRoles)
        {
            await TenantSchemaEnsureHelper.EnsureBuiltinRolesAsync(
                tenantId.Value,
                conn,
                () => EnsureBuiltinRolesForTenantAsync(tenantId.Value, conn, context.RequestAborted),
                context.RequestAborted);
        }

        if (needsMenus)
        {
            await TenantSchemaEnsureHelper.EnsureMenusTablesAsync(
                tenantId.Value,
                conn,
                () => UsersSchemaEnsurer.EnsureMenusTablesAsync(conn, context.RequestAborted),
                context.RequestAborted);
        }

        if (needsRoleMenus)
        {
            await TenantSchemaEnsureHelper.EnsureRoleMenusTablesAsync(
                tenantId.Value,
                conn,
                () => UsersSchemaEnsurer.EnsureRoleMenusTablesAsync(conn, context.RequestAborted),
                context.RequestAborted);
        }

        await _next(context);
    }

    private static async Task EnsureBuiltinRolesForTenantAsync(
        Guid tenantId,
        string connectionString,
        CancellationToken cancellationToken)
    {
        var optionsBuilder = new DbContextOptionsBuilder<UsersDbContext>();
        optionsBuilder.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.MigrationsHistoryTable("__EFMigrationsHistory", UsersDbContext.SchemaName);
            npgsql.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(30),
                errorCodesToAdd: null);
        });
        await using var usersContext = new UsersDbContext(optionsBuilder.Options, new StaticTenantProvider(tenantId));
        await BuiltinRoleProvisioning.EnsureAsync(usersContext, tenantId, cancellationToken);
    }

    private static bool RequiresExtendedUserColumns(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        if (path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase))
            return true;

        if (path.Equals("/api/usersession", StringComparison.OrdinalIgnoreCase))
            return true;

        return path.StartsWith("/api/users", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresPermissionCategories(string method, string? path)
    {
        if (!HttpMethods.IsGet(method) || string.IsNullOrEmpty(path))
            return false;

        if (path.Equals("/api/usersession", StringComparison.OrdinalIgnoreCase))
            return true;

        if (path.Equals("/api/users/roles/permissions", StringComparison.OrdinalIgnoreCase))
            return true;

        return IsGetUserByIdPath(path);
    }

    private static bool RequiresBuiltinRoles(string method, string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        if (RequiresPermissionCategories(method, path))
            return true;

        return path.StartsWith("/api/users/roles", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresMenus(string method, string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        if (path.Equals("/api/users/menus", StringComparison.OrdinalIgnoreCase))
            return HttpMethods.IsGet(method) || HttpMethods.IsPost(method);

        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 4)
            return false;

        if (!segments[0].Equals("api", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!segments[1].Equals("users", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!segments[2].Equals("menus", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!Guid.TryParse(segments[3], out _))
            return false;

        return HttpMethods.IsGet(method)
            || HttpMethods.IsPut(method)
            || HttpMethods.IsDelete(method);
    }

    private static bool RequiresRoleMenus(string method, string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        if (!HttpMethods.IsGet(method) && !HttpMethods.IsPut(method))
            return false;

        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 5)
            return false;

        if (!segments[0].Equals("api", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!segments[1].Equals("users", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!segments[2].Equals("roles", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!segments[4].Equals("menus", StringComparison.OrdinalIgnoreCase))
            return false;

        return Guid.TryParse(segments[3], out _);
    }

    private static bool IsGetUserByIdPath(string path)
    {
        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 3)
            return false;

        if (!segments[0].Equals("api", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!segments[1].Equals("users", StringComparison.OrdinalIgnoreCase))
            return false;

        return Guid.TryParse(segments[2], out _);
    }
}
