using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SaaSApp.Repository.Application.Contracts;
using SaaSApp.SharedKernel.Options;

namespace SaaSApp.Repository.Infrastructure.Services;

/// <summary>
/// Document Intelligent Agent — POST <c>Agents:ChatUrl</c> with
/// <c>intent=document_intelligent</c>; callers unwrap <c>document_intelligent_result</c>.
/// </summary>
public sealed class DocumentIntelligentAgentClient : IDocumentIntelligentAgentClient
{
    public const string IntentName = "document_intelligent";
    public const string ResultPropertyName = "document_intelligent_result";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly AgentsChatOptions _agentsChat;
    private readonly IStaticRepositoryProvisioner _provisioner;
    private readonly ILogger<DocumentIntelligentAgentClient> _logger;

    public DocumentIntelligentAgentClient(
        HttpClient httpClient,
        IOptions<AgentsChatOptions> agentsChat,
        IStaticRepositoryProvisioner provisioner,
        ILogger<DocumentIntelligentAgentClient> logger)
    {
        _httpClient = httpClient;
        _agentsChat = agentsChat.Value;
        _provisioner = provisioner;
        _logger = logger;
        if (_httpClient.Timeout < TimeSpan.FromMinutes(3))
            _httpClient.Timeout = TimeSpan.FromMinutes(5);
    }

    public async Task<DocumentIntelligentAgentProxyResult> ClassifyAsync(
        DocumentIntelligentAgentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var tenantId = request.TenantId
            ?? throw new ArgumentException("tenant_id is required.");

        var hasOcr = !string.IsNullOrWhiteSpace(request.OcrText);
        var hasPath = !string.IsNullOrWhiteSpace(request.FilePath);
        var hasFile = request.FileBytes is { Length: > 0 };
        if (!hasOcr && !hasPath && !hasFile)
            throw new ArgumentException("Provide ocr_text, filepath, or a file.");

        var apiUrl = _agentsChat.ResolveChatUrl();
        if (string.IsNullOrWhiteSpace(apiUrl))
            throw new InvalidOperationException(
                "Agents:ChatUrl is not configured. Set Agents:ChatUrl (e.g. https://cloud.ezofis.com/chat).");

        var sessionId = string.IsNullOrWhiteSpace(request.SessionId)
            ? $"doc-intel-{Guid.NewGuid():N}"
            : request.SessionId.Trim();

        IReadOnlyList<object>? repositories = null;
        if (request.IncludeRepositoryCatalog)
            repositories = await BuildRepositoryCatalogAsync(tenantId, cancellationToken);

        _logger.LogInformation(
            "Document Intelligent Agent /chat {Url} session={SessionId} tenant={TenantId} source={Source} repos={RepoCount}",
            apiUrl,
            sessionId,
            tenantId,
            hasFile ? "file" : hasOcr ? "ocr_text" : "filepath",
            repositories?.Count ?? 0);

        try
        {
            if (hasFile)
            {
                return await PostMultipartAsync(
                    apiUrl,
                    sessionId,
                    tenantId,
                    request,
                    repositories,
                    cancellationToken);
            }

            return await PostJsonAsync(
                apiUrl,
                sessionId,
                tenantId,
                request,
                repositories,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ArgumentException and not InvalidOperationException)
        {
            _logger.LogError(ex, "Document Intelligent Agent call to {Url} failed.", apiUrl);
            throw new InvalidOperationException(
                $"Document Intelligent Agent is unavailable: {ex.Message}", ex);
        }
    }

    private async Task<DocumentIntelligentAgentProxyResult> PostJsonAsync(
        string apiUrl,
        string sessionId,
        Guid tenantId,
        DocumentIntelligentAgentRequest request,
        IReadOnlyList<object>? repositories,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["tenant_id"] = tenantId.ToString("D")
        };

        if (!string.IsNullOrWhiteSpace(request.OcrText))
            payload["ocr_text"] = request.OcrText;

        if (!string.IsNullOrWhiteSpace(request.FilePath))
            payload["filepath"] = request.FilePath.Trim();

        if (!string.IsNullOrWhiteSpace(request.PageNo))
            payload["pageno"] = request.PageNo.Trim();

        if (!string.IsNullOrWhiteSpace(request.Model))
            payload["model"] = request.Model.Trim();

        if (repositories is { Count: > 0 })
            payload["repositories"] = repositories;

        // JSON path also needs a message when only catalog metadata would otherwise be sent.
        var message = !string.IsNullOrWhiteSpace(request.OcrText) || !string.IsNullOrWhiteSpace(request.FilePath)
            ? "Classify this document into the tenant repository catalog."
            : null;

        var body = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["session_id"] = sessionId,
            ["intent"] = IntentName,
            ["payload"] = payload
        };
        if (message != null)
            body["message"] = message;

