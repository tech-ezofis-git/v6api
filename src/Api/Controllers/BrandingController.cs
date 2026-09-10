using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaaSApp.Api.Services;

namespace SaaSApp.Api.Controllers;

/// <summary>
/// Public branding APIs (no JWT). Encrypt branding name; decrypt + load branding from catalog DB.
/// </summary>
[ApiController]
[Route("api/branding")]
[AllowAnonymous]
public sealed class BrandingController : ControllerBase
{
    private readonly IBrandingCryptoService _crypto;
    private readonly IBrandingService _branding;

    public BrandingController(IBrandingCryptoService crypto, IBrandingService branding)
    {
        _crypto = crypto;
        _branding = branding;
    }

    /// <summary>Encrypt a branding name. No token required.</summary>
    [HttpPost("encrypt")]
    [ProducesResponseType(typeof(EncryptBrandingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult Encrypt([FromBody] EncryptBrandingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.BrandingName))
            return BadRequest(new { error = "brandingName is required." });

        try
        {
            var encrypted = _crypto.Encrypt(request.BrandingName);
            return Ok(new EncryptBrandingResponse(request.BrandingName.Trim(), encrypted));
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Decrypt encrypted branding name and return branding details from catalog.
    /// No token required. Use Base64Url value from encrypt (safe in path).
    /// </summary>
    [HttpGet("{encryptedName}")]
    [ProducesResponseType(typeof(BrandingGetResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByEncryptedName(string encryptedName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(encryptedName))
            return BadRequest(new { error = "encryptedName is required." });

        string brandingName;
        try
        {
            brandingName = _crypto.Decrypt(Uri.UnescapeDataString(encryptedName));
        }
        catch (Exception)
        {
            return BadRequest(new { error = "Invalid encrypted branding name." });
        }

        var row = await _branding.GetByBrandingNameAsync(brandingName, cancellationToken);
        if (row == null)
            return NotFound(new { error = "Branding not found.", brandingName });

        return Ok(new BrandingGetResponse(
            brandingName,
            encryptedName,
            row.TenantId,
            row));
    }

    /// <summary>Save / upsert branding details in catalog.Branding. No token required.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(BrandingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Save([FromBody] SaveBrandingApiRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var saved = await _branding.SaveAsync(
                new SaveBrandingRequest(
                    request.TenantId,
                    request.UserId,
                    request.UserEmail ?? string.Empty,
                    request.BrandingName ?? string.Empty,
                    request.BrandingJson ?? "{}"),
                cancellationToken);
            return Ok(saved);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

public sealed record EncryptBrandingRequest(string BrandingName);

public sealed record EncryptBrandingResponse(string BrandingName, string EncryptedBrandingName);

public sealed record BrandingGetResponse(
    string BrandingName,
    string EncryptedBrandingName,
    Guid TenantId,
    BrandingDto Branding);

public sealed record SaveBrandingApiRequest(
    Guid TenantId,
    Guid UserId,
    string? UserEmail,
    string? BrandingName,
    string? BrandingJson);
