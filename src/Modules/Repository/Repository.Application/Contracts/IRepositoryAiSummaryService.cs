namespace SaaSApp.Repository.Application.Contracts;

public interface IRepositoryAiSummaryService
{
    Task<AiSummaryResult> GetOrGenerateAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid itemId,
        CancellationToken cancellationToken = default);
}

public sealed record AiSummaryResult(string Output, bool WasGenerated);
