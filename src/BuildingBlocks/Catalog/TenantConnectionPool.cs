using Npgsql;

namespace SaaSApp.Catalog;

/// <summary>
/// Azure Postgres small tiers reserve the last slots for superusers (53300).
/// Keep every tenant consumer on one small pool so idle API connections are released.
/// </summary>
public static class TenantConnectionPool
{
    public const int MaxPoolSize = 8;

    public static string Apply(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = true,
            MaxPoolSize = MaxPoolSize,
            MinPoolSize = 0,
            ConnectionIdleLifetime = 20,
            Timeout = 15
        };
        return builder.ConnectionString;
    }
}
