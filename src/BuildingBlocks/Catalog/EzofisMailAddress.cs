using System.Net.Mail;

namespace SaaSApp.Catalog;

/// <summary>From address shown as "{user name} via EZOFIS" instead of the mailbox display name.</summary>
public static class EzofisMailAddress
{
    public static MailAddress From(string emailId, string? userName)
    {
        var name = string.IsNullOrWhiteSpace(userName) ? "EZOFIS" : userName.Trim();
        var display = name.EndsWith("via EZOFIS", StringComparison.OrdinalIgnoreCase)
            ? name
            : $"{name} via EZOFIS";
        return new MailAddress(emailId.Trim(), display);
    }
}
