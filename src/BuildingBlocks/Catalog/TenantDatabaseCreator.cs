using Npgsql;
using Microsoft.Extensions.Configuration;

namespace SaaSApp.Catalog;

public sealed class TenantDatabaseCreator : ITenantDatabaseCreator
{
    private readonly string _maintenanceConnectionString;

    /// <summary>Command timeout in seconds for CREATE DATABASE (managed Postgres can take 60+ seconds).</summary>
    private const int CreateDatabaseCommandTimeoutSeconds = 180;

    public TenantDatabaseCreator(IConfiguration configuration)
    {
        var catalogConnection = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DefaultConnection not found.");
        // Postgres has no `master` database -- admin operations (CREATE DATABASE, catalog
        // lookups) run against the `postgres` maintenance database instead.
        var builder = new NpgsqlConnectionStringBuilder(catalogConnection) { Database = "postgres" };
        if (builder.Timeout < 60)
            builder.Timeout = 60;
        // Postgres rejects CREATE DATABASE inside a transaction block. Npgsql does not
        // auto-open an implicit transaction for a bare command (only via explicit
        // BeginTransaction or an ambient TransactionScope, neither of which is used here),
        // so ExecuteNonQueryAsync below already runs CREATE DATABASE outside any transaction.
        // Enlist=false makes that explicit and prevents an ambient TransactionScope from
        // silently pulling this connection into one if this class is ever called from
        // within one.
        builder.Enlist = false;
        _maintenanceConnectionString = builder.ConnectionString;
    }

    public async Task<bool> DatabaseExistsAsync(string databaseName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
            return false;
        var safeName = new string(databaseName.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        if (string.IsNullOrEmpty(safeName))
            return false;
        await using var connection = new NpgsqlConnection(_maintenanceConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM pg_database WHERE datname = @name";
        cmd.Parameters.AddWithValue("@name", safeName);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result != null;
    }

    public async Task CreateDatabaseAsync(string databaseName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
            throw new ArgumentException("Database name is required.", nameof(databaseName));
        if (databaseName.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
            throw new ArgumentException("Database name must be alphanumeric or underscore.", nameof(databaseName));

        var safeName = new string(databaseName.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        if (string.IsNullOrEmpty(safeName))
            throw new ArgumentException("Invalid database name.", nameof(databaseName));
        // Postgres identifier length limit is 63 bytes (vs. SQL Server's 128) -- guard
        // against silent truncation rather than let Postgres accept a shortened name.
        if (System.Text.Encoding.UTF8.GetByteCount(safeName) > 63)
            throw new ArgumentException($"Database name '{safeName}' exceeds PostgreSQL's 63-byte identifier limit.", nameof(databaseName));

        await using var connection = new NpgsqlConnection(_maintenanceConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        // Double-quoted identifier (Postgres syntax) instead of SQL Server's [brackets].
        // No explicit transaction is opened anywhere in this method/class -- required,
        // since Postgres rejects CREATE DATABASE inside a transaction block.
        cmd.CommandText = $"CREATE DATABASE \"{safeName}\"";
        cmd.CommandTimeout = CreateDatabaseCommandTimeoutSeconds;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
