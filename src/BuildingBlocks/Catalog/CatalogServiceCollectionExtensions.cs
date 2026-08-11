using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SaaSApp.Catalog.Persistence;
using SaaSApp.MultiTenancy;

namespace SaaSApp.Catalog;

public static class CatalogServiceCollectionExtensions
{
    public static IServiceCollection AddCatalog(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? configuration.GetConnectionString("CatalogConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' or 'CatalogConnection' not found for catalog.");

        services.AddDbContextFactory<CatalogDbContext>(options =>
        {
            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", CatalogDbContext.SchemaName);
                npgsql.EnableRetryOnFailure(
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(30),
                    errorCodesToAdd: null);
            });
            var slowQueryThresholdMs = configuration.GetValue<int?>("Performance:SlowSqlThresholdMs") ?? 500;
            options.LogTo(message =>
                {
                    if (!message.Contains("Executed DbCommand", StringComparison.Ordinal) ||
                        !TryGetCommandDurationMs(message, out var durationMs) ||
                        durationMs < slowQueryThresholdMs)
                    {
                        return;
                    }

                    Console.WriteLine($"SLOW SQL ({durationMs:F0}ms): {message}");
                },
                LogLevel.Information);
        });

        services.AddScoped<ITenantConnectionStringResolver, TenantConnectionStringResolver>();
        services.AddScoped<ITenantDisplayResolver, TenantDisplayResolver>();
        services.AddScoped<ITenantDatabaseCreator, TenantDatabaseCreator>();
        services.AddScoped<IUserTenantRegistry, UserTenantRegistry>();
        services.AddScoped<IConnectorProviderCatalog, ConnectorProviderCatalog>();

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
