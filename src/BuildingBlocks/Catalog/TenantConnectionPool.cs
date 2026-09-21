using Npgsql;

namespace SaaSApp.Catalog;

/// <summary>
/// Azure Postgres small tiers reserve the last slots for superusers (53300).
/// Keep every tenant consumer on one small pool so idle API connections are released.
/// </summary>
public static class TenantConnectionPool
{
    /// <summary>Default per-tenant MaxPoolSize (lower than catalog; many tenants share one server).</summary>
    public const int MaxPoolSize = 5;

    public static string Apply(string connectionString, int? maxPoolSize = null)
    {
        var max = Math.Clamp(maxPoolSize ?? MaxPoolSize, 1, 15);
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = true,
            MaxPoolSize = max,
            MinPoolSize = 0,
            ConnectionIdleLifetime = 20,
            Timeout = 15
        };
        return builder.ConnectionString;
    }
}
