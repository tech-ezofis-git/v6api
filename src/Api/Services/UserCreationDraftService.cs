using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using SaaSApp.Catalog.Entities;
using SaaSApp.Catalog.Persistence;

namespace SaaSApp.Api.Services;

public interface IUserCreationDraftService
{
    Task<UserCreationDraftDto> SaveAsync(SaveUserCreationDraftRequest request, CancellationToken cancellationToken = default);
    Task<UserCreationDraftDto?> GetActiveAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default);
    Task<UserCreationDraftDto?> GetByIdAsync(Guid draftId, Guid tenantId, CancellationToken cancellationToken = default);
    Task<bool> CompleteAsync(Guid draftId, Guid tenantId, Guid userId, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid draftId, Guid tenantId, Guid userId, CancellationToken cancellationToken = default);
}

public sealed record SaveUserCreationDraftRequest(
    Guid TenantId,
    Guid UserId,
    int CurrentStep,
    string? CurrentStepKey = null,
    string? DraftJson = null,
    Guid? DraftId = null);

public sealed record UserCreationDraftDto(
    Guid Id,
    Guid TenantId,
    Guid UserId,
    int CurrentStep,
    string CurrentStepKey,
    string DraftJson,
    DateTime CreatedAtUtc,
    DateTime? ModifiedAtUtc,
    bool IsCompleted);

public sealed class UserCreationDraftService : IUserCreationDraftService
{
    public static readonly IReadOnlyList<string> StepKeys =
    [
        "loginDetails",
        "businessDetail",
        "groupAssignment",
        "authentication",
        "review"
    ];

    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "confirmPassword", "pwd", "secret", "temporaryPassword"
    };

    private readonly IDbContextFactory<CatalogDbContext> _dbFactory;
    private readonly IConfiguration _configuration;

    public UserCreationDraftService(
        IDbContextFactory<CatalogDbContext> dbFactory,
        IConfiguration configuration)
    {
        _dbFactory = dbFactory;
        _configuration = configuration;
    }

    public async Task<UserCreationDraftDto> SaveAsync(
        SaveUserCreationDraftRequest request,
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

        stepKey = StepKeys.First(k => string.Equals(k, stepKey, StringComparison.OrdinalIgnoreCase));

        var draftJson = SanitizeDraftJson(
            string.IsNullOrWhiteSpace(request.DraftJson) ? "{}" : request.DraftJson);

        await EnsureSchemaAsync(cancellationToken);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        UserCreationDraft? row = null;
        if (request.DraftId is { } draftId && draftId != Guid.Empty)
        {
            row = await db.UserCreationDrafts
                .FirstOrDefaultAsync(
                    d => d.Id == draftId
                         && d.TenantId == request.TenantId
                         && d.UserId == request.UserId
                         && !d.IsDeleted,
                    cancellationToken);
        }

        row ??= await db.UserCreationDrafts
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

        row = new UserCreationDraft
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
        db.UserCreationDrafts.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return Map(row);
    }

    public async Task<UserCreationDraftDto?> GetActiveAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || userId == Guid.Empty)
            return null;

        await EnsureSchemaAsync(cancellationToken);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.UserCreationDrafts
            .AsNoTracking()
            .Where(d => d.TenantId == tenantId
                        && d.UserId == userId
                        && !d.IsDeleted
                        && !d.IsCompleted)
            .OrderByDescending(d => d.ModifiedAtUtc ?? d.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return row == null ? null : Map(row);
    }

    public async Task<UserCreationDraftDto?> GetByIdAsync(
        Guid draftId,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        if (draftId == Guid.Empty || tenantId == Guid.Empty)
            return null;

        await EnsureSchemaAsync(cancellationToken);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.UserCreationDrafts
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
        var row = await db.UserCreationDrafts
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
        var row = await db.UserCreationDrafts
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

    /// <summary>Strip password / secret fields before persisting draft JSON.</summary>
    internal static string SanitizeDraftJson(string raw)
    {
        try
        {
            var node = JsonNode.Parse(raw);
            if (node == null)
                return "{}";
            StripSensitive(node);
            return node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        }
        catch (JsonException)
        {
            return "{}";
        }
    }

    private static void StripSensitive(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            var toRemove = obj
                .Where(p => SensitiveKeys.Contains(p.Key))
                .Select(p => p.Key)
                .ToList();
            foreach (var key in toRemove)
                obj.Remove(key);

            foreach (var prop in obj.ToList())
            {
                if (prop.Value != null)
                    StripSensitive(prop.Value);
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item != null)
                    StripSensitive(item);
            }
        }
    }

    private static UserCreationDraftDto Map(UserCreationDraft row) =>
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

        CREATE TABLE IF NOT EXISTS catalog."UserCreationDrafts" (
            "Id"              uuid NOT NULL CONSTRAINT "PK_UserCreationDrafts" PRIMARY KEY,
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

        CREATE INDEX IF NOT EXISTS "IX_UserCreationDrafts_Tenant_User"
            ON catalog."UserCreationDrafts" ("TenantId", "UserId", "IsDeleted", "IsCompleted");
        """;
}
