namespace SaaSApp.Repository.Infrastructure.Options;

/// <summary>Hardcoded AI summary defaults. Agents URL comes from <c>Agents:ChatUrl</c>.</summary>
public static class RepositoryAiSummaryDefaults
{
    public const int TimeoutMinutes = 5;
    public const int Credit = 1;
    public const int KeyFactsCount = 6;
    public const string PageNo = "1";
}
