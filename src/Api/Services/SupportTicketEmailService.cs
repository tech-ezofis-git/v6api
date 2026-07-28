using System.Net;
using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SaaSApp.Api.Services.Jira;
using SaaSApp.Catalog.Entities;
using SaaSApp.Catalog.Persistence;

namespace SaaSApp.Api.Services;

public sealed class SupportTicketEmailContext
{
    public Guid TicketId { get; init; }
    public Guid TenantId { get; init; }
    public string? CallerEmail { get; init; }
    public string? SupportCategory { get; init; }
    public string? Priorty { get; init; }
    public string? PreferredContact { get; init; }
    public string? PhoneNO { get; init; }
    public string? RequestDescription { get; init; }
    public string? JiraIssueKey { get; init; }
    public string? JiraIssueUrl { get; init; }
    public DateTime CreatedAtUtc { get; init; }
}

/// <summary>Sends acknowledgment and support-team notification emails after a successful Jira ticket create.</summary>
public sealed class SupportTicketEmailService
{
    private readonly IDbContextFactory<CatalogDbContext> _catalogFactory;
    private readonly JiraOptions _jiraOptions;
    private readonly ILogger<SupportTicketEmailService> _logger;

    public SupportTicketEmailService(
        IDbContextFactory<CatalogDbContext> catalogFactory,
        IOptions<JiraOptions> jiraOptions,
        ILogger<SupportTicketEmailService> logger)
    {
        _catalogFactory = catalogFactory;
        _jiraOptions = jiraOptions.Value;
        _logger = logger;
    }

    public async Task SendSuccessNotificationsAsync(
        SupportTicketEmailContext context,
        CancellationToken cancellationToken)
    {
        try
        {
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
                _logger.LogWarning("Support ticket emails not sent: mailsettings not configured.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(context.CallerEmail))
            {
                await SendAsync(
                    settings,
                    context.CallerEmail.Trim(),
                    "Your support request has been received",
                    BuildCallerBody(context),
                    cancellationToken);
            }

            var supportEmail = _jiraOptions.Email?.Trim();
            if (!string.IsNullOrWhiteSpace(supportEmail))
            {
                var category = string.IsNullOrWhiteSpace(context.SupportCategory)
                    ? "Support request"
                    : context.SupportCategory.Trim();
                await SendAsync(
                    settings,
                    supportEmail,
                    $"New support ticket: {category}",
                    BuildSupportTeamBody(context),
                    cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send support ticket notification emails for ticket {TicketId}", context.TicketId);
        }
    }

    private static async Task SendAsync(
        MailSetting settings,
        string to,
        string subject,
        string htmlBody,
        CancellationToken cancellationToken)
    {
        using var mail = new MailMessage
        {
            From = new MailAddress(settings.EmailId),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true
        };
        mail.To.Add(to);

        using var smtp = new SmtpClient(settings.OutgoingServer, settings.OutgoingPort)
        {
            EnableSsl = true,
            Credentials = new NetworkCredential(settings.EmailId, settings.Password)
        };

        await smtp.SendMailAsync(mail, cancellationToken);
    }

    private static string BuildCallerBody(SupportTicketEmailContext ctx)
    {
        var jiraLine = string.IsNullOrWhiteSpace(ctx.JiraIssueKey)
            ? ""
            : $"<p><strong>Reference:</strong> {Encode(ctx.JiraIssueKey)}"
              + (string.IsNullOrWhiteSpace(ctx.JiraIssueUrl)
                  ? "</p>"
                  : $" (<a href=\"{EncodeAttr(ctx.JiraIssueUrl)}\">view ticket</a>)</p>");

        return $"""
            <p>Thank you for contacting Ezofis support.</p>
            <p>We have received your request and notified our team. We will contact you soon.</p>
            <h3>Your request details</h3>
            <ul>
              <li><strong>Category:</strong> {EncodeOrDash(ctx.SupportCategory)}</li>
              <li><strong>Priority:</strong> {EncodeOrDash(ctx.Priorty)}</li>
              <li><strong>Email:</strong> {EncodeOrDash(ctx.CallerEmail)}</li>
              <li><strong>Phone:</strong> {EncodeOrDash(ctx.PhoneNO)}</li>
            </ul>
            <p><strong>Description:</strong><br/>{EncodeOrDash(ctx.RequestDescription)?.Replace("\n", "<br/>", StringComparison.Ordinal)}</p>
            {jiraLine}
            """;
    }

    private static string BuildSupportTeamBody(SupportTicketEmailContext ctx)
    {
        var jiraLine = string.IsNullOrWhiteSpace(ctx.JiraIssueKey)
            ? "<li><strong>Jira issue:</strong> —</li>"
            : $"<li><strong>Jira issue:</strong> {Encode(ctx.JiraIssueKey)}"
              + (string.IsNullOrWhiteSpace(ctx.JiraIssueUrl)
                  ? "</li>"
                  : $" (<a href=\"{EncodeAttr(ctx.JiraIssueUrl)}\">open</a>)</li>");

        return $"""
            <p>A new support ticket was submitted.</p>
            <ul>
              <li><strong>Ticket id:</strong> {ctx.TicketId}</li>
              <li><strong>Tenant id:</strong> {ctx.TenantId}</li>
              <li><strong>Caller email:</strong> {EncodeOrDash(ctx.CallerEmail)}</li>
              <li><strong>Category:</strong> {EncodeOrDash(ctx.SupportCategory)}</li>
              <li><strong>Priority:</strong> {EncodeOrDash(ctx.Priorty)}</li>
              <li><strong>Email:</strong> {EncodeOrDash(ctx.CallerEmail)}</li>
              <li><strong>Phone:</strong> {EncodeOrDash(ctx.PhoneNO)}</li>
              <li><strong>Submitted (UTC):</strong> {ctx.CreatedAtUtc:yyyy-MM-dd HH:mm:ss}</li>
              {jiraLine}
            </ul>
            <p><strong>Description:</strong><br/>{EncodeOrDash(ctx.RequestDescription)?.Replace("\n", "<br/>", StringComparison.Ordinal)}</p>
            """;
    }

    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string EncodeOrDash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : WebUtility.HtmlEncode(value.Trim());

    private static string EncodeAttr(string value) => WebUtility.HtmlEncode(value);
}
