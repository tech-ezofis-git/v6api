using Npgsql;
using SaaSApp.ActivityLog.Application.Contracts;

namespace SaaSApp.ActivityLog.Infrastructure.Services;

public sealed class ActivityLogSchemaService : IActivityLogSchemaService
{
    public async Task ApplyBaseSchemaAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        var script = await LoadScriptAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        try
        {
            // The ported Postgres script uses CREATE SCHEMA/TABLE/INDEX IF NOT EXISTS throughout
            // (no SQL Server "GO" batch separators), so it can run as a single multi-statement batch.
            await using var command = new NpgsqlCommand(script, connection) { CommandTimeout = 120 };
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState is
            PostgresErrorCodes.DuplicateTable or
            PostgresErrorCodes.DuplicateObject or
            PostgresErrorCodes.DuplicateSchema or
            PostgresErrorCodes.DuplicateColumn)
        {
            // idempotent
        }
    }

    private static async Task<string> LoadScriptAsync(CancellationToken cancellationToken)
    {
        var asm = typeof(ActivityLogSchemaService).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("CreateActivityLogSchema.sql", StringComparison.OrdinalIgnoreCase));
        if (resourceName != null)
        {
            await using var stream = asm.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(cancellationToken);
        }

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "scripts", "postgres", "CreateActivityLogSchema.sql"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "scripts", "postgres", "CreateActivityLogSchema.sql"))
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return await File.ReadAllTextAsync(path, cancellationToken);
        }

        throw new FileNotFoundException("CreateActivityLogSchema.sql not found. Rebuild SaaSApp.Api.");
    }
}
