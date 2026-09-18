using System.Net;
using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using SaaSApp.Catalog.Entities;
using SaaSApp.Catalog.Persistence;

namespace SaaSApp.Api.Services;

/// <summary>Sends login 2FA email OTPs using catalog mail settings (same path as signup OTP).</summary>
public interface ILoginEmailOtpService
{
    /// <summary>Generate a 6-digit OTP, email it, and persist for audit. Returns the OTP for cache verification.</summary>
    Task<string> SendLoginOtpAsync(string email, string? displayName, CancellationToken cancellationToken = default);
}

public sealed class LoginEmailOtpService : ILoginEmailOtpService
{
    private readonly IDbContextFactory<CatalogDbContext> _catalogFactory;
    private readonly ILogger<LoginEmailOtpService> _logger;

    public LoginEmailOtpService(
        IDbContextFactory<CatalogDbContext> catalogFactory,
        ILogger<LoginEmailOtpService> logger)
    {
        _catalogFactory = catalogFactory;
        _logger = logger;
    }

    public async Task<string> SendLoginOtpAsync(string email, string? displayName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email is required.");

        var normalizedEmail = email.Trim().ToLowerInvariant();
        MailSetting settings;
        await using (var catalog = await _catalogFactory.CreateDbContextAsync(cancellationToken))
        {
            settings = await catalog.MailSettings
                .AsNoTracking()
                .Where(x => x.Preference == 1 && !x.Isdeleted)
                .OrderByDescending(x => x.SettingId)
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException("Mail settings are not configured for OTP email.");
        }

        if (string.IsNullOrWhiteSpace(settings.EmailId)
            || string.IsNullOrWhiteSpace(settings.Password)
            || string.IsNullOrWhiteSpace(settings.OutgoingServer)
            || settings.OutgoingPort <= 0)
        {
            throw new InvalidOperationException("Mail settings have invalid SMTP configuration.");
        }

        var otp = Random.Shared.Next(100000, 999999).ToString();
        var firstName = ResolveFirstName(displayName, normalizedEmail);
        var body = $"""
            <p>Hi {WebUtility.HtmlEncode(firstName)},</p>
            <p>Your login verification code is: <b>{WebUtility.HtmlEncode(otp)}</b></p>
            <p>This code is valid for 5 minutes. If you did not try to sign in, you can ignore this email.</p>
            """;

        using var mail = new MailMessage
        {
            From = new MailAddress(settings.EmailId),
            Subject = "Your Ezofis login verification code",
            IsBodyHtml = true,
            Body = body
        };
        mail.To.Add(normalizedEmail);

        using var smtp = new SmtpClient(settings.OutgoingServer, settings.OutgoingPort)
        {
            Credentials = new NetworkCredential(settings.EmailId, settings.Password),
            EnableSsl = true
        };

        try
        {
            await smtp.SendMailAsync(mail, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send login OTP email to {Email}", normalizedEmail);
            throw new InvalidOperationException("Failed to send OTP email. Please try again later.");
        }

        await using (var catalog = await _catalogFactory.CreateDbContextAsync(cancellationToken))
        {
            var nowUtc = DateTime.UtcNow;
            catalog.OtpVerifications.Add(new OtpVerification
            {
                Email = normalizedEmail,
                OTP = otp,
                Status = "login_shared",
                ValidateAt = nowUtc.AddMinutes(5),
                CreatedAt = nowUtc,
                CreatedBy = 1,
                IsDeleted = false
            });
            await catalog.SaveChangesAsync(cancellationToken);
        }

        return otp;
    }

    private static string ResolveFirstName(string? displayName, string email)
    {
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            var first = displayName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            if (!string.IsNullOrWhiteSpace(first))
                return first;
        }

        var local = email.Split('@')[0];
        var part = local.Split('.', StringSplitOptions.RemoveEmptyEntries)[0];
        if (string.IsNullOrWhiteSpace(part))
            return "User";
        return char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant();
    }
}
