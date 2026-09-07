using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaaSApp.MultiTenancy;
using SaaSApp.Repository.Application.Contracts;
using SaaSApp.Security;

namespace SaaSApp.Api.Controllers;

/// <summary>
/// Proxies repository assistant calls to the local Python Azure Functions host (search / chatbot).
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
    /// POST body → <c>http://localhost:7071/api/search</c>.
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
            return ToActionResult(result);
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }

    /// <summary>
    /// POST body → <c>http://localhost:7071/api/chatbot</c>.
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

    private static bool IsLikelyJson(string body)
    {
        var trimmed = body.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }
}
