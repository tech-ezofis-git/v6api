using Npgsql;

namespace SaaSApp.Api.Services;

/// <summary>Applies workflow schema (workflow.Workflows, etc.) to a tenant database.</summary>
public interface IWorkflowSchemaService
{
    /// <summary>Apply workflow schema to the given connection string. Idempotent.</summary>
    Task ApplySchemaAsync(string connectionString, CancellationToken cancellationToken = default);
}

public sealed class WorkflowSchemaService : IWorkflowSchemaService
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
            // idempotent (object already exists)
        }
        catch (PostgresException ex)
        {
            throw new InvalidOperationException($"Workflow schema batch failed: {ex.Message}", ex);
        }
    }

    private static async Task<string> LoadScriptAsync(CancellationToken cancellationToken)
    {
        // 1. Embedded resource (most reliable)
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var resourceName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("CreateWorkflowSchemaComplete.sql"));
        if (resourceName != null)
        {
            await using var stream = asm.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(cancellationToken);
        }

        // 2. Output directory
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "scripts", "postgres", "CreateWorkflowSchemaComplete.sql");
        if (File.Exists(scriptPath))
            return await File.ReadAllTextAsync(scriptPath, cancellationToken);

        // 3. Current directory (e.g. solution root)
        scriptPath = Path.Combine(Directory.GetCurrentDirectory(), "scripts", "postgres", "CreateWorkflowSchemaComplete.sql");
        if (File.Exists(scriptPath))
            return await File.ReadAllTextAsync(scriptPath, cancellationToken);

        throw new FileNotFoundException("CreateWorkflowSchemaComplete.sql not found as embedded resource or in scripts/postgres/.");
    }
}
