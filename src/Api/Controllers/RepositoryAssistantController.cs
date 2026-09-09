using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaaSApp.MultiTenancy;
using SaaSApp.Repository.Application.Contracts;
using SaaSApp.Security;

namespace SaaSApp.Api.Controllers;

/// <summary>
/// Proxies repository assistant search/chatbot to agents <c>/chat</c> (<c>intent=global_search</c>).
/// </summary>
[ApiController]
[Route("api/repositories/assistant")]
[Authorize(Policy = AuthorizationPolicies.TenantUser)]
public sealed class RepositoryAssistantController : ControllerBase
{
    private readonly IRepositoryPythonAssistantClient _client;
    private readonly ITenantProvider _tenantProvider;

    public RepositoryAssistantController(
        IRepositoryPythonAssistantClient client,
        ITenantProvider tenantProvider)
    {
        _client = client;
        _tenantProvider = tenantProvider;
    }

    /// <summary>
    /// Proxies to <c>Agents:ChatUrl</c> with <c>intent=global_search</c>.
    /// Response body is only the agents <c>global_search_result</c> object (not the full chat envelope).
    /// Example: <c>{"actionFrom":"Repository","query":"po","specificId":"...","tenantId":"..."}</c>
    /// </summary>
    [HttpPost("search")]
    [Produces("application/json")]
    public async Task<IActionResult> Search(
        [FromBody] RepositoryAssistantSearchRequest? request,
        CancellationToken cancellationToken = default)
    {
        request ??= new RepositoryAssistantSearchRequest();
        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest(new { error = "query is required." });

        ApplyDefaults(request);
        try
        {
            var result = await _client.SearchAsync(request, cancellationToken);
            return ToSearchActionResult(result);
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Proxies to <c>Agents:ChatUrl</c> with <c>intent=global_search</c> (message mapped to query).
    /// Example: <c>{"actionFrom":"Repository","message":"hai","specificId":"...","tenantId":"...","token":"Bearer ..."}</c>
    /// </summary>
    [HttpPost("chatbot")]
    [Produces("application/json")]
    public async Task<IActionResult> Chatbot(
        [FromBody] RepositoryAssistantChatbotRequest? request,
        CancellationToken cancellationToken = default)
    {
        request ??= new RepositoryAssistantChatbotRequest();
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { error = "message is required." });

        ApplyDefaults(request);

        if (string.IsNullOrWhiteSpace(request.Token))
        {
            var auth = Request.Headers.Authorization.ToString();
            if (!string.IsNullOrWhiteSpace(auth))
                request.Token = auth;
        }

        try
        {
            var result = await _client.ChatbotAsync(request, cancellationToken);
            return ToActionResult(result);
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }

    private void ApplyDefaults(RepositoryAssistantSearchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ActionFrom))
            request.ActionFrom = "Repository";
        request.TenantId ??= _tenantProvider.GetTenantId();
    }

    private void ApplyDefaults(RepositoryAssistantChatbotRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ActionFrom))
            request.ActionFrom = "Repository";
        request.TenantId ??= _tenantProvider.GetTenantId();
    }

    private static IActionResult ToActionResult(RepositoryPythonProxyResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Body))
            return new StatusCodeResult(result.StatusCode);

        return new ContentResult
        {
            StatusCode = result.StatusCode,
            Content = result.Body,
            ContentType = IsLikelyJson(result.Body)
                ? "application/json"
                : (result.ContentType ?? "text/plain")
        };
    }

    /// <summary>
    /// For search only: unwrap <c>global_search_result</c> from the agents chat envelope.
    /// Falls back to the raw body when the property is missing or the payload is not JSON.
    /// </summary>
    private static IActionResult ToSearchActionResult(RepositoryPythonProxyResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Body))
            return new StatusCodeResult(result.StatusCode);

        if (result.StatusCode is >= 200 and < 300
            && TryExtractGlobalSearchResult(result.Body, out var extracted))
        {
            return new ContentResult
            {
                StatusCode = result.StatusCode,
                Content = extracted,
                ContentType = "application/json"
            };
        }

        return ToActionResult(result);
    }

    private static bool TryExtractGlobalSearchResult(string body, out string extracted)
    {
        extracted = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            if (!TryGetPropertyIgnoreCase(doc.RootElement, "global_search_result", out var resultEl)
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
