using Npgsql;

namespace SaaSApp.Catalog;

/// <summary>
/// Caps Npgsql pooling for the shared catalog DB (Hangfire + EF catalog).
/// Azure Postgres reserves the last slots for superusers; uncapped pools (Npgsql default 100)
/// cause FATAL: remaining connection slots are reserved for roles with the SUPERUSER attribute.
/// </summary>
public static class CatalogConnectionPool
{
    public const int DefaultMaxPoolSize = 20;

    public static string Apply(string connectionString, int? maxPoolSize = null)
    {
        var max = Math.Clamp(maxPoolSize ?? DefaultMaxPoolSize, 2, 40);
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = true,
            MaxPoolSize = max,
            MinPoolSize = 0,
            ConnectionIdleLifetime = 30,
            Timeout = 15
        };
        // Avoid Command Timeout=0 holding catalog connections forever under load.
        if (builder.CommandTimeout is 0 or < 0)
            builder.CommandTimeout = 30;

        return builder.ConnectionString;
    }
}
