using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using SaaSApp.Catalog.Entities;
using SaaSApp.Catalog.Persistence;

namespace SaaSApp.Api.Services;

public interface IPortalJsonService
{
    Task<PortalJsonSnapshotDto> SaveAsync(SavePortalJsonRequest request, CancellationToken cancellationToken = default);
    Task<PortalJsonSnapshotDto?> GetForUserAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default);
}

public sealed record SavePortalJsonRequest(
    Guid TenantId,
    Guid UserId,
    string PortalJson);

public sealed record PortalJsonSnapshotDto(
    Guid TenantId,
    Guid UserId,
    string PortalJson,
    DateTime CreatedAtUtc,
    DateTime? ModifiedAtUtc);

public sealed class PortalJsonService : IPortalJsonService
{
    private readonly IDbContextFactory<CatalogDbContext> _dbFactory;
    private readonly IConfiguration _configuration;

    public PortalJsonService(
        IDbContextFactory<CatalogDbContext> dbFactory,
        IConfiguration configuration)
    {
        _dbFactory = dbFactory;
        _configuration = configuration;
    }

    public async Task<PortalJsonSnapshotDto> SaveAsync(
        SavePortalJsonRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.TenantId == Guid.Empty)
            throw new ArgumentException("TenantId is required.");
        if (request.UserId == Guid.Empty)
            throw new ArgumentException("UserId is required.");

        var portalJson = NormalizePortalJson(request.PortalJson);

        await EnsureSchemaAsync(cancellationToken);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var row = await db.PortalJsonSnapshots
            .Where(s => s.TenantId == request.TenantId
                        && s.UserId == request.UserId
                        && !s.IsDeleted)
            .OrderByDescending(s => s.ModifiedAtUtc ?? s.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (row != null)
        {
            row.PortalJson = portalJson;
            row.ModifiedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return Map(row);
        }

        row = new PortalJsonSnapshot
        {
            Id = Guid.NewGuid(),
            Token = GenerateToken(),
            TenantId = request.TenantId,
            UserId = request.UserId,
            PortalJson = portalJson,
            CreatedAtUtc = DateTime.UtcNow,
            ModifiedAtUtc = DateTime.UtcNow,
            IsDeleted = false
        };
        db.PortalJsonSnapshots.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return Map(row);
    }

    public async Task<PortalJsonSnapshotDto?> GetForUserAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || userId == Guid.Empty)
            return null;

        await EnsureSchemaAsync(cancellationToken);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.PortalJsonSnapshots
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId
                        && s.UserId == userId
                        && !s.IsDeleted)
            .OrderByDescending(s => s.ModifiedAtUtc ?? s.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return row == null ? null : Map(row);
    }

    private static string NormalizePortalJson(string? portalJson)
    {
        if (string.IsNullOrWhiteSpace(portalJson))
            return "{}";

        var trimmed = portalJson.Trim();
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            return JsonSerializer.Serialize(doc.RootElement);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"portalJson must be valid JSON: {ex.Message}", ex);
        }
    }

    private static string GenerateToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static PortalJsonSnapshotDto Map(PortalJsonSnapshot row) =>
        new(
            row.TenantId,
            row.UserId,
            row.PortalJson,
            row.CreatedAtUtc,
            row.ModifiedAtUtc);

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        var cs = _configuration.GetConnectionString("DefaultConnection")
            ?? _configuration.GetConnectionString("CatalogConnection")
            ?? throw new InvalidOperationException("Catalog connection string not configured.");

        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(EnsureSchemaSql, connection) { CommandTimeout = 120 };
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string EnsureSchemaSql = """
        CREATE SCHEMA IF NOT EXISTS catalog;

        CREATE TABLE IF NOT EXISTS catalog."PortalJsonSnapshots" (
            "Id"              uuid NOT NULL CONSTRAINT "PK_PortalJsonSnapshots" PRIMARY KEY,
            "Token"           varchar(64) NOT NULL,
            "TenantId"        uuid NOT NULL,
            "UserId"          uuid NOT NULL,
            "PortalJson"      text NOT NULL,
            "CreatedAtUtc"    timestamptz NOT NULL DEFAULT ((NOW() AT TIME ZONE 'utc')),
            "ModifiedAtUtc"   timestamptz NULL,
            "IsDeleted"       boolean NOT NULL DEFAULT false
        );

        CREATE UNIQUE INDEX IF NOT EXISTS "UX_PortalJsonSnapshots_Token"
            ON catalog."PortalJsonSnapshots" ("Token");

        CREATE INDEX IF NOT EXISTS "IX_PortalJsonSnapshots_Tenant_User"
            ON catalog."PortalJsonSnapshots" ("TenantId", "UserId", "IsDeleted");

        CREATE UNIQUE INDEX IF NOT EXISTS "UX_PortalJsonSnapshots_Tenant_User_Active"
            ON catalog."PortalJsonSnapshots" ("TenantId", "UserId")
            WHERE "IsDeleted" = false;
        """;
}
