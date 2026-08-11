using System.Text.Json;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using SaaSApp.Catalog.Entities;
using SaaSApp.Catalog.Persistence;

namespace SaaSApp.Catalog;

public sealed class UserTenantRegistry : IUserTenantRegistry
{
    private const string SqlErrorInvalidObjectName = PostgresErrorCodes.UndefinedTable;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly IDbContextFactory<CatalogDbContext> _catalogFactory;

    public UserTenantRegistry(IDbContextFactory<CatalogDbContext> catalogFactory)
    {
        _catalogFactory = catalogFactory;
    }

    public async Task AddOrUpdateAsync(
        string email,
        Guid tenantId,
        string role,
        Guid? userId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = email?.Trim() ?? throw new ArgumentNullException(nameof(email));
        if (string.IsNullOrEmpty(normalizedEmail))
            throw new ArgumentException("Email cannot be empty.", nameof(email));

        try
        {
            await using var context = await _catalogFactory.CreateDbContextAsync(cancellationToken);

            UserTenant? existing = null;
            if (userId.HasValue)
            {
                existing = await context.UserTenants
                    .FirstOrDefaultAsync(ut => ut.UserId == userId.Value && ut.TenantId == tenantId, cancellationToken);
            }

            existing ??= await context.UserTenants
                .FirstOrDefaultAsync(ut => ut.Email == normalizedEmail && ut.TenantId == tenantId, cancellationToken);

            if (existing != null)
            {
                existing.Role = role?.Trim() ?? existing.Role;
                existing.Email = normalizedEmail;
                if (userId.HasValue && existing.UserId == null)
                    existing.UserId = userId;
                await context.SaveChangesAsync(cancellationToken);
                return;
            }

            context.UserTenants.Add(new UserTenant
            {
                Id = Guid.NewGuid(),
                Email = normalizedEmail,
                TenantId = tenantId,
                Role = role?.Trim() ?? "TenantUser",
                UserId = userId,
                CreatedAtUtc = DateTime.UtcNow
            });
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == SqlErrorInvalidObjectName)
        {
            // catalog.UserTenants table not created yet; run migration or scripts/AddUserTenantsPreQuestionsJson.sql
        }
    }

    public async Task<UserPreQuestionsResponse?> GetPreQuestionsAsync(
        Guid userId,
        Guid tenantId,
        string email,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = await _catalogFactory.CreateDbContextAsync(cancellationToken);
            var row = await FindMembershipAsync(context, userId, tenantId, email, cancellationToken);
            if (row == null)
                return null;

            return new UserPreQuestionsResponse(
                userId,
                tenantId,
                DeserializeQuestions(row.PreQuestionsJson));
        }
        catch (PostgresException ex) when (ex.SqlState == SqlErrorInvalidObjectName)
        {
            return null;
        }
    }

    public async Task<bool> UpdatePreQuestionsAsync(
        Guid userId,
        Guid tenantId,
        string email,
        IReadOnlyList<PreQuestionAnswerDto> questions,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = email?.Trim() ?? throw new ArgumentNullException(nameof(email));
        if (string.IsNullOrEmpty(normalizedEmail))
            throw new ArgumentException("Email cannot be empty.", nameof(email));

        if (questions == null || questions.Count == 0)
            throw new ArgumentException("At least one question and answer is required.", nameof(questions));

        foreach (var item in questions)
        {
            if (string.IsNullOrWhiteSpace(item.Question))
                throw new ArgumentException("Each item must include a question.", nameof(questions));
            ValidateAnswer(item.Answer, nameof(questions));
        }

        var json = SerializeQuestions(questions);

        try
        {
            await using var context = await _catalogFactory.CreateDbContextAsync(cancellationToken);
            var row = await FindMembershipAsync(context, userId, tenantId, normalizedEmail, cancellationToken);
            if (row == null)
            {
                row = new UserTenant
                {
                    Id = Guid.NewGuid(),
                    Email = normalizedEmail,
                    TenantId = tenantId,
                    UserId = userId,
                    Role = "TenantUser",
                    CreatedAtUtc = DateTime.UtcNow,
                    PreQuestionsJson = json
                };
                context.UserTenants.Add(row);
            }
            else
            {
                row.PreQuestionsJson = json;
                row.Email = normalizedEmail;
                if (row.UserId == null)
                    row.UserId = userId;
            }

            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == SqlErrorInvalidObjectName)
        {
            return false;
        }
    }

    private static async Task<UserTenant?> FindMembershipAsync(
        CatalogDbContext context,
        Guid userId,
        Guid tenantId,
        string email,
        CancellationToken cancellationToken)
    {
        var byUserId = await context.UserTenants
            .FirstOrDefaultAsync(ut => ut.TenantId == tenantId && ut.UserId == userId, cancellationToken);
        if (byUserId != null)
            return byUserId;

        var byEmail = await context.UserTenants
            .FirstOrDefaultAsync(ut => ut.TenantId == tenantId && ut.Email == email, cancellationToken);
        if (byEmail == null)
            return null;

        if (byEmail.UserId == null)
            byEmail.UserId = userId;

        return byEmail;
    }

    private static string SerializeQuestions(IReadOnlyList<PreQuestionAnswerDto> questions)
    {
        var payload = questions
            .Select(q => new PreQuestionAnswerDto(q.Question.Trim(), q.Answer.Clone()))
            .ToList();

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static IReadOnlyList<PreQuestionAnswerDto> DeserializeQuestions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<PreQuestionAnswerDto>();

        try
        {
            var items = JsonSerializer.Deserialize<List<PreQuestionAnswerDto>>(json, JsonOptions);
            return items ?? new List<PreQuestionAnswerDto>();
        }
        catch (JsonException)
        {
            return Array.Empty<PreQuestionAnswerDto>();
        }
    }

    private static void ValidateAnswer(JsonElement answer, string paramName)
    {
        switch (answer.ValueKind)
        {
            case JsonValueKind.String:
                if (string.IsNullOrWhiteSpace(answer.GetString()))
                    throw new ArgumentException("Each answer string must be non-empty.", paramName);
                return;
            case JsonValueKind.Array:
                if (answer.GetArrayLength() == 0)
                    throw new ArgumentException("Each answer array must contain at least one value.", paramName);

                foreach (var item in answer.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                        throw new ArgumentException("Answer arrays must contain only non-empty string values.", paramName);
                }
                return;
            default:
                throw new ArgumentException("Each answer must be either a string or an array of strings.", paramName);
        }
    }
}
