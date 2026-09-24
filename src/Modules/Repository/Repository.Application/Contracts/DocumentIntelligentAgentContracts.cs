namespace SaaSApp.Repository.Application.Contracts;

/// <summary>
/// Request for Document Intelligent Agent via agents <c>/chat</c>
/// (<c>intent=document_intelligent</c>). Provide <see cref="OcrText"/> and/or
/// <see cref="FilePath"/> and/or <see cref="FileBytes"/>.
/// </summary>
public sealed class DocumentIntelligentAgentRequest
{
    public string? SessionId { get; set; }

    /// <summary>Set by the API from access token / tenant context — not from the frontend.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>Supplied OCR text — preferred when already available.</summary>
    public string? OcrText { get; set; }

    /// <summary>Blob / storage path for agents to OCR (e.g. folder/invoice.pdf).</summary>
    public string? FilePath { get; set; }

    /// <summary>Optional page number when using filepath or file upload.</summary>
    public string? PageNo { get; set; }

    /// <summary>Optional Catalog / console model override.</summary>
    public string? Model { get; set; }

    /// <summary>Optional multipart file bytes (wins over filepath on agents).</summary>
    public byte[]? FileBytes { get; set; }

    public string? FileName { get; set; }

    public string? FileContentType { get; set; }

    /// <summary>
    /// When true (default), V6 loads the tenant repository catalog (id/name/fields)
    /// into <c>payload.repositories</c> for the agents matcher.
    /// </summary>
    public bool IncludeRepositoryCatalog { get; set; } = true;
}

/// <summary>Raw HTTP result from agents <c>/chat</c> (full envelope or unwrapped).</summary>
public sealed record DocumentIntelligentAgentProxyResult(
    int StatusCode,
    string? ContentType,
    string Body);

/// <summary>
/// Proxies Document Intelligent Agent to <c>Agents:ChatUrl</c> with
/// <c>intent=document_intelligent</c>. There is no agents <c>/document-intelligent</c> URL.
/// </summary>
public interface IDocumentIntelligentAgentClient
{
    Task<DocumentIntelligentAgentProxyResult> ClassifyAsync(
        DocumentIntelligentAgentRequest request,
        CancellationToken cancellationToken = default);
}
