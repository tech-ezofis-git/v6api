using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace SaaSApp.Security;

/// <summary>
/// Policy-based authorization: Admin vs any signed-in tenant member.
/// </summary>
public static class AuthorizationPolicies
{
    public const string Admin = "Admin";
    public const string TenantUser = "TenantUser";
    public const string AnyAuthenticated = "AnyAuthenticated";

    public static IServiceCollection AddSaaSAppAuthorizationPolicies(this IServiceCollection services, string[]? authenticationSchemes = null)
    {
        var builder = services.AddAuthorizationBuilder();
        if (authenticationSchemes is { Length: > 0 })
        {
            builder.AddPolicy(Admin, policy =>
                policy.RequireRole("Admin")
                    .RequireAuthenticatedUser()
                    .AddAuthenticationSchemes(authenticationSchemes))
            // Any authenticated tenant JWT (builtin Admin/TenantUser/ExternalUser or a custom role like "FTL User").
            .AddPolicy(TenantUser, policy =>
                policy.RequireAuthenticatedUser()
                    .AddAuthenticationSchemes(authenticationSchemes))
            .AddPolicy(AnyAuthenticated, policy =>
                policy.RequireAuthenticatedUser()
                    .AddAuthenticationSchemes(authenticationSchemes));
        }
        else
        {
            builder.AddPolicy(Admin, policy =>
                policy.RequireRole("Admin")
                    .RequireAuthenticatedUser())
            .AddPolicy(TenantUser, policy =>
                policy.RequireAuthenticatedUser())
            .AddPolicy(AnyAuthenticated, policy =>
                policy.RequireAuthenticatedUser());
        }
        return services;
    }
}
