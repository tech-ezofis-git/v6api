using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using SaaSApp.Catalog.Entities;
using SaaSApp.Catalog.Persistence;

namespace SaaSApp.Api.Services;

public interface IDashboardSchemaService
{
    Task<DashboardSchemaSnapshotDto> SaveAsync(
        SaveDashboardSchemaRequest request,
        CancellationToken cancellationToken = default);

    Task<DashboardSchemaSnapshotDto?> GetAsync(
        Guid tenantId,
        Guid? repositoryId,
        Guid? workflowId,
        CancellationToken cancellationToken = default);

    Task SaveHtmlAsync(
        SaveDashboardHtmlRequest request,
        CancellationToken cancellationToken = default);

    Task<DashboardHtmlSnapshotDto?> GetHtmlAsync(
        Guid tenantId,
        Guid? repositoryId,
        Guid? workflowId,
        CancellationToken cancellationToken = default);
}

public sealed record SaveDashboardSchemaRequest(
    Guid TenantId,
    Guid? RepositoryId,
    Guid? WorkflowId,
    string SchemaJson);

public sealed record SaveDashboardHtmlRequest(
    Guid TenantId,
    Guid? RepositoryId,
    Guid? WorkflowId,
    string DashboardHtml);

public sealed record DashboardSchemaSnapshotDto(
    Guid TenantId,
    Guid? RepositoryId,
    Guid? WorkflowId,
    string SchemaJson,
    string? DashboardHtml,
    DateTime CreatedAtUtc,
    DateTime? ModifiedAtUtc,
    DateTime? HtmlModifiedAtUtc);

public sealed record DashboardHtmlSnapshotDto(
    Guid TenantId,
    Guid? RepositoryId,
    Guid? WorkflowId,
    string DashboardHtml,
    DateTime? HtmlModifiedAtUtc);

public static class DashboardSchemaJsonResolver
{
    public static string ResolveSchemaJson(JsonElement? dashboardJson, JsonElement? dashboardResult)
    {
        if (dashboardJson is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null })
            return NormalizeJson(dashboardJson.Value);

        if (dashboardResult is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null })
            return NormalizeJson(dashboardResult.Value);

        throw new ArgumentException("dashboard_json or dashboard_result is required.");
    }

    public static (Guid? RepositoryId, Guid? WorkflowId) ExtractScopeIds(JsonElement schemaRoot)
    {
        Guid? repositoryId = null;
        Guid? workflowId = null;

        if (TryReadGuid(schemaRoot, "repository_id", out var repo) ||
            TryReadGuid(schemaRoot, "repositoryId", out repo))
            repositoryId = repo;

        if (TryReadGuid(schemaRoot, "workflow_id", out var workflow) ||
            TryReadGuid(schemaRoot, "workflowId", out workflow))
            workflowId = workflow;

        return (repositoryId, workflowId);
    }

    public static JsonElement ParseStoredSchema(string schemaJson)
    {
        using var doc = JsonDocument.Parse(schemaJson);
        return doc.RootElement.Clone();
    }

    private static string NormalizeJson(JsonElement element) =>
        JsonSerializer.Serialize(element);

    private static bool TryReadGuid(JsonElement root, string propertyName, out Guid value)
    {
        value = Guid.Empty;
        if (!root.TryGetProperty(propertyName, out var prop))
            return false;

        if (prop.ValueKind == JsonValueKind.String &&
            Guid.TryParse(prop.GetString(), out value))
            return value != Guid.Empty;

        return false;
    }
}

public sealed class DashboardSchemaService : IDashboardSchemaService
{
    private readonly IDbContextFactory<CatalogDbContext> _dbFactory;
    private readonly IConfiguration _configuration;

    public DashboardSchemaService(
        IDbContextFactory<CatalogDbContext> dbFactory,
        IConfiguration configuration)
    {
        _dbFactory = dbFactory;
        _configuration = configuration;
    }

    public async Task<DashboardSchemaSnapshotDto> SaveAsync(
        SaveDashboardSchemaRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.TenantId == Guid.Empty)
            throw new ArgumentException("tenant_id is required.");

        if (request.RepositoryId is null && request.WorkflowId is null)
            throw new ArgumentException("repository_id or workflow_id is required.");

        if (string.IsNullOrWhiteSpace(request.SchemaJson))
            throw new ArgumentException("dashboard_json is required.");

        NormalizeJson(request.SchemaJson);

        await EnsureSchemaAsync(cancellationToken);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var row = await FindActiveRowAsync(
            db,
            request.TenantId,
            request.RepositoryId,
            request.WorkflowId,
            cancellationToken);

        if (row != null)
        {
            row.SchemaJson = request.SchemaJson;
            row.RepositoryId = request.RepositoryId ?? row.RepositoryId;
            row.WorkflowId = request.WorkflowId ?? row.WorkflowId;
            row.ModifiedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return Map(row);
        }

