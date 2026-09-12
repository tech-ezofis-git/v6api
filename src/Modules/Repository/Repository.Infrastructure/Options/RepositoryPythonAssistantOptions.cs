namespace SaaSApp.Repository.Infrastructure.Options;

public sealed class RepositoryPythonAssistantOptions
{
    public const string SectionName = "RepositoryPythonAssistant";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Optional override for agents <c>/chat</c>. When empty, uses <c>Agents:ChatUrl</c>.
    /// </summary>
    public string ChatUrl { get; set; } = string.Empty;

    /// <summary>Agents intent for search (default <c>global_search</c>).</summary>
    public string SearchIntent { get; set; } = "global_search";

    /// <summary>Agents intent for chatbot (default <c>chatbot</c>).</summary>
    public string ChatbotIntent { get; set; } = "chatbot";

    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Legacy Azure Functions URL; ignored when it points at localhost:7071.</summary>
    public string SearchUrl { get; set; } = string.Empty;

    /// <summary>Legacy Azure Functions URL; ignored when it points at localhost:7071.</summary>
    public string ChatbotUrl { get; set; } = string.Empty;
}
