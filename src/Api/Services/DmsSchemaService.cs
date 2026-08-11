using Npgsql;

namespace SaaSApp.Api.Services;

/// <summary>Applies DMS schema to tenant database. Same pattern as WorkflowSchemaService.</summary>
public sealed class DmsSchemaService : IDmsSchemaService
{
    public async Task ApplySchemaAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        var script = await LoadScriptAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        try
        {
            // Ported Postgres script uses CREATE SCHEMA/TABLE/INDEX IF NOT EXISTS throughout
            // (no SQL Server "GO" batch separators), so it runs as a single multi-statement batch.
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
        catch (PostgresException ex)
        {
            throw new InvalidOperationException($"DMS schema batch failed: {ex.Message}", ex);
        }
    }

    private static async Task<string> LoadScriptAsync(CancellationToken cancellationToken)
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var resourceName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("CreateDmsSchema.sql"));
        if (resourceName != null)
        {
            await using var stream = asm.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(cancellationToken);
        }

        var scriptPath = Path.Combine(AppContext.BaseDirectory, "scripts", "postgres", "CreateDmsSchema.sql");
        if (File.Exists(scriptPath))
            return await File.ReadAllTextAsync(scriptPath, cancellationToken);

        scriptPath = Path.Combine(Directory.GetCurrentDirectory(), "scripts", "postgres", "CreateDmsSchema.sql");
        if (File.Exists(scriptPath))
            return await File.ReadAllTextAsync(scriptPath, cancellationToken);

        throw new FileNotFoundException("CreateDmsSchema.sql not found as embedded resource or in scripts/postgres/.");
    }
}
