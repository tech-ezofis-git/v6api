using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using SaaSApp.Catalog.Entities;
using SaaSApp.Catalog.Persistence;

namespace SaaSApp.Api.Services;

public interface IFolderCreationDraftService
{
    Task<FolderCreationDraftDto> SaveAsync(SaveFolderCreationDraftRequest request, CancellationToken cancellationToken = default);
    Task<FolderCreationDraftDto?> GetActiveAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default);
    Task<FolderCreationDraftDto?> GetByIdAsync(Guid draftId, Guid tenantId, CancellationToken cancellationToken = default);
    Task<bool> CompleteAsync(Guid draftId, Guid tenantId, Guid userId, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid draftId, Guid tenantId, Guid userId, CancellationToken cancellationToken = default);
}

public sealed record SaveFolderCreationDraftRequest(
    Guid TenantId,
    Guid UserId,
    int CurrentStep,
    string? CurrentStepKey = null,
    string? DraftJson = null,
    Guid? DraftId = null);

public sealed record FolderCreationDraftDto(
    Guid Id,
    Guid TenantId,
    Guid UserId,
    int CurrentStep,
    string CurrentStepKey,
    string DraftJson,
    DateTime CreatedAtUtc,
    DateTime? ModifiedAtUtc,
    bool IsCompleted);

public sealed class FolderCreationDraftService : IFolderCreationDraftService
{
    public static readonly IReadOnlyList<string> StepKeys =
    [
        "folderDetails",
        "fields",
        "storage",
        "versioning",
        "integrations"
    ];

    private readonly IDbContextFactory<CatalogDbContext> _dbFactory;
    private readonly IConfiguration _configuration;

    public FolderCreationDraftService(
        IDbContextFactory<CatalogDbContext> dbFactory,
        IConfiguration configuration)
    {
        _dbFactory = dbFactory;
        _configuration = configuration;
    }

    public async Task<FolderCreationDraftDto> SaveAsync(
        SaveFolderCreationDraftRequest request,
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
            throw new ArgumentException(
                $"CurrentStepKey must be one of: {string.Join(", ", StepKeys)}.");

        // Normalize casing to canonical key.
        stepKey = StepKeys.First(k => string.Equals(k, stepKey, StringComparison.OrdinalIgnoreCase));

        var draftJson = string.IsNullOrWhiteSpace(request.DraftJson) ? "{}" : request.DraftJson;

        await EnsureSchemaAsync(cancellationToken);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        FolderCreationDraft? row = null;
        if (request.DraftId is { } draftId && draftId != Guid.Empty)
        {
            row = await db.FolderCreationDrafts
                .FirstOrDefaultAsync(
                    d => d.Id == draftId
                         && d.TenantId == request.TenantId
                         && d.UserId == request.UserId
                         && !d.IsDeleted,
                    cancellationToken);
        }

        row ??= await db.FolderCreationDrafts
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

        row = new FolderCreationDraft
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
        db.FolderCreationDrafts.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return Map(row);
    }

    public async Task<FolderCreationDraftDto?> GetActiveAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || userId == Guid.Empty)
            return null;

        await EnsureSchemaAsync(cancellationToken);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.FolderCreationDrafts
            .AsNoTracking()
            .Where(d => d.TenantId == tenantId
                        && d.UserId == userId
                        && !d.IsDeleted
                        && !d.IsCompleted)
            .OrderByDescending(d => d.ModifiedAtUtc ?? d.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return row == null ? null : Map(row);
    }

    public async Task<FolderCreationDraftDto?> GetByIdAsync(
        Guid draftId,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        if (draftId == Guid.Empty || tenantId == Guid.Empty)
            return null;

        await EnsureSchemaAsync(cancellationToken);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.FolderCreationDrafts
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
        var row = await db.FolderCreationDrafts
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
        var row = await db.FolderCreationDrafts
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

    private static FolderCreationDraftDto Map(FolderCreationDraft row) =>
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

        CREATE TABLE IF NOT EXISTS catalog."FolderCreationDrafts" (
            "Id"              uuid NOT NULL CONSTRAINT "PK_FolderCreationDrafts" PRIMARY KEY,
            "TenantId"        uuid NOT NULL,
            "UserId"          uuid NOT NULL,
            "CurrentStep"     integer NOT NULL,
            "CurrentStepKey"  varchar(64) NOT NULL,
            "DraftJson"       text NOT NULL,
            "CreatedAtUtc"    timestamptz NOT NULL DEFAULT ((NOW() AT TIME ZONE 'utc')),
            "ModifiedAtUtc"   timestamptz NULL,
            "IsCompleted"     boolean NOT NULL DEFAULT false,
            "IsDeleted"       boolean NOT NULL DEFAULT false
        );

        CREATE INDEX IF NOT EXISTS "IX_FolderCreationDrafts_Tenant_User"
            ON catalog."FolderCreationDrafts" ("TenantId", "UserId", "IsDeleted", "IsCompleted");
        """;
}
