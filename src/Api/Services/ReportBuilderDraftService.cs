using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using SaaSApp.Catalog.Entities;
using SaaSApp.Catalog.Persistence;

namespace SaaSApp.Api.Services;

public interface IReportBuilderDraftService
{
    Task<ReportBuilderDraftDto> SaveAsync(SaveReportBuilderDraftRequest request, CancellationToken cancellationToken = default);
    Task<ReportBuilderDraftDto?> GetActiveAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default);
    Task<ReportBuilderDraftDto?> GetByIdAsync(Guid draftId, Guid tenantId, CancellationToken cancellationToken = default);
    Task<bool> CompleteAsync(Guid draftId, Guid tenantId, Guid userId, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid draftId, Guid tenantId, Guid userId, CancellationToken cancellationToken = default);
}

public sealed record SaveReportBuilderDraftRequest(
    Guid TenantId,
    Guid UserId,
    int CurrentStep,
    string? CurrentStepKey = null,
    string? DraftJson = null,
    Guid? DraftId = null);

public sealed record ReportBuilderDraftDto(
    Guid Id,
    Guid TenantId,
    Guid UserId,
    int CurrentStep,
    string CurrentStepKey,
    string DraftJson,
    DateTime CreatedAtUtc,
    DateTime? ModifiedAtUtc,
    bool IsCompleted);

public sealed class ReportBuilderDraftService : IReportBuilderDraftService
{
    public static readonly IReadOnlyList<string> StepKeys =
    [
        "ai",
        "details",
        "fields",
        "filters",
        "schedule"
    ];

    private readonly IDbContextFactory<CatalogDbContext> _dbFactory;
    private readonly IConfiguration _configuration;

    public ReportBuilderDraftService(
        IDbContextFactory<CatalogDbContext> dbFactory,
        IConfiguration configuration)
    {
        _dbFactory = dbFactory;
        _configuration = configuration;
    }

    public async Task<ReportBuilderDraftDto> SaveAsync(
        SaveReportBuilderDraftRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.TenantId == Guid.Empty)
            throw new ArgumentException("TenantId is required.");
        if (request.UserId == Guid.Empty)
            throw new ArgumentException("UserId is required.");
        if (request.CurrentStep < 1 || request.CurrentStep > StepKeys.Count)
            throw new ArgumentException($"CurrentStep must be between 1 and {StepKeys.Count}.");

        var stepKey = string.IsNullOrWhiteSpace(request.CurrentStepKey)
            ? StepKeys[request.CurrentStep - 1]
            : request.CurrentStepKey.Trim();

        if (!StepKeys.Contains(stepKey, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"CurrentStepKey must be one of: {string.Join(", ", StepKeys)}.");

        stepKey = StepKeys.First(k => string.Equals(k, stepKey, StringComparison.OrdinalIgnoreCase));
        var draftJson = string.IsNullOrWhiteSpace(request.DraftJson) ? "{}" : request.DraftJson;

        await EnsureSchemaAsync(cancellationToken);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        ReportBuilderDraft? row = null;
        if (request.DraftId is { } draftId && draftId != Guid.Empty)
        {
            row = await db.ReportBuilderDrafts
                .FirstOrDefaultAsync(
                    d => d.Id == draftId
                         && d.TenantId == request.TenantId
                         && d.UserId == request.UserId
                         && !d.IsDeleted,
                    cancellationToken);
        }

        row ??= await db.ReportBuilderDrafts
            .Where(d => d.TenantId == request.TenantId
                        && d.UserId == request.UserId
                        && !d.IsDeleted
                        && !d.IsCompleted)
            .OrderByDescending(d => d.ModifiedAtUtc ?? d.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (row != null)
        {
            row.CurrentStep = request.CurrentStep;
            row.CurrentStepKey = stepKey;
            row.DraftJson = draftJson;
            row.ModifiedAtUtc = DateTime.UtcNow;
            row.IsCompleted = false;
            await db.SaveChangesAsync(cancellationToken);
            return Map(row);
        }

        row = new ReportBuilderDraft
        {
            Id = Guid.NewGuid(),
            TenantId = request.TenantId,
            UserId = request.UserId,
            CurrentStep = request.CurrentStep,
            CurrentStepKey = stepKey,
            DraftJson = draftJson,
            CreatedAtUtc = DateTime.UtcNow,
            ModifiedAtUtc = DateTime.UtcNow,
            IsCompleted = false,
            IsDeleted = false
        };
        db.ReportBuilderDrafts.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return Map(row);
    }

    public async Task<ReportBuilderDraftDto?> GetActiveAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || userId == Guid.Empty)
            return null;

        await EnsureSchemaAsync(cancellationToken);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.ReportBuilderDrafts
            .AsNoTracking()
            .Where(d => d.TenantId == tenantId
                        && d.UserId == userId
                        && !d.IsDeleted
                        && !d.IsCompleted)
            .OrderByDescending(d => d.ModifiedAtUtc ?? d.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return row == null ? null : Map(row);
    }

    public async Task<ReportBuilderDraftDto?> GetByIdAsync(
        Guid draftId,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        if (draftId == Guid.Empty || tenantId == Guid.Empty)
            return null;

        await EnsureSchemaAsync(cancellationToken);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.ReportBuilderDrafts
            .AsNoTracking()
            .FirstOrDefaultAsync(
                d => d.Id == draftId && d.TenantId == tenantId && !d.IsDeleted,
                cancellationToken);

        return row == null ? null : Map(row);
    }

    public async Task<bool> CompleteAsync(
        Guid draftId,
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.ReportBuilderDrafts
            .FirstOrDefaultAsync(
                d => d.Id == draftId
                     && d.TenantId == tenantId
                     && d.UserId == userId
                     && !d.IsDeleted,
                cancellationToken);
        if (row == null)
            return false;

        row.IsCompleted = true;
        row.ModifiedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(
        Guid draftId,
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.ReportBuilderDrafts
            .FirstOrDefaultAsync(
                d => d.Id == draftId
                     && d.TenantId == tenantId
                     && d.UserId == userId
                     && !d.IsDeleted,
                cancellationToken);
        if (row == null)
            return false;

        row.IsDeleted = true;
        row.ModifiedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static ReportBuilderDraftDto Map(ReportBuilderDraft row) =>
        new(
            row.Id,
            row.TenantId,
            row.UserId,
            row.CurrentStep,
            row.CurrentStepKey,
            row.DraftJson,
            row.CreatedAtUtc,
            row.ModifiedAtUtc,
            row.IsCompleted);

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

        CREATE TABLE IF NOT EXISTS catalog."ReportBuilderDrafts" (
            "Id"              uuid NOT NULL CONSTRAINT "PK_ReportBuilderDrafts" PRIMARY KEY,
            "TenantId"        uuid NOT NULL,
            "UserId"          uuid NOT NULL,
            "CurrentStep"     integer NOT NULL,
            "CurrentStepKey"  varchar(64) NOT NULL,
            "DraftJson"       text NOT NULL,
            "CreatedAtUtc"    timestamptz NOT NULL DEFAULT now(),
            "ModifiedAtUtc"   timestamptz NULL,
            "IsCompleted"     boolean NOT NULL DEFAULT false,
            "IsDeleted"       boolean NOT NULL DEFAULT false
        );

        CREATE INDEX IF NOT EXISTS "IX_ReportBuilderDrafts_Tenant_User"
            ON catalog."ReportBuilderDrafts" ("TenantId", "UserId", "IsDeleted", "IsCompleted");
        """;
}
