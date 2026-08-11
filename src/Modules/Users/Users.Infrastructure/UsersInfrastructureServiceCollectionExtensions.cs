using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SaaSApp.MultiTenancy;
using SaaSApp.Users.Application.Contracts;
using SaaSApp.Users.Infrastructure.DomainEvents;
using SaaSApp.Users.Infrastructure.Jobs;
using SaaSApp.Users.Infrastructure.Persistence;

namespace SaaSApp.Users.Infrastructure;

public static class UsersInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddUsersInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var slowQueryThresholdMs = configuration.GetValue<int?>("Performance:SlowSqlThresholdMs") ?? 500;
        // Database-per-tenant: UsersDbContext is created per request with the tenant's connection string (set by middleware).
        services.AddScoped<UsersDbContext>(sp =>
        {
            var tenantConnection = sp.GetRequiredService<ITenantConnectionProvider>();
            var connectionString = tenantConnection.ConnectionString
                ?? throw new InvalidOperationException("Tenant connection string has not been set for this request. Ensure tenant resolution middleware runs and the tenant exists in the catalog.");
            var optionsBuilder = new DbContextOptionsBuilder<UsersDbContext>();
            optionsBuilder.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", UsersDbContext.SchemaName);
                npgsql.EnableRetryOnFailure(
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(30),
                    errorCodesToAdd: null);
            });
            optionsBuilder.LogTo(
                action: message =>
                {
                    if (!message.Contains("Executed DbCommand", StringComparison.Ordinal) ||
                        !TryGetCommandDurationMs(message, out var durationMs) ||
                        durationMs < slowQueryThresholdMs)
                    {
                        return;
                    }

                    Console.WriteLine($"SLOW SQL ({durationMs:F0}ms): {message}");
                },
                minimumLevel: LogLevel.Information);
            var tenantProvider = sp.GetRequiredService<ITenantProvider>();
            return new UsersDbContext(optionsBuilder.Options, tenantProvider);
        });

        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<IGroupRepository, GroupRepository>();
        services.AddScoped<IUsersSchemaEnsurer, UsersSchemaEnsurerService>();
        services.AddScoped<IBuiltinRoleProvisioning, BuiltinRoleProvisioning>();
        services.AddScoped<IMenuRepository, MenuRepository>();
        services.AddScoped<IPermissionCategoryRepository, PermissionCategoryRepository>();
        services.AddScoped<IUnitOfWork, UsersUnitOfWork>();
        services.AddScoped<DomainEventDispatcher>();
        services.AddScoped<IWelcomeEmailJobClient, WelcomeEmailJobClient>();

        return services;
    }

    private static bool TryGetCommandDurationMs(string message, out double durationMs)
    {
        durationMs = 0;
        var marker = "Executed DbCommand (";
        var start = message.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        start += marker.Length;
        var end = message.IndexOf("ms)", start, StringComparison.Ordinal);
        if (end < 0)
        {
            return false;
        }

        var raw = message[start..end].Trim();
        return double.TryParse(raw, out durationMs);
    }
}
