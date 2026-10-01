using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using System.Text.RegularExpressions;

namespace SaaSApp.Catalog;

/// <summary>
/// OTP email laid out like a verification code message: logo, identity line, and one numeric code.
/// The code is also sent as plain text so mailbox clients can offer Copy code.
/// The logo is a hosted image, not an attached file.
/// </summary>
public static class EzofisOtpMail
{
    public const string LogoUrl = "https://app.ezofis.com/logo/main_black.png";
    public const string Subject = "[Ezofis] Sudo email verification code";

    public static void Apply(
        MailMessage mail,
        string identityName,
        string code,
        string codeLabel,
        string validFor)
    {
        var (html, plain) = Create(identityName, code, codeLabel, validFor);
        // Body is ignored once an alternate view exists, so the code must be its own text/plain part.
        mail.Body = string.Empty;
        mail.IsBodyHtml = false;

        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(plain, Encoding.UTF8, MediaTypeNames.Text.Plain));
        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(html, Encoding.UTF8, MediaTypeNames.Text.Html));
    }

    /// <summary>
    /// Encodes text for HTML and keeps file names as plain text. A zero-width space before the
    /// extension stops mailbox clients from turning "Invoice_1.pdf" into a link.
    /// </summary>
    public static string EncodeDisplayText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        var encoded = WebUtility.HtmlEncode(value);
        return Regex.Replace(
            encoded,
            @"\.(pdf|docx?|xlsx?|pptx?|csv|txt|zip|png|jpe?g|gif|xml)\b",
            "&#8203;.$1",
            RegexOptions.IgnoreCase);
    }

    public static (string Html, string Plain) Create(
        string identityName,
        string code,
        string codeLabel,
        string validFor)
    {
        var name = string.IsNullOrWhiteSpace(identityName) ? "there" : identityName.Trim();
        var heading = "Please verify your identity, <strong>" + WebUtility.HtmlEncode(name) + "</strong>";
        var otp = code.Trim();
        var safeCode = WebUtility.HtmlEncode(otp);
        var safeLabel = WebUtility.HtmlEncode(codeLabel.Trim());
        var safeValid = WebUtility.HtmlEncode(validFor.Trim());
        var logo = $"""<img src="{LogoUrl}" alt="Ezofis" width="160" style="display:block;border:0;outline:none;text-decoration:none;height:auto;margin:0 auto 28px;" />""";

        var html = $$"""
            <!DOCTYPE html>
            <html>
            <body style="margin:0;padding:0;background:#ffffff;">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#ffffff;">
                <tr>
                  <td align="center" style="padding:48px 16px 32px;">
                    {{logo}}
                    <p style="margin:0 0 28px;font-family:Arial,Helvetica,sans-serif;font-size:22px;line-height:1.35;font-weight:400;color:#1f2328;">
                      {{heading}}
                    </p>
                    <table role="presentation" width="480" cellpadding="0" cellspacing="0" style="max-width:480px;width:100%;border:1px solid #d0d7de;border-radius:6px;">
                      <tr>
                        <td style="padding:22px 24px 4px;font-family:Arial,Helvetica,sans-serif;font-size:16px;line-height:1.5;font-weight:400;color:#1f2328;text-align:left;">
                          {{safeLabel}}
                        </td>
                      </tr>
                      <tr>
                        <td align="center" style="padding:16px 24px 8px;font-family:Arial,Helvetica,sans-serif;font-size:28px;line-height:1.2;font-weight:400;letter-spacing:4px;color:#1f2328;">
                          {{safeCode}}
                        </td>
                      </tr>
                      <tr>
                        <td style="padding:12px 24px 4px;font-family:Arial,Helvetica,sans-serif;font-size:14px;line-height:1.5;font-weight:400;color:#1f2328;text-align:left;">
                          This code is valid for <strong>{{safeValid}}</strong> and can only be used once.
                        </td>
                      </tr>
                      <tr>
                        <td style="padding:8px 24px 4px;font-family:Arial,Helvetica,sans-serif;font-size:14px;line-height:1.5;font-weight:400;color:#1f2328;text-align:left;">
                          <strong>Please don't share this code with anyone:</strong> we'll never ask for it on the phone or via email.
                        </td>
                      </tr>
                      <tr>
                        <td style="padding:16px 24px 22px;font-family:Arial,Helvetica,sans-serif;font-size:14px;line-height:1.5;font-weight:400;color:#1f2328;text-align:left;">
                          Thanks,<br/>The Ezofis Team
                        </td>
                      </tr>
                    </table>
                    <p style="margin:28px 0 0;max-width:480px;font-family:Arial,Helvetica,sans-serif;font-size:14px;line-height:1.5;font-weight:400;color:#656d76;">
                      You're receiving this email because a verification code was requested for your Ezofis account. If this wasn't you, please ignore this email.
                    </p>
                  </td>
                </tr>
              </table>
            </body>
            </html>
            """;

        var plain = $"""
            Please verify your identity, {name}

            {codeLabel.Trim()}

            {otp}

            This code is valid for {validFor.Trim()} and can only be used once.
            Please don't share this code with anyone: we'll never ask for it on the phone or via email.
            """;

        return (html, plain);
    }
}
