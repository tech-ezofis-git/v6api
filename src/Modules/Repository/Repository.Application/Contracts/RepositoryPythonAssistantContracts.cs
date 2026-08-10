namespace SaaSApp.Repository.Application.Contracts;

public sealed class RepositoryAssistantSearchRequest
{
    public string ActionFrom { get; set; } = "Repository";
    public string Query { get; set; } = string.Empty;
    public Guid? SpecificId { get; set; }
    public Guid? TenantId { get; set; }
}

public sealed class RepositoryAssistantChatbotRequest
{
    public string ActionFrom { get; set; } = "Repository";
    public string Message { get; set; } = string.Empty;
    public Guid? SpecificId { get; set; }
    public Guid? TenantId { get; set; }
    /// <summary>Optional. Prefer Authorization header; if set, forwarded in body to Python.</summary>
    public string? Token { get; set; }
}

public sealed record RepositoryPythonProxyResult(
    int StatusCode,
    string? ContentType,
    string Body);

public interface IRepositoryPythonAssistantClient
{
    Task<RepositoryPythonProxyResult> SearchAsync(
        RepositoryAssistantSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<RepositoryPythonProxyResult> ChatbotAsync(
        RepositoryAssistantChatbotRequest request,
        CancellationToken cancellationToken = default);
}
