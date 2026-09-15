using Azure.Storage.Blobs;
using System.Net;
using System.Net.Mail;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using SaaSApp.Catalog.Entities;
using SaaSApp.Catalog.Persistence;

namespace SaaSApp.Api.TenantAuth.Commands.CheckAuthenticate;

public sealed class CheckAuthenticateCommandHandler : IRequestHandler<CheckAuthenticateCommand, CheckAuthenticateResult>
{
    private readonly IDbContextFactory<CatalogDbContext> _catalogFactory;
    private readonly IConfiguration _configuration;
    private readonly IDistributedCache _cache;
    private readonly ILogger<CheckAuthenticateCommandHandler> _logger;

    public CheckAuthenticateCommandHandler(
        IDbContextFactory<CatalogDbContext> catalogFactory,
        IConfiguration configuration,
        IDistributedCache cache,
        ILogger<CheckAuthenticateCommandHandler> logger)
    {
        _catalogFactory = catalogFactory;
        _configuration = configuration;
        _cache = cache;
        _logger = logger;
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

        var otp = Random.Shared.Next(100000, 999999).ToString();
        var firstName = GetFirstNameFromEmail(email);

        var htmlBody = await BuildOtpHtmlBodyAsync(email, firstName, otp, cancellationToken);

        using var mail = new MailMessage
        {
            From = new MailAddress(settings.EmailId),
            Subject = "Your One-Time Password (OTP) Code",
            IsBodyHtml = true,
            Body = htmlBody
        };
        mail.To.Add(email);

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

    private async Task<string> BuildOtpHtmlBodyAsync(string email, string firstName, string otp, CancellationToken cancellationToken)
    {
        var htmlFile = @"HTMLFiles\OTP Alert.html";
        var container = _configuration["OtpTemplate:Container"] ?? "ezofis";
        var commonPath = _configuration["CommonPath"] ?? string.Empty;
        var htmlBody = string.Empty;

        if (!string.IsNullOrWhiteSpace(commonPath))
        {
            var mapPath = Path.Combine(commonPath, htmlFile);
            if (File.Exists(mapPath))
                htmlBody = await File.ReadAllTextAsync(mapPath, cancellationToken);
        }
        else
        {
            var assetStorageConnection = ResolveOtpAssetStorageConnection();
            if (!string.IsNullOrWhiteSpace(assetStorageConnection))
            {
                try
                {
                    var blobServiceClient = new BlobServiceClient(assetStorageConnection);
                    var containerClient = blobServiceClient.GetBlobContainerClient(container);
                    var blobClient = containerClient.GetBlobClient(htmlFile.Replace('\\', '/'));
                    var blobDownloadInfo = await blobClient.DownloadAsync(cancellationToken);
                    await using var content = blobDownloadInfo.Value.Content;
                    using var memoryStream = new MemoryStream();
                    await content.CopyToAsync(memoryStream, cancellationToken);
                    var buffer = memoryStream.ToArray();
                    if (buffer.Length > 0)
                        htmlBody = System.Text.Encoding.UTF8.GetString(buffer);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Could not load OTP HTML template from blob container {Container}; using built-in template",
                        container);
                }
            }
            else
            {
                _logger.LogInformation(
                    "OTP blob template not configured (OtpTemplate:DefaultAssetStorageConnection); using built-in template");
            }
        }

        if (string.IsNullOrWhiteSpace(htmlBody))
        {
            htmlBody = """
                       <p>Hi #Parameter1#,</p>
                       <p>Your OTP Code is: <b>#Parameter2#</b></p>
                       <p>Please enter this code to complete verification. This code is valid for 5 minutes.</p>
                       <p>Date: #Date#</p>
                       """;
        }

        htmlBody = htmlBody
            .Replace("#Parameter1#", WebUtility.HtmlEncode(firstName), StringComparison.Ordinal)
            .Replace("#Parameter2#", WebUtility.HtmlEncode(otp), StringComparison.Ordinal)
            .Replace("#Date#", DateTime.Now.ToString("dd MMM yyyy"), StringComparison.Ordinal)
            .Replace("#TENANTLOGO#", _configuration["OtpTemplate:TenantLogoUrl"] ?? string.Empty, StringComparison.Ordinal);

        return htmlBody;
    }

    private string? ResolveOtpAssetStorageConnection()
    {
        var serverForOcr = (_configuration["ServerForOcr"] ?? string.Empty).ToLowerInvariant();
        if (serverForOcr == "trial")
        {
            return _configuration["OtpTemplate:TrialAssetStorageConnection"]
                ?? _configuration["OtpTemplate:DefaultAssetStorageConnection"]
                ?? _configuration["EzofisBlobStorage:ConnectionString"]
                ?? _configuration["WorkflowJsonStorage:Blob:ConnectionString"];
        }

        return _configuration["OtpTemplate:DefaultAssetStorageConnection"]
            ?? _configuration["EzofisBlobStorage:ConnectionString"]
            ?? _configuration["WorkflowJsonStorage:Blob:ConnectionString"];
    }
}
