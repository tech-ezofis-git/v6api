using System.Globalization;
using System.Text.RegularExpressions;
using Hangfire;

namespace SaaSApp.Reporting.Infrastructure.Services;

internal static class ReportCron
{
    public static bool TryBuild(string? recurrence, string? day, string? time, string? timezone, out string cron, out TimeZoneInfo zone)
    {
        cron = string.Empty;
        zone = ResolveTimeZone(timezone);

        var (hour, minute) = ParseTime(time);
        var kind = (recurrence ?? "Weekly").Trim();

        if (kind.Equals("Hourly", StringComparison.OrdinalIgnoreCase))
        {
            cron = $"{minute} * * * *";
            return true;
        }

        if (kind.Equals("Daily", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("Every Day", StringComparison.OrdinalIgnoreCase))
        {
            cron = $"{minute} {hour} * * *";
            return true;
        }

        if (kind.Equals("Monthly", StringComparison.OrdinalIgnoreCase))
        {
            var monthDay = 1;
            if (!string.IsNullOrWhiteSpace(day) && int.TryParse(day, out var parsedDay))
                monthDay = Math.Clamp(parsedDay, 1, 28);
            cron = $"{minute} {hour} {monthDay} * *";
            return true;
        }

        // Weekly (default)
        var dow = ParseDayOfWeek(day);
        cron = $"{minute} {hour} * * {dow}";
        return true;
    }

    public static TimeZoneInfo ResolveTimeZone(string? timezone)
    {
        if (string.IsNullOrWhiteSpace(timezone) || timezone.Equals("UTC", StringComparison.OrdinalIgnoreCase))
            return TimeZoneInfo.Utc;

        var raw = timezone.Trim();
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(raw);
        }
        catch (TimeZoneNotFoundException)
        {
            var mapped = raw.Equals("India Standard Time", StringComparison.OrdinalIgnoreCase)
                ? "Asia/Kolkata"
                : raw.Equals("Asia/Kolkata", StringComparison.OrdinalIgnoreCase)
                    ? "India Standard Time"
                    : raw;
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(mapped);
            }
            catch
            {
                return TimeZoneInfo.Utc;
            }
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    public static (int Hour, int Minute) ParseTime(string? time)
    {
        if (string.IsNullOrWhiteSpace(time))
            return (9, 0);

        var raw = time.Trim();
        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return (dt.Hour, dt.Minute);

        var match = Regex.Match(raw, @"^(?<h>\d{1,2}):(?<m>\d{2})\s*(?<ampm>AM|PM)?$", RegexOptions.IgnoreCase);
        if (!match.Success)
            return (9, 0);

        var hour = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minute = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        var ampm = match.Groups["ampm"].Value;
        if (!string.IsNullOrWhiteSpace(ampm))
        {
            if (ampm.Equals("PM", StringComparison.OrdinalIgnoreCase) && hour < 12)
                hour += 12;
            if (ampm.Equals("AM", StringComparison.OrdinalIgnoreCase) && hour == 12)
                hour = 0;
        }

        return (Math.Clamp(hour, 0, 23), Math.Clamp(minute, 0, 59));
    }

    public static int ParseDayOfWeek(string? day)
    {
        if (string.IsNullOrWhiteSpace(day))
            return 1; // Monday

        if (int.TryParse(day, out var n) && n is >= 0 and <= 6)
            return n;

        return day.Trim().ToLowerInvariant() switch
        {
            "sun" or "sunday" => 0,
            "mon" or "monday" => 1,
            "tue" or "tues" or "tuesday" => 2,
            "wed" or "wednesday" => 3,
            "thu" or "thur" or "thurs" or "thursday" => 4,
            "fri" or "friday" => 5,
            "sat" or "saturday" => 6,
            _ => 1
        };
    }

    public static bool HangfireConfigured()
    {
        try
        {
            return JobStorage.Current != null;
        }
        catch
        {
            return false;
        }
    }
}
