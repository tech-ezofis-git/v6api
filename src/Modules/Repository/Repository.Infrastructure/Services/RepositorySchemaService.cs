using Npgsql;
using SaaSApp.Repository.Application.Contracts;

namespace SaaSApp.Repository.Infrastructure.Services;

public sealed class RepositorySchemaService : IRepositorySchemaService
{
    public async Task ApplyBaseSchemaAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        var script = await LoadScriptAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // Postgres port: no GO batch separators (SQL Server-only), and the ported script uses
        // native CREATE TABLE/INDEX IF NOT EXISTS throughout, so it's idempotent and safe to
        // run as a single multi-statement command rather than splitting into SQL-Server-style
        // batches.
        await using var command = new NpgsqlCommand(script, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string> LoadScriptAsync(CancellationToken cancellationToken)
    {
        var asm = typeof(RepositorySchemaService).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("CreateRepositorySchema.postgres.sql", StringComparison.OrdinalIgnoreCase));
        if (resourceName != null)
        {
            await using var stream = asm.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(cancellationToken);
        }

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "scripts", "postgres", "CreateRepositorySchema.sql"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "scripts", "postgres", "CreateRepositorySchema.sql"))
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return await File.ReadAllTextAsync(path, cancellationToken);
        }

        throw new FileNotFoundException("CreateRepositorySchema.sql (postgres) not found. Rebuild SaaSApp.Api.");
    }
}
