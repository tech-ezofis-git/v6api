using Microsoft.AspNetCore.Http;
using SaaSApp.Catalog;
using SaaSApp.MultiTenancy;

namespace SaaSApp.Api.Middleware;

/// <summary>
/// Resolves the current tenant's connection string from the catalog and sets it on ITenantConnectionProvider.
/// Must run after UseAuthentication so JWT (and thus TenantId) is available.
/// </summary>
public sealed class TenantConnectionMiddleware
{
    private readonly RequestDelegate _next;

    public TenantConnectionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        ITenantProvider tenantProvider,
        ITenantConnectionStringResolver resolver,
        ITenantConnectionProvider connectionProvider)
    {
        // OTP before signup: no tenant DB yet; catalog-only (ignore X-Tenant-Id / JWT tid).
        if (TenantSignupOtpPathHelper.Matches(context))
        {
            await _next(context);
            return;
        }

        var tenantId = tenantProvider.GetTenantId();

        if (tenantId is null)
        {
            // Auth endpoints require X-Tenant-Id header; fail fast before controller creation (avoids UsersDbContext resolution)
            var path = context.Request.Path.Value ?? "";
            if (path.Equals("/api/auth/ezofis/login", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/api/auth/2fa/complete", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/api/auth/social/login", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = "X-Tenant-Id header is required to select organization." });
                return;
            }
            await _next(context);
            return;
        }

        var connectionString = await resolver.GetConnectionStringAsync(tenantId.Value, context.RequestAborted);

        if (string.IsNullOrEmpty(connectionString))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { error = "Tenant not found or inactive." });
            return;
        }

        connectionProvider.SetConnectionString(TenantConnectionPool.Apply(connectionString));
        await _next(context);
    }
}