        row = new DashboardSchemaSnapshot
        {
            Id = Guid.NewGuid(),
            TenantId = request.TenantId,
            RepositoryId = request.RepositoryId,
            WorkflowId = request.WorkflowId,
            SchemaJson = request.SchemaJson,
            CreatedAtUtc = DateTime.UtcNow,
            ModifiedAtUtc = DateTime.UtcNow,
            IsDeleted = false
        };
        db.DashboardSchemaSnapshots.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return Map(row);
    }

    public async Task SaveHtmlAsync(
        SaveDashboardHtmlRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.TenantId == Guid.Empty)
            throw new ArgumentException("tenant_id is required.");

        if (request.RepositoryId is null && request.WorkflowId is null)
            throw new ArgumentException("repository_id or workflow_id is required.");

        if (string.IsNullOrWhiteSpace(request.DashboardHtml))
            throw new ArgumentException("dashboard_html is required.");

        await EnsureSchemaAsync(cancellationToken);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var row = await FindActiveRowAsync(
            db,
            request.TenantId,
            request.RepositoryId,
            request.WorkflowId,
            cancellationToken);

        var now = DateTime.UtcNow;
        if (row != null)
        {
            row.DashboardHtml = request.DashboardHtml;
            row.HtmlModifiedAtUtc = now;
            row.ModifiedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        row = new DashboardSchemaSnapshot
        {
            Id = Guid.NewGuid(),
            TenantId = request.TenantId,
            RepositoryId = request.RepositoryId,
            WorkflowId = request.WorkflowId,
            SchemaJson = "{}",
            DashboardHtml = request.DashboardHtml,
            CreatedAtUtc = now,
            ModifiedAtUtc = now,
            HtmlModifiedAtUtc = now,
            IsDeleted = false
        };
        db.DashboardSchemaSnapshots.Add(row);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<DashboardSchemaSnapshotDto?> GetAsync(
        Guid tenantId,
        Guid? repositoryId,
        Guid? workflowId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
            return null;

        if (repositoryId is null && workflowId is null)
            return null;

        await EnsureSchemaAsync(cancellationToken);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var row = await FindActiveRowAsync(db, tenantId, repositoryId, workflowId, cancellationToken);
        return row == null ? null : Map(row);
    }

    public async Task<DashboardHtmlSnapshotDto?> GetHtmlAsync(
        Guid tenantId,
        Guid? repositoryId,
        Guid? workflowId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetAsync(tenantId, repositoryId, workflowId, cancellationToken);
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.DashboardHtml))
            return null;

        return new DashboardHtmlSnapshotDto(
            snapshot.TenantId,
            snapshot.RepositoryId,
            snapshot.WorkflowId,
            snapshot.DashboardHtml,
            snapshot.HtmlModifiedAtUtc);
    }

    private static async Task<DashboardSchemaSnapshot?> FindActiveRowAsync(
        CatalogDbContext db,
        Guid tenantId,
        Guid? repositoryId,
        Guid? workflowId,
        CancellationToken cancellationToken)
    {
        var query = db.DashboardSchemaSnapshots
            .Where(s => s.TenantId == tenantId && !s.IsDeleted);

        if (repositoryId is { } repoId && repoId != Guid.Empty)
        {
            query = query.Where(s => s.RepositoryId == repoId);
        }
        else if (workflowId is { } wfId && wfId != Guid.Empty)
        {
            query = query.Where(s => s.WorkflowId == wfId);
        }
        else
        {
            return null;
        }

        return await query
            .OrderByDescending(s => s.ModifiedAtUtc ?? s.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static void NormalizeJson(string schemaJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(schemaJson);
            _ = doc.RootElement.ValueKind;
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"dashboard_json must be valid JSON: {ex.Message}", ex);
        }
    }

    private static DashboardSchemaSnapshotDto Map(DashboardSchemaSnapshot row) =>
        new(
            row.TenantId,
            row.RepositoryId,
            row.WorkflowId,
            row.SchemaJson,
            row.DashboardHtml,
            row.CreatedAtUtc,
            row.ModifiedAtUtc,
            row.HtmlModifiedAtUtc);

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

        CREATE TABLE IF NOT EXISTS catalog."DashboardSchemaSnapshots" (
            "Id"                uuid NOT NULL CONSTRAINT "PK_DashboardSchemaSnapshots" PRIMARY KEY,
            "TenantId"          uuid NOT NULL,
            "RepositoryId"      uuid NULL,
            "WorkflowId"        uuid NULL,
            "SchemaJson"        text NOT NULL,
            "DashboardHtml"     text NULL,
            "HtmlModifiedAtUtc" timestamptz NULL,
            "CreatedAtUtc"      timestamptz NOT NULL DEFAULT now(),
            "ModifiedAtUtc"     timestamptz NULL,
            "IsDeleted"         boolean NOT NULL DEFAULT false
        );

        CREATE INDEX IF NOT EXISTS "IX_DashboardSchemaSnapshots_Tenant_Repository"
            ON catalog."DashboardSchemaSnapshots" ("TenantId", "RepositoryId", "IsDeleted");
        CREATE INDEX IF NOT EXISTS "IX_DashboardSchemaSnapshots_Tenant_Workflow"
            ON catalog."DashboardSchemaSnapshots" ("TenantId", "WorkflowId", "IsDeleted");

        CREATE UNIQUE INDEX IF NOT EXISTS "UX_DashboardSchemaSnapshots_Tenant_Repository_Active"
            ON catalog."DashboardSchemaSnapshots" ("TenantId", "RepositoryId")
            WHERE "RepositoryId" IS NOT NULL AND "IsDeleted" = false;
        CREATE UNIQUE INDEX IF NOT EXISTS "UX_DashboardSchemaSnapshots_Tenant_Workflow_Active"
            ON catalog."DashboardSchemaSnapshots" ("TenantId", "WorkflowId")
            WHERE "WorkflowId" IS NOT NULL AND "IsDeleted" = false;

        ALTER TABLE catalog."DashboardSchemaSnapshots"
            ADD COLUMN IF NOT EXISTS "DashboardHtml" text NULL;
        ALTER TABLE catalog."DashboardSchemaSnapshots"
            ADD COLUMN IF NOT EXISTS "HtmlModifiedAtUtc" timestamptz NULL;
        """;
}
