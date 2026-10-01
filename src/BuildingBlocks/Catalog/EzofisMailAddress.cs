using System.Net.Mail;

namespace SaaSApp.Catalog;

/// <summary>From address for product mail. OTP stays "EZOFIS". User notifications use "Display Name (via Ezofis)".</summary>
public static class EzofisMailAddress
{
    public static MailAddress System(string emailId) =>
        new(emailId.Trim(), "EZOFIS");

    public static MailAddress From(string emailId, string? userName)
    {
        var name = StripViaSuffix(userName);
        if (string.IsNullOrWhiteSpace(name)
            || string.Equals(name, "Ezofis", StringComparison.OrdinalIgnoreCase))
        {
            return System(emailId);
        }

        return new MailAddress(emailId.Trim(), $"{name} (via Ezofis)");
    }

    private static string? StripViaSuffix(string? userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
            return null;

        var name = userName.Trim();
        string[] suffixes = [" (via Ezofis)", " (via EZOFIS)", " via Ezofis", " via EZOFIS"];
        foreach (var suffix in suffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return name[..^suffix.Length].Trim();
        }

        return name;
    }
}
