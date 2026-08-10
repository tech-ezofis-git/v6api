namespace SaaSApp.Repository.Infrastructure.Options;

public sealed class RepositoryPythonAssistantOptions
{
    public const string SectionName = "RepositoryPythonAssistant";

    public bool Enabled { get; set; } = true;

    public string SearchUrl { get; set; } = "http://localhost:7071/api/search";

    public string ChatbotUrl { get; set; } = "http://localhost:7071/api/chatbot";

    public int TimeoutSeconds { get; set; } = 120;
}
