using Npgsql;
using SaaSApp.MultiTenancy;
using SaaSApp.Repository.Application.Contracts;

namespace SaaSApp.Repository.Infrastructure.Services;

public sealed class RepositoryStorageSeedService : IRepositoryStorageSeedService
{
    private static readonly (string Code, string Name)[] Defaults =
    [
        ("EZOFIS", "EZOFIS Storage"),
        ("GCP", "Google Cloud Storage"),
        ("ONEDRIVE", "Microsoft OneDrive")
    ];

    private readonly ITenantConnectionProvider _connectionProvider;
    private readonly IRepositorySchemaService _schemaService;

    public RepositoryStorageSeedService(
        ITenantConnectionProvider connectionProvider,
        IRepositorySchemaService schemaService)
    {
        _connectionProvider = connectionProvider;
        _schemaService = schemaService;
    }

    public Task EnsureDefaultProvidersAsync(Guid tenantId, Guid? createdBy, CancellationToken cancellationToken = default)
    {
        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");
        return EnsureDefaultProvidersAsync(connectionString, tenantId, createdBy, cancellationToken);
    }

    public async Task EnsureDefaultProvidersAsync(string connectionString, Guid tenantId, Guid? createdBy, CancellationToken cancellationToken = default)
    {
        await _schemaService.ApplyBaseSchemaAsync(connectionString, cancellationToken);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        foreach (var (code, name) in Defaults)
        {
            const string sql = """
                INSERT INTO repository."StorageProviders" ("TenantId", "Code", "Name", "CreatedBy")
                SELECT @TenantId, @Code, @Name, @CreatedBy
                WHERE NOT EXISTS (
                    SELECT 1 FROM repository."StorageProviders"
                    WHERE "TenantId" = @TenantId AND "Code" = @Code AND "IsDeleted" = false);
                """;

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@TenantId", tenantId);
            cmd.Parameters.AddWithValue("@Code", code);
            cmd.Parameters.AddWithValue("@Name", name);
            cmd.Parameters.AddWithValue("@CreatedBy", (object?)createdBy ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<StorageProviderDto>> ListProvidersAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT "Id", "Code", "Name", "IsActive"
            FROM repository."StorageProviders"
            WHERE "TenantId" = @TenantId AND "IsDeleted" = false
            ORDER BY "Code";
            """;

        var list = new List<StorageProviderDto>();
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new StorageProviderDto(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3)));
        }

        return list;
    }

    public async Task<Guid> ResolveStorageProviderIdAsync(
        Guid tenantId,
        Guid? storageProviderId,
        string? storageProviderCode,
        CancellationToken cancellationToken = default)
    {
        if (storageProviderId is { } id && id != Guid.Empty)
            return id;

        if (string.IsNullOrWhiteSpace(storageProviderCode))
            throw new ArgumentException("Either storageProviderId (GUID) or storageProviderCode (e.g. EZOFIS) is required.");

        var connectionString = _connectionProvider.ConnectionString
            ?? throw new InvalidOperationException("Tenant connection string not resolved.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var code = storageProviderCode.Trim();
        var resolved = await TryResolveProviderIdAsync(connection, tenantId, code, cancellationToken);
        if (resolved is not null)
            return resolved.Value;

        // First create-repository call: seed EZOFIS / GCP / ONEDRIVE if none exist yet.
        await EnsureDefaultProvidersAsync(connectionString, tenantId, createdBy: null, cancellationToken);
        resolved = await TryResolveProviderIdAsync(connection, tenantId, code, cancellationToken);
        if (resolved is not null)
            return resolved.Value;

        throw new InvalidOperationException(
            $"Storage provider '{storageProviderCode}' not found. Use EZOFIS, GCP, or ONEDRIVE, or pass storageProviderId from GET /api/repositories/storage-providers.");
    }

    private static async Task<Guid?> TryResolveProviderIdAsync(
        NpgsqlConnection connection,
        Guid tenantId,
        string code,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT "Id" FROM repository."StorageProviders"
            WHERE "TenantId" = @TenantId AND "IsDeleted" = false AND UPPER("Code") = UPPER(@Code);
            """;

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@TenantId", tenantId);
        cmd.Parameters.AddWithValue("@Code", code);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        if (result is Guid g)
            return g;
        if (result is not null && Guid.TryParse(result.ToString(), out var parsed))
            return parsed;
        return null;
    }
}
