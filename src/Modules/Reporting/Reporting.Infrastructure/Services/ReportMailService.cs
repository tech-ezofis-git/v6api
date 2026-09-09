using System.Net;
using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SaaSApp.Catalog.Persistence;
using SaaSApp.Reporting.Application.Contracts;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Reporting.Infrastructure.Services;

public sealed class ReportMailService : IReportMailService
{
    private readonly IDbContextFactory<CatalogDbContext> _catalogFactory;
    private readonly IUserEmailLookup _userEmails;
    private readonly IReportExportService _export;
    private readonly ILogger<ReportMailService> _logger;

    public ReportMailService(
        IDbContextFactory<CatalogDbContext> catalogFactory,
        IUserEmailLookup userEmails,
        IReportExportService export,
        ILogger<ReportMailService> logger)
    {
        _catalogFactory = catalogFactory;
        _userEmails = userEmails;
        _export = export;
        _logger = logger;
    }

    public async Task SendAsync(
        Guid tenantId,
        ReportBuilderConfig config,
        ReportRunResult data,
        CancellationToken cancellationToken = default)
    {
        var schedule = config.Schedule ?? new ReportScheduleConfig();
        var file = _export.Export(data, schedule.Format);
        var (to, cc) = await ResolveRecipientsAsync(config, cancellationToken);
        if (to.Count == 0 && cc.Count == 0)
            throw new InvalidOperationException("No email recipients were resolved for this scheduled report.");

        await using var catalog = await _catalogFactory.CreateDbContextAsync(cancellationToken);
        var settings = await catalog.MailSettings
            .AsNoTracking()
            .Where(x => x.Preference == 1 && !x.Isdeleted)
            .OrderByDescending(x => x.SettingId)
            .FirstOrDefaultAsync(cancellationToken);

        if (settings == null
            || string.IsNullOrWhiteSpace(settings.EmailId)
            || string.IsNullOrWhiteSpace(settings.Password)
            || string.IsNullOrWhiteSpace(settings.OutgoingServer)
            || settings.OutgoingPort <= 0)
        {
            throw new InvalidOperationException("Mail settings are not configured.");
        }

        var subject = string.IsNullOrWhiteSpace(schedule.Subject)
            ? $"{data.Name} ΓÇö {DateTime.UtcNow:yyyy-MM-dd}"
            : schedule.Subject.Trim();
        var message = string.IsNullOrWhiteSpace(schedule.Message)
            ? $"Attached is the scheduled report <strong>{WebUtility.HtmlEncode(data.Name)}</strong> ({data.RowCount} row(s))."
            : WebUtility.HtmlEncode(schedule.Message).Replace("\n", "<br/>", StringComparison.Ordinal);

        using var mail = new MailMessage
        {
            From = new MailAddress(settings.EmailId),
            Subject = subject,
            Body = $"""
                <p>{message}</p>
                <p>Domain: {WebUtility.HtmlEncode(data.Domain)}<br/>
                Generated (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm}</p>
                """,
            IsBodyHtml = true
        };

        foreach (var addr in to)
            mail.To.Add(addr);
        foreach (var addr in cc)
            mail.CC.Add(addr);

        using var attachmentStream = new MemoryStream(file.Content);
        mail.Attachments.Add(new Attachment(attachmentStream, file.FileName, file.ContentType));

        using var smtp = new SmtpClient(settings.OutgoingServer, settings.OutgoingPort)
        {
            EnableSsl = true,
            Credentials = new NetworkCredential(settings.EmailId, settings.Password)
        };

        await smtp.SendMailAsync(mail, cancellationToken);
        _logger.LogInformation(
            "Scheduled report {ReportId} emailed to {ToCount} recipient(s) ({Format}).",
            config.Id,
            to.Count,
            schedule.Format);
    }

    private async Task<(List<string> To, List<string> Cc)> ResolveRecipientsAsync(
        ReportBuilderConfig config,
        CancellationToken cancellationToken)
    {
        var schedule = config.Schedule ?? new ReportScheduleConfig();
        var tokens = schedule.Recipients.Concat(schedule.Cc).Concat(config.SharedUsers).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var guidTokens = tokens
            .Select(t => Guid.TryParse(t, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var emailsByUser = guidTokens.Count == 0
            ? new Dictionary<Guid, string>()
            : (await _userEmails.GetEmailsAsync(guidTokens, cancellationToken)).ToDictionary(kv => kv.Key, kv => kv.Value);

        List<string> Resolve(IEnumerable<string> source)
        {
            var list = new List<string>();
            foreach (var token in source)
            {
                if (string.IsNullOrWhiteSpace(token))
                    continue;
                if (token.Contains('@', StringComparison.Ordinal))
                {
                    list.Add(token.Trim());
                    continue;
                }
                if (Guid.TryParse(token, out var id) && emailsByUser.TryGetValue(id, out var email))
                    list.Add(email);
            }
            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        var to = Resolve(schedule.Recipients);
        if (to.Count == 0)
            to = Resolve(config.SharedUsers);
        var cc = Resolve(schedule.Cc);
        cc = cc.Except(to, StringComparer.OrdinalIgnoreCase).ToList();
        return (to, cc);
    }
}
