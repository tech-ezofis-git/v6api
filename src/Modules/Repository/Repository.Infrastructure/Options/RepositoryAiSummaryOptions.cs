namespace SaaSApp.Repository.Infrastructure.Options;

public sealed class RepositoryAiSummaryOptions
{
    public const string SectionName = "Repository:AiSummary";

    public string ApiUrl { get; set; } = "http://localhost:8091/api/v1/summary";

    public int TimeoutMinutes { get; set; } = 5;

    public int Credit { get; set; } = 1;
}