        using var response = await _httpClient.PostAsync(
            apiUrl,
            new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json"),
            cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
        return new DocumentIntelligentAgentProxyResult((int)response.StatusCode, contentType, content);
    }

    private async Task<DocumentIntelligentAgentProxyResult> PostMultipartAsync(
        string apiUrl,
        string sessionId,
        Guid tenantId,
        DocumentIntelligentAgentRequest request,
        IReadOnlyList<object>? repositories,
        CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(sessionId), "session_id");
        form.Add(new StringContent(IntentName), "intent");
        // Shared /chat pydantic validator requires message|ocr_text|filepath|… — file alone is not enough.
        form.Add(
            new StringContent("Classify this document into the tenant repository catalog."),
            "message");

        var pageNo = string.IsNullOrWhiteSpace(request.PageNo) ? "1" : request.PageNo.Trim();
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["tenant_id"] = tenantId.ToString("D"),
            ["pageno"] = pageNo
        };
        if (!string.IsNullOrWhiteSpace(request.Model))
            payload["model"] = request.Model.Trim();
        if (repositories is { Count: > 0 })
            payload["repositories"] = repositories;

        // Nested payload JSON (same shape as JSON /chat) so agents maps tenant + catalog.
        form.Add(
            new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
            "payload");
        form.Add(new StringContent(tenantId.ToString("D")), "tenant_id");
        form.Add(new StringContent(pageNo), "pageno");

        var fileName = string.IsNullOrWhiteSpace(request.FileName) ? "document.pdf" : request.FileName.Trim();
        // Match OCR client: octet-stream. Agents binds UploadFile as upload_file and/or file.
        var bytes = request.FileBytes!;
        form.Add(CreateFilePart(bytes, fileName), "file", fileName);
        form.Add(CreateFilePart(bytes, fileName), "upload_file", fileName);

        using var response = await _httpClient.PostAsync(apiUrl, form, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var responseType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
        return new DocumentIntelligentAgentProxyResult((int)response.StatusCode, responseType, content);
    }

    private static ByteArrayContent CreateFilePart(byte[] bytes, string fileName)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return part;
    }

    private async Task<IReadOnlyList<object>> BuildRepositoryCatalogAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var summaries = await _provisioner.ListRepositoriesAsync(tenantId, cancellationToken);
        var catalog = new List<object>(summaries.Count);

        foreach (var summary in summaries)
        {
            var detail = await _provisioner.GetRepositoryAsync(summary.Id, tenantId, cancellationToken);
            var fields = (detail?.Fields ?? Array.Empty<RepositoryFieldDto>())
                .Select(f => new Dictionary<string, object?>
                {
                    ["name"] = f.Name,
                    ["sql_column_name"] = f.SqlColumnName,
                    ["data_type"] = f.DataType
                })
                .ToList();

            catalog.Add(new Dictionary<string, object?>
            {
                ["repository_id"] = summary.Id.ToString("D"),
                ["repository_name"] = summary.Name,
                ["fields"] = fields
            });
        }

        return catalog;
    }
}
