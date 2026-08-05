using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaaSApp.MultiTenancy;
using SaaSApp.Repository.Application.Contracts;

namespace SaaSApp.Api.Controllers;

[ApiController]
[Authorize]
public sealed class SignRequestsController : ControllerBase
{
    private readonly IRepositorySignRequestService _signRequests;
    private readonly ITenantProvider _tenantProvider;

    public SignRequestsController(
        IRepositorySignRequestService signRequests,
        ITenantProvider tenantProvider)
    {
        _signRequests = signRequests;
        _tenantProvider = tenantProvider;
    }

    /// <summary>Create a parallel or sequential sign request for a repository file.</summary>
    [HttpPost("/api/repositories/{repositoryId:guid}/items/{itemId:guid}/sign-requests")]
    [ProducesResponseType(typeof(SignRequestDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(
        Guid repositoryId,
        Guid itemId,
        [FromBody] CreateSignRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        var userId = GetUserId();
        if (userId is null)
            return Unauthorized(new { error = "User id is required." });

        try
        {
            var result = await _signRequests.CreateAsync(
                tenantId,
                repositoryId,
                itemId,
                userId.Value,
                GetUserEmail(),
                GetUserName(),
                request,
                cancellationToken);
            return Created($"/api/sign-requests/{result.SignRequestId}", result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("/api/repositories/{repositoryId:guid}/items/{itemId:guid}/sign-requests")]
    [ProducesResponseType(typeof(IReadOnlyList<SignRequestDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListForItem(
        Guid repositoryId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        var list = await _signRequests.ListForItemAsync(tenantId, repositoryId, itemId, cancellationToken);
        return Ok(list);
    }

    [HttpGet("/api/sign-requests/{signRequestId:guid}")]
    [ProducesResponseType(typeof(SignRequestDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid signRequestId, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        var result = await _signRequests.GetAsync(tenantId, signRequestId, cancellationToken);
        return result == null ? NotFound(new { error = "Sign request not found." }) : Ok(result);
    }

    [HttpPost("/api/sign-requests/{signRequestId:guid}/cancel")]
    [ProducesResponseType(typeof(SignRequestDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(Guid signRequestId, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        var userId = GetUserId();
        if (userId is null)
            return Unauthorized();

        try
        {
            var result = await _signRequests.CancelAsync(tenantId, signRequestId, userId.Value, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Logged-in signer inbox: pending sign tasks for this user's email (no invite token needed).
    /// Use when user opens app via normal login instead of email link.
    /// </summary>
    [HttpGet("/api/sign-requests/pending-for-me")]
    [ProducesResponseType(typeof(IReadOnlyList<MyPendingSignRequestDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> PendingForMe(CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        var email = GetUserEmail();
        if (string.IsNullOrWhiteSpace(email))
            return Unauthorized(new { error = "Logged-in email is required." });

        var list = await _signRequests.ListPendingForMeAsync(tenantId, email, cancellationToken);
        return Ok(list);
    }

    /// <summary>
    /// Open PDF for logged-in signer by signRequestId (no invite token).
    /// Same as repository file: <c>?disposition=inline</c> (view) or <c>?disposition=attachment</c> (download).
    /// </summary>
    [HttpGet("/api/sign-requests/{signRequestId:guid}/file")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> OpenFileByRequest(
        Guid signRequestId,
        [FromQuery] string disposition = "inline",
        CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenantId();
        var email = GetUserEmail();
        if (string.IsNullOrWhiteSpace(email))
            return Unauthorized(new { error = "Logged-in email is required." });

        try
        {
            var file = await _signRequests.OpenFileForSignerAsync(tenantId, signRequestId, email, cancellationToken);
            if (file == null)
                return NotFound(new { error = "File not found." });
            return ToFileResult(file, disposition);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Sign as logged-in user matched by email (no invite token).</summary>
    [HttpPost("/api/sign-requests/{signRequestId:guid}/sign")]
    [ProducesResponseType(typeof(SignRequestDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> SignByRequest(
        Guid signRequestId,
        [FromBody] SubmitSignRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        var email = GetUserEmail();
        if (string.IsNullOrWhiteSpace(email))
            return Unauthorized(new { error = "Logged-in email is required." });

        try
        {
            var result = await _signRequests.SubmitSignatureForSignerAsync(
                tenantId, signRequestId, email, GetUserId(), request, cancellationToken);
            return Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Decline as logged-in signer (no invite token).</summary>
    [HttpPost("/api/sign-requests/{signRequestId:guid}/decline")]
    [ProducesResponseType(typeof(SignRequestDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeclineByRequest(
        Guid signRequestId,
        [FromBody] DeclineSignRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireTenantId();
        var email = GetUserEmail();
        if (string.IsNullOrWhiteSpace(email))
            return Unauthorized(new { error = "Logged-in email is required." });

        try
        {
            var result = await _signRequests.DeclineForSignerAsync(tenantId, signRequestId, email, request, cancellationToken);
            return Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Anonymous preview for signer invite (DocuSign-style details + auth methods).</summary>
    [HttpGet("/api/sign-requests/invite/{inviteToken}/preview")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(SignRequestInvitePreviewDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Preview(string inviteToken, CancellationToken cancellationToken)
    {
        var preview = await _signRequests.GetInvitePreviewAsync(inviteToken, cancellationToken);
        return preview == null
            ? NotFound(new { error = "Sign invite not found or expired." })
            : Ok(preview);
    }

    /// <summary>
    /// Open PDF for invite signer.
    /// Same as repository file: <c>?disposition=inline</c> (view) or <c>?disposition=attachment</c> (download).
    /// </summary>
    [HttpGet("/api/sign-requests/invite/{inviteToken}/file")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> OpenFile(
        string inviteToken,
        [FromQuery] string disposition = "inline",
        CancellationToken cancellationToken = default)
    {
        var email = GetUserEmail();
        if (string.IsNullOrWhiteSpace(email))
            return Unauthorized(new { error = "Logged-in email is required." });

        try
        {
            var file = await _signRequests.OpenInviteFileAsync(inviteToken, email, cancellationToken);
            if (file == null)
                return NotFound(new { error = "File not found." });

            return ToFileResult(file, disposition);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("/api/sign-requests/invite/{inviteToken}/sign")]
    [ProducesResponseType(typeof(SignRequestDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Sign(
        string inviteToken,
        [FromBody] SubmitSignRequestDto request,
        CancellationToken cancellationToken)
    {
        var email = GetUserEmail();
        if (string.IsNullOrWhiteSpace(email))
            return Unauthorized(new { error = "Logged-in email is required." });

        try
        {
            var result = await _signRequests.SubmitSignatureAsync(
                inviteToken, email, GetUserId(), request, cancellationToken);
            return Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("/api/sign-requests/invite/{inviteToken}/decline")]
    [ProducesResponseType(typeof(SignRequestDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Decline(
        string inviteToken,
        [FromBody] DeclineSignRequestDto request,
        CancellationToken cancellationToken)
    {
        var email = GetUserEmail();
        if (string.IsNullOrWhiteSpace(email))
            return Unauthorized(new { error = "Logged-in email is required." });

        try
        {
            var result = await _signRequests.DeclineAsync(inviteToken, email, request, cancellationToken);
            return Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private static FileStreamResult ToFileResult(RepositoryItemFileContent file, string? disposition)
    {
        var inline = string.Equals(disposition, "inline", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(disposition);
        return new FileStreamResult(file.Stream, file.ContentType ?? "application/pdf")
        {
            FileDownloadName = inline ? null : file.FileName,
            EnableRangeProcessing = true
        };
    }

    private Guid RequireTenantId() =>
        _tenantProvider.GetTenantId()
        ?? throw new InvalidOperationException("X-Tenant-Id header is required.");

    private Guid? GetUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub") ?? User.FindFirstValue("oid");
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    private string? GetUserEmail() =>
        User.FindFirstValue(ClaimTypes.Email)
        ?? User.FindFirstValue("email")
        ?? User.FindFirstValue("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress");

    private string? GetUserName() =>
        User.FindFirstValue(ClaimTypes.Name)
        ?? User.FindFirstValue("name")
        ?? GetUserEmail();
}
