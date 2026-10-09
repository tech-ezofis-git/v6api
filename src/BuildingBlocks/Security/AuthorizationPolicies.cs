using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace SaaSApp.Security;

/// <summary>
/// Policy-based authorization: Admin, TenantUser roles.
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
            .AddPolicy(TenantUser, policy =>
                policy.RequireRole("Admin", "TenantUser", "ExternalUser")
                    .RequireAuthenticatedUser()
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
                policy.RequireRole("Admin", "TenantUser", "ExternalUser")
                    .RequireAuthenticatedUser())
            .AddPolicy(AnyAuthenticated, policy =>
                policy.RequireAuthenticatedUser());
        }
        return services;
    }
}
