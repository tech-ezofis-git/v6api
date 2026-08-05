using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaaSApp.Api.Services;
using SaaSApp.MultiTenancy;
using Microsoft.Extensions.Hosting;

namespace SaaSApp.Api.Controllers.Auth;

/// <summary>Ezofis email/password login. Send X-Tenant-Id header to select organization.</summary>
[ApiController]
[Route("api/auth")]
[AllowAnonymous]
public sealed class LoginController : ControllerBase
{
    private readonly IEzofisAuthService _authService;
    private readonly ITenantProvider _tenantProvider;
    private readonly ILogger<LoginController> _logger;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;

    public LoginController(
        IEzofisAuthService authService,
        ITenantProvider tenantProvider,
        ILogger<LoginController> logger,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        _authService = authService;
        _tenantProvider = tenantProvider;
        _logger = logger;
        _configuration = configuration;
        _environment = environment;
    }

    /// <summary>Login with email and password. If 2FA enabled, returns tempToken; call POST /2fa/complete with code.</summary>
    [HttpPost("ezofis/login")]
    [ProducesResponseType(typeof(LoginSuccess), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(LoginRequiresTwoFactor), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(LoginRequiresPasswordSetup), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        var tenantId = _tenantProvider.GetTenantId();
        if (tenantId == null)
            return BadRequest(new { error = "X-Tenant-Id header is required to select organization." });

        try
        {
            var result = await _authService.LoginAsync(request.Email, request.Password, tenantId.Value, cancellationToken);
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
            _logger.LogError(ex, "Login failed due to configuration or user profile data.");
            return StatusCode(StatusCodes.Status500InternalServerError, BuildLoginConfigError(ex));
        }
    }

    /// <summary>Complete login after 2FA. Use tempToken from login response and TOTP code from authenticator app.</summary>
    [HttpPost("2fa/complete")]
    [ProducesResponseType(typeof(LoginSuccess), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CompleteTwoFactor([FromBody] CompleteTwoFactorRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _authService.CompleteTwoFactorAsync(request.TempToken, request.Code, cancellationToken);
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
            _logger.LogError(ex, "2FA completion failed due to configuration or user profile data.");
            return StatusCode(StatusCodes.Status500InternalServerError, BuildLoginConfigError(ex));
        }
    }

    /// <summary>
    /// Social login (Google / Microsoft). Email + provider only — no password.
    /// User must exist with matching loginType/authStrategy (GOOGLE or MICROSOFT).
    /// </summary>
    [HttpPost("social/login")]
    [ProducesResponseType(typeof(LoginSuccess), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SocialLogin([FromBody] SocialLoginRequest request, CancellationToken cancellationToken)
    {
        var tenantId = _tenantProvider.GetTenantId();
        if (tenantId == null)
            return BadRequest(new { error = "X-Tenant-Id header is required to select organization." });

        try
        {
            var result = await _authService.SocialLoginAsync(request.Email, request.Provider, tenantId.Value, cancellationToken);
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
            _logger.LogError(ex, "Social login failed due to configuration or user profile data.");
            return StatusCode(StatusCodes.Status500InternalServerError, BuildLoginConfigError(ex));
        }
    }

    /// <summary>
    /// Set first-time password for a workflow inbox / guest file share invite (EZOFIS users only).
    /// No X-Tenant-Id required — tenant is resolved from the share token.
    /// Returns JWT on success; use shareToken on repository file APIs after login.
    /// </summary>
    [HttpPost("share/set-password")]
    [ProducesResponseType(typeof(LoginSuccess), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SetShareInvitePassword(
        [FromBody] SetShareInvitePasswordRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _authService.SetShareInvitePasswordAsync(
                request.ShareToken,
                request.Email,
                request.Password,
                cancellationToken);
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

    /// <summary>
    /// Social login (Google / Microsoft) for a workflow inbox / guest file share invite.
    /// Call after client-side Google/Microsoft OAuth. No X-Tenant-Id — tenant from share token.
    /// </summary>
    [HttpPost("share/social-login")]
    [ProducesResponseType(typeof(LoginSuccess), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SetShareInviteSocialLogin(
        [FromBody] SetShareInviteSocialLoginRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _authService.SetShareInviteSocialLoginAsync(
                request.ShareToken,
                request.Email,
                request.Provider,
                cancellationToken);
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

    /// <summary>Set first-time password for a repository sign-request invite. No X-Tenant-Id — tenant from invite token.</summary>
    [HttpPost("sign-request/set-password")]
    [ProducesResponseType(typeof(LoginSuccess), StatusCodes.Status200OK)]
    public async Task<IActionResult> SetSignInvitePassword(
        [FromBody] SetSignInvitePasswordRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _authService.SetSignInvitePasswordAsync(
                request.InviteToken, request.Email, request.Password, cancellationToken);
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

    /// <summary>Social login for a repository sign-request invite. No X-Tenant-Id — tenant from invite token.</summary>
    [HttpPost("sign-request/social-login")]
    [ProducesResponseType(typeof(LoginSuccess), StatusCodes.Status200OK)]
    public async Task<IActionResult> SetSignInviteSocialLogin(
        [FromBody] SetSignInviteSocialLoginRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _authService.SetSignInviteSocialLoginAsync(
                request.InviteToken, request.Email, request.Provider, cancellationToken);
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

    private object BuildLoginConfigError(InvalidOperationException ex)
    {
        var showDetails = _environment.IsDevelopment()
            || _configuration.GetValue<bool>("Diagnostics:ShowDetailedErrors");
        if (showDetails)
            return new { error = ex.Message };
        return new { error = "Login configuration is invalid. Please contact support." };
    }
}

/// <summary>Email and password for Ezofis login. Requires X-Tenant-Id header.</summary>
public record LoginRequest(string Email, string Password);

/// <summary>TempToken from login response when 2FA required; Code from authenticator app. Requires X-Tenant-Id header.</summary>
public record CompleteTwoFactorRequest(string TempToken, string Code);

/// <summary>Social login: email + provider (google or microsoft). Requires X-Tenant-Id header. No password.</summary>
public record SocialLoginRequest(string Email, string Provider);

/// <summary>First-time password for guest share invite from workflow inbox (EZOFIS only).</summary>
public record SetShareInvitePasswordRequest(string ShareToken, string Email, string Password);

/// <summary>Social login for guest share invite after Google/Microsoft OAuth on the client.</summary>
public record SetShareInviteSocialLoginRequest(string ShareToken, string Email, string Provider);

/// <summary>First-time password for repository sign-request invite.</summary>
public record SetSignInvitePasswordRequest(string InviteToken, string Email, string Password);

/// <summary>Social login for repository sign-request invite.</summary>
public record SetSignInviteSocialLoginRequest(string InviteToken, string Email, string Provider);
