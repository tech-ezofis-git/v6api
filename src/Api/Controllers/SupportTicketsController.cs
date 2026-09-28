using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SaaSApp.Api.Services;
using SaaSApp.Api.Services.Jira;
using SaaSApp.MultiTenancy;
using SaaSApp.Security;

namespace SaaSApp.Api.Controllers;

/// <summary>Creates a support ticket in Jira and stores the submission in the tenant DB.</summary>
[ApiController]
[Route("api/support-tickets")]
[Authorize(Policy = AuthorizationPolicies.TenantUser)]
public sealed class SupportTicketsController : ControllerBase
{
    private readonly ITenantProvider _tenantProvider;
    private readonly JiraIssueClient _jiraClient;
    private readonly SupportTicketStore _store;
    private readonly SupportTicketEmailService _emailService;
    private readonly JiraOptions _jiraOptions;

    public SupportTicketsController(
        ITenantProvider tenantProvider,
        JiraIssueClient jiraClient,
        SupportTicketStore store,
        SupportTicketEmailService emailService,
        IOptions<JiraOptions> jiraOptions)
    {
        _tenantProvider = tenantProvider;
        _jiraClient = jiraClient;
        _store = store;
        _emailService = emailService;
        _jiraOptions = jiraOptions.Value;
    }

    /// <summary>Submit a support ticket. Creates a Jira issue when Jira:Enabled is true, then persists the request.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(CreateSupportTicketResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(
        [FromBody] CreateSupportTicketRequest? request,
        CancellationToken cancellationToken)
    {
        request ??= new CreateSupportTicketRequest();

        var tenantId = _tenantProvider.GetTenantId();
        if (tenantId == null)
            return BadRequest(new { error = "Tenant not resolved. Send X-Tenant-Id header or ensure JWT includes tid." });

        var callerEmail = GetCurrentUserEmail();
        var userId = GetCurrentUserId();
        var id = Guid.NewGuid();

        JiraCreateIssueResult jiraResult;
        if (_jiraOptions.Enabled)
        {
            jiraResult = await _jiraClient.CreateIssueAsync(
                new JiraCreateIssueRequest
                {
                    SupportCategory = request.SupportCategory,
                    Priority = request.Priorty,
                    PreferredContact = request.PreferredContact,
                    PhoneNO = request.PhoneNO,
                    RequestDescription = request.RequestDescription,
                    IsEmailSend = request.IsEmailSend,
                    CallerEmail = callerEmail
                },
                cancellationToken);
        }
        else
        {
            jiraResult = new JiraCreateIssueResult
            {
                Success = false,
                RawResponse = "Jira:Enabled is false; skipped remote create."
            };
        }

        var createdAtUtc = DateTime.UtcNow;

        await _store.InsertAsync(
            new SupportTicketInsertRequest
            {
                Id = id,
                TenantId = tenantId.Value,
                UserId = userId,
                CallerEmail = callerEmail,
                SupportCategory = request.SupportCategory,
                Priorty = request.Priorty,
                PreferredContact = request.PreferredContact,
                PhoneNO = request.PhoneNO,
                RequestDescription = request.RequestDescription,
                IsEmailSend = request.IsEmailSend,
                JiraIssueId = jiraResult.IssueId,
                JiraIssueKey = jiraResult.IssueKey,
                JiraIssueUrl = jiraResult.IssueUrl,
                JiraRawResponse = jiraResult.RawResponse,
                JiraSuccess = jiraResult.Success,
                CreatedAtUtc = createdAtUtc
            },
            cancellationToken);

        if (jiraResult.Success)
        {
            await _emailService.SendSuccessNotificationsAsync(
                new SupportTicketEmailContext
                {
                    TicketId = id,
                    TenantId = tenantId.Value,
                    CallerEmail = callerEmail,
                    CallerName = GetCurrentUserName(),
                    SupportCategory = request.SupportCategory,
                    Priorty = request.Priorty,
                    PreferredContact = request.PreferredContact,
                    PhoneNO = request.PhoneNO,
                    RequestDescription = request.RequestDescription,
                    JiraIssueKey = jiraResult.IssueKey,
                    JiraIssueUrl = jiraResult.IssueUrl,
                    CreatedAtUtc = createdAtUtc
                },
                cancellationToken);
        }

        return StatusCode(
            StatusCodes.Status201Created,
            new CreateSupportTicketResponse(id, jiraResult.IssueKey, jiraResult.IssueUrl, jiraResult.Success));
    }

    private string? GetCurrentUserName()
    {
        var user = HttpContext.User;
        var name = user.FindFirst("name")?.Value
            ?? user.FindFirst(ClaimTypes.Name)?.Value
            ?? user.FindFirst("displayName")?.Value;
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    private string? GetCurrentUserEmail()
    {
        var user = HttpContext.User;
        var email = user.FindFirst("email")?.Value
            ?? user.FindFirst("preferred_username")?.Value
            ?? user.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress")?.Value;
        return email?.Trim();
    }

    private Guid? GetCurrentUserId()
    {
        var user = HttpContext.User;
        var sub = user.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? user.FindFirstValue("sub")
            ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("userId");
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}

/// <summary>Support ticket form payload. Field names match the client (including Priorty spelling).</summary>
public sealed class CreateSupportTicketRequest
{
    /// <summary>Ticket heading/category, e.g. Account configuration, Accounts payable setup.</summary>
    public string? SupportCategory { get; set; }
    public string? Priorty { get; set; }
    public string? PreferredContact { get; set; }
    public string? PhoneNO { get; set; }
    public string? RequestDescription { get; set; }
    public bool IsEmailSend { get; set; }
}

public sealed record CreateSupportTicketResponse(
    Guid Id,
    string? JiraIssueKey,
    string? JiraIssueUrl,
    bool JiraSuccess);
