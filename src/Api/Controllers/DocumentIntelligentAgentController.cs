using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaaSApp.MultiTenancy;
using SaaSApp.Repository.Application.Contracts;
using SaaSApp.Repository.Infrastructure.Services;
using SaaSApp.Security;

namespace SaaSApp.Api.Controllers;

/// <summary>
/// Document Intelligent Agent — proxies to agents <c>/chat</c> with
/// <c>intent=document_intelligent</c>. Returns only <c>document_intelligent_result</c>.
/// There is no agents <c>/document-intelligent</c> URL.
/// </summary>
[ApiController]
[Route("api/repositories/document-intelligent-agent")]
[Authorize(Policy = AuthorizationPolicies.TenantUser)]
public sealed class DocumentIntelligentAgentController : ControllerBase
{
    private readonly IDocumentIntelligentAgentClient _client;
    private readonly ITenantProvider _tenantProvider;

    public DocumentIntelligentAgentController(
        IDocumentIntelligentAgentClient client,
        ITenantProvider tenantProvider)
    {
        _client = client;
        _tenantProvider = tenantProvider;
    }

    /// <summary>
    /// Classify a document into a tenant repository via agents <c>/chat</c>.
    /// JSON body: sessionId, ocrText and/or filepath, optional pageno/model.
    /// Tenant comes from the access token / <c>X-Tenant-Id</c> — do not send tenantId.
    /// </summary>
    [HttpPost]
    [Consumes("application/json")]
    [Produces("application/json")]
    public async Task<IActionResult> ClassifyJson(
        [FromBody] DocumentIntelligentAgentJsonRequest? body,
        CancellationToken cancellationToken = default)
    {
        body ??= new DocumentIntelligentAgentJsonRequest();
        var request = new DocumentIntelligentAgentRequest
        {
            SessionId = CleanOptional(body.SessionId ?? body.Session_Id) ?? $"doc-intel-{Guid.NewGuid():N}",
            TenantId = RequireTenantId(),
            OcrText = CleanOptional(body.OcrText ?? body.Ocr_Text),
            FilePath = CleanOptional(body.FilePath ?? body.Filepath),
            PageNo = CleanOptional(body.PageNo ?? body.Pageno),
            Model = CleanOptional(body.Model),
            IncludeRepositoryCatalog = body.IncludeRepositoryCatalog ?? true
        };

        return await ClassifyCoreAsync(request, cancellationToken);
    }

    /// <summary>
    /// Multipart classify: form fields <c>session_id</c>, <c>pageno</c>,
    /// optional <c>ocr_text</c>/<c>filepath</c>/<c>model</c>, and <c>file</c>.
    /// Tenant comes from the access token / <c>X-Tenant-Id</c> — do not send tenant_id.
    /// </summary>
    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [Produces("application/json")]
    [RequestSizeLimit(104_857_600)]
    public async Task<IActionResult> ClassifyUpload(
        IFormFile? file,
        [FromForm] string? session_id,
        [FromForm] string? ocr_text,
        [FromForm] string? filepath,
        [FromForm] string? pageno,
        [FromForm] string? model,
        [FromForm] bool? include_repository_catalog,
        CancellationToken cancellationToken = default)
    {
        byte[]? bytes = null;
        if (file is { Length: > 0 })
        {
            await using var ms = new MemoryStream();
            await file.CopyToAsync(ms, cancellationToken);
            bytes = ms.ToArray();
        }

        // Swagger "Try it out" fills optional strings with the literal "string" — ignore those.
        var cleanedOcr = CleanOptional(ocr_text);
        var cleanedPath = CleanOptional(filepath);
        var hasFile = bytes is { Length: > 0 };

        // When a real file is uploaded, do not also forward placeholder ocr/filepath to agents.
        var request = new DocumentIntelligentAgentRequest
        {
            SessionId = CleanOptional(session_id) ?? $"doc-intel-{Guid.NewGuid():N}",
            TenantId = RequireTenantId(),
            OcrText = hasFile ? null : cleanedOcr,
            FilePath = hasFile ? null : cleanedPath,
            PageNo = CleanOptional(pageno) ?? (hasFile ? "1" : null),
            Model = CleanOptional(model),
            FileBytes = bytes,
            FileName = file?.FileName,
            FileContentType = file?.ContentType,
            IncludeRepositoryCatalog = include_repository_catalog ?? true
        };

        return await ClassifyCoreAsync(request, cancellationToken);
    }

    private static string? CleanOptional(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;
        if (string.Equals(trimmed, "string", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase))
            return null;
        return trimmed;
    }

    private Guid RequireTenantId() =>
        _tenantProvider.GetTenantId()
        ?? throw new InvalidOperationException("Tenant context is required (access token / X-Tenant-Id).");

    private async Task<IActionResult> ClassifyCoreAsync(
        DocumentIntelligentAgentRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _client.ClassifyAsync(request, cancellationToken);
            return ToUnwrappedActionResult(result, DocumentIntelligentAgentClient.ResultPropertyName);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout, new
            {
                error = "Agents /chat timed out waiting for document_intelligent. Check Agents:ChatUrl and nginx proxy timeouts.",
                detail = ex.Message
            });
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                error = "Could not reach Agents:ChatUrl for document_intelligent.",
                detail = ex.Message
            });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }

    private static IActionResult ToUnwrappedActionResult(
        DocumentIntelligentAgentProxyResult result,
        string propertyName)
    {
        if (string.IsNullOrWhiteSpace(result.Body))
            return new StatusCodeResult(result.StatusCode);

        if (result.StatusCode is >= 200 and < 300
            && TryExtractNamedResult(result.Body, propertyName, out var extracted))
        {
            return new ContentResult
            {
                StatusCode = result.StatusCode,
                Content = extracted,
                ContentType = "application/json"
            };
        }

        return new ContentResult
        {
            StatusCode = result.StatusCode,
            Content = result.Body,
            ContentType = IsLikelyJson(result.Body)
                ? "application/json"
                : (result.ContentType ?? "text/plain")
        };
    }

    private static bool TryExtractNamedResult(string body, string propertyName, out string extracted)
    {
        extracted = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            if (!TryGetPropertyIgnoreCase(doc.RootElement, propertyName, out var resultEl)
                || resultEl.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                return false;

            extracted = resultEl.GetRawText();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value))
            return true;

        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool IsLikelyJson(string body)
    {
        var trimmed = body.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }
}

/// <summary>JSON body for Document Intelligent Agent (camelCase or snake_case). Tenant is not accepted from the client.</summary>
public sealed class DocumentIntelligentAgentJsonRequest
{
    public string? SessionId { get; set; }
    public string? Session_Id { get; set; }
    public string? OcrText { get; set; }
    public string? Ocr_Text { get; set; }
    public string? FilePath { get; set; }
    public string? Filepath { get; set; }
    public string? PageNo { get; set; }
    public string? Pageno { get; set; }
    public string? Model { get; set; }
    public bool? IncludeRepositoryCatalog { get; set; }
}
