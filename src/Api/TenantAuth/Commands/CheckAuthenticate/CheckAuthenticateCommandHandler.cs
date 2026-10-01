using System.Net;
using System.Net.Mail;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using SaaSApp.Catalog;
using SaaSApp.Catalog.Entities;
using SaaSApp.Catalog.Persistence;

namespace SaaSApp.Api.TenantAuth.Commands.CheckAuthenticate;

public sealed class CheckAuthenticateCommandHandler : IRequestHandler<CheckAuthenticateCommand, CheckAuthenticateResult>
{
    private readonly IDbContextFactory<CatalogDbContext> _catalogFactory;
    private readonly IDistributedCache _cache;

    public CheckAuthenticateCommandHandler(
        IDbContextFactory<CatalogDbContext> catalogFactory,
        IDistributedCache cache)
    {
        _catalogFactory = catalogFactory;
        _cache = cache;
    }

    public async Task<CheckAuthenticateResult> Handle(CheckAuthenticateCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
            throw new ArgumentException("Email should not empty");

        var email = request.Email.Trim().ToLowerInvariant();

        await using (var catalog = await _catalogFactory.CreateDbContextAsync(cancellationToken))
        {
            // Unique org email lives on Tenants; UserTenants can have many users per tenant.
            var exists = await catalog.Tenants
                .AsNoTracking()
                .AnyAsync(x => x.Email != null && x.Email.ToLower() == email, cancellationToken);

            if (exists)
                return new CheckAuthenticateResult(409, "Tenant is already exists, Please change the Email for signup");
        }

        if (!request.RequiredOTP)
            return new CheckAuthenticateResult(200, "success");

        MailSetting? settings;
        await using (var catalog = await _catalogFactory.CreateDbContextAsync(cancellationToken))
        {
            settings = await catalog.MailSettings
                .AsNoTracking()
                .Where(x => x.Preference == 1 && !x.Isdeleted)
                .OrderByDescending(x => x.SettingId)
                .FirstOrDefaultAsync(cancellationToken);

            if (settings == null)
                return new CheckAuthenticateResult(400, $"check the prefernce in mailsettings Tenant {email}");
        }

        if (string.IsNullOrWhiteSpace(settings.EmailId) ||
            string.IsNullOrWhiteSpace(settings.Password) ||
            string.IsNullOrWhiteSpace(settings.OutgoingServer) ||
            settings.OutgoingPort <= 0)
        {
            return new CheckAuthenticateResult(400, "mailsettings has invalid SMTP configuration.");
        }

        var otp = Random.Shared.Next(100000, 1000000).ToString();
        var firstName = GetFirstNameFromEmail(email);

        using var mail = new MailMessage
        {
            From = EzofisMailAddress.System(settings.EmailId),
            Subject = EzofisOtpMail.Subject
        };
        mail.To.Add(email);
        EzofisOtpMail.Apply(
            mail,
            firstName,
            otp,
            "Here is your Ezofis sudo authentication code:",
            "5 minutes");

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
            return new CheckAuthenticateResult(400, "ERROR on mail send " + ex.Message);
        }

        try
        {
            await _cache.SetStringAsync(
                $"signup:otp:{email}",
                otp,
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
                },
                cancellationToken);
        }
        catch
        {
            // Redis can be unavailable in some environments; OTP verification still works via catalog.OtpVerifications.
        }

        await using (var catalog = await _catalogFactory.CreateDbContextAsync(cancellationToken))
        {
            var nowUtc = DateTime.UtcNow;
            catalog.OtpVerifications.Add(new OtpVerification
            {
                Email = email,
                OTP = otp,
                Status = "shared",
                ValidateAt = nowUtc.AddMinutes(5),
                CreatedAt = nowUtc,
                CreatedBy = 1,
                IsDeleted = false
            });
            await catalog.SaveChangesAsync(cancellationToken);
        }

        return new CheckAuthenticateResult(200, "OTP sent succeeded");
    }

    private static string GetFirstNameFromEmail(string email)
    {
        var localPart = email.Split('@')[0];
        if (string.IsNullOrWhiteSpace(localPart))
            return "User";

        var first = localPart.Split('.')[0];
        if (string.IsNullOrWhiteSpace(first))
            return "User";

        return char.ToUpperInvariant(first[0]) + first[1..].ToLowerInvariant();
    }

}
