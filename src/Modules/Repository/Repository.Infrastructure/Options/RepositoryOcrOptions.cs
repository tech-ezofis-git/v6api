namespace SaaSApp.Repository.Infrastructure.Options;

/// <summary>Hardcoded OCR defaults. Agents URL comes from <c>Agents:ChatUrl</c>.</summary>
public static class RepositoryOcrDefaults
{
    public const string DefaultPageNo = "1";
    public const string Instruction = "Region: India. Normalize DATE fields to YYYY-MM-DD.";
    public const int TimeoutMinutes = 5;
}
