using System.Globalization;
using SaaSApp.Billing.Application.Contracts;

namespace SaaSApp.Billing.Infrastructure.Services;

internal static class CreditUsageDashboardBuilder
{
    public static CreditUsageDashboardResult Build(
        CreditUsagePeriod period,
        DateTime rangeStartUtc,
        DateTime rangeEndUtc,
        IReadOnlyList<CreditTransactionItemDto> transactions)
    {
        var ist = IndiaTimeZone();
        var total = transactions.Sum(t => t.Credit);

        var highest = transactions
            .GroupBy(t => ResolveCategory(t.Agent, t.SubActivityType))
            .Select(g => new CreditUsageTypeSummaryDto(g.Key, g.Sum(x => x.Credit)))
            .OrderByDescending(x => x.CreditsUsed)
            .ToList();

        var distribution = transactions
            .GroupBy(t => ResolveDistributionLabel(t.Agent, t.SubActivityType, t.Remarks))
            .Select(g => new CreditUsageTypeSummaryDto(g.Key, g.Sum(x => x.Credit)))
            .OrderByDescending(x => x.CreditsUsed)
            .ToList();

        var overallSplit = transactions
            .GroupBy(t => ResolveOverallCreditSplitLabel(t.Agent, t.SubActivityType, t.Remarks))
            .Select(g => new CreditUsageTypeSummaryDto(g.Key, g.Sum(x => x.Credit)))
            .OrderByDescending(x => x.CreditsUsed)
            .ToList();

        var timeline = BuildTimeline(period, rangeStartUtc, rangeEndUtc, transactions, ist);
        var monthlyConsumption = BuildMonthlyConsumption(period, rangeStartUtc, rangeEndUtc, transactions, ist);

        return new CreditUsageDashboardResult(
            period,
            BuildPeriodLabel(period, rangeStartUtc, rangeEndUtc, ist),
            rangeStartUtc,
            rangeEndUtc,
            total,
            transactions.Count,
            AppendTotalRow(highest, total),
            // Do not append a Total row here — clients sum distribution rows for the
            // "by activity · N credits" subtitle; including Total doubled the figure (e.g. 20+20=40).
            distribution,
            overallSplit,
            timeline,
            monthlyConsumption,
            transactions.OrderByDescending(t => t.CreatedAt).ToList());
    }

    private static IReadOnlyList<CreditUsageTimelinePointDto> BuildTimeline(
        CreditUsagePeriod period,
        DateTime rangeStartUtc,
        DateTime rangeEndUtc,
        IReadOnlyList<CreditTransactionItemDto> transactions,
        TimeZoneInfo ist)
    {
        return period switch
        {
            CreditUsagePeriod.Monthly => BuildWeeklyTimeline(rangeStartUtc, transactions, ist),
            CreditUsagePeriod.Quarterly => BuildMonthlyTimelineInRange(rangeStartUtc, rangeEndUtc, transactions, ist),
            CreditUsagePeriod.Yearly => BuildMonthlyTimelineInRange(rangeStartUtc, rangeEndUtc, transactions, ist),
            CreditUsagePeriod.Today => BuildHourlyTimeline(rangeStartUtc, rangeEndUtc, transactions, ist),
            CreditUsagePeriod.Yesterday => BuildSingleBucketTimeline("Yesterday", transactions),
            _ => BuildSingleBucketTimeline("Period", transactions)
        };
    }

    private static IReadOnlyList<CreditUsageMonthSummaryDto>? BuildMonthlyConsumption(
        CreditUsagePeriod period,
        DateTime rangeStartUtc,
        DateTime rangeEndUtc,
        IReadOnlyList<CreditTransactionItemDto> transactions,
        TimeZoneInfo ist)
    {
        if (period is not (CreditUsagePeriod.Yearly or CreditUsagePeriod.Quarterly))
            return null;

        var startIst = TimeZoneInfo.ConvertTimeFromUtc(rangeStartUtc, ist);
        var endIst = TimeZoneInfo.ConvertTimeFromUtc(rangeEndUtc, ist);
        var cursor = new DateTime(startIst.Year, startIst.Month, 1);
        var endMonth = new DateTime(endIst.Year, endIst.Month, 1);
        var buckets = new Dictionary<(int Year, int Month), int>();

        while (cursor < endMonth)
        {
            buckets[(cursor.Year, cursor.Month)] = 0;
            cursor = cursor.AddMonths(1);
        }

        foreach (var tx in transactions)
        {
            if (tx.CreatedAt is null)
                continue;

            var local = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(tx.CreatedAt.Value, DateTimeKind.Utc),
                ist);
            var key = (local.Year, local.Month);
            if (!buckets.ContainsKey(key))
                continue;

            buckets[key] += tx.Credit;
        }

        return buckets
            .OrderBy(x => x.Key.Year)
            .ThenBy(x => x.Key.Month)
            .Select(x => new CreditUsageMonthSummaryDto(
                x.Key.Year,
                x.Key.Month,
                $"{CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(x.Key.Month)} - {x.Key.Year}",
                x.Value))
            .ToList();
    }

    private static IReadOnlyList<CreditUsageTimelinePointDto> BuildWeeklyTimeline(
        DateTime rangeStartUtc,
        IReadOnlyList<CreditTransactionItemDto> transactions,
        TimeZoneInfo ist)
    {
        var buckets = new Dictionary<int, int> { [1] = 0, [2] = 0, [3] = 0, [4] = 0, [5] = 0 };
        foreach (var tx in transactions)
        {
            if (tx.CreatedAt is null)
                continue;

            var local = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(tx.CreatedAt.Value, DateTimeKind.Utc),
                ist);
            var week = Math.Min(5, ((local.Day - 1) / 7) + 1);
            buckets[week] += tx.Credit;
        }

        return buckets
            .Where(x => x.Key <= 4 || x.Value > 0)
            .OrderBy(x => x.Key)
            .Select(x => new CreditUsageTimelinePointDto($"Week {x.Key}", x.Value, null))
            .ToList();
    }

    private static IReadOnlyList<CreditUsageTimelinePointDto> BuildMonthlyTimelineInRange(
        DateTime rangeStartUtc,
        DateTime rangeEndUtc,
        IReadOnlyList<CreditTransactionItemDto> transactions,
        TimeZoneInfo ist)
    {
        var startIst = TimeZoneInfo.ConvertTimeFromUtc(rangeStartUtc, ist);
        var endIst = TimeZoneInfo.ConvertTimeFromUtc(rangeEndUtc, ist);
        var cursor = new DateTime(startIst.Year, startIst.Month, 1);
        var endMonth = new DateTime(endIst.Year, endIst.Month, 1);
        var buckets = new List<(string Label, int Year, int Month, int Credits)>();

        while (cursor < endMonth)
        {
            buckets.Add((
                CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(cursor.Month),
                cursor.Year,
                cursor.Month,
                0));
            cursor = cursor.AddMonths(1);
        }

        foreach (var tx in transactions)
        {
            if (tx.CreatedAt is null)
                continue;

            var local = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(tx.CreatedAt.Value, DateTimeKind.Utc),
                ist);
            var index = buckets.FindIndex(b => b.Year == local.Year && b.Month == local.Month);
            if (index < 0)
                continue;

            var current = buckets[index];
            buckets[index] = (current.Label, current.Year, current.Month, current.Credits + tx.Credit);
        }

        return buckets
            .Select(b => new CreditUsageTimelinePointDto(b.Label, b.Credits, null))
            .ToList();
    }

    private static IReadOnlyList<CreditUsageTimelinePointDto> BuildHourlyTimeline(
        DateTime rangeStartUtc,
        DateTime rangeEndUtc,
        IReadOnlyList<CreditTransactionItemDto> transactions,
        TimeZoneInfo ist)
    {
        var buckets = new Dictionary<int, int>();
        for (var hour = 0; hour < 24; hour++)
            buckets[hour] = 0;

        foreach (var tx in transactions)
        {
            if (tx.CreatedAt is null)
                continue;

            var local = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(tx.CreatedAt.Value, DateTimeKind.Utc),
                ist);
            buckets[local.Hour] += tx.Credit;
        }

        return buckets
            .Where(x => x.Value > 0)
            .OrderBy(x => x.Key)
            .Select(x => new CreditUsageTimelinePointDto($"{x.Key:00}:00", x.Value, null))
            .ToList();
    }

    private static IReadOnlyList<CreditUsageTimelinePointDto> BuildSingleBucketTimeline(
        string label,
        IReadOnlyList<CreditTransactionItemDto> transactions) =>
        [new CreditUsageTimelinePointDto(label, transactions.Sum(t => t.Credit), null)];

    private static IReadOnlyList<CreditUsageTypeSummaryDto> AppendTotalRow(
        IReadOnlyList<CreditUsageTypeSummaryDto> rows,
        int total)
    {
        var list = rows.ToList();
        list.Add(new CreditUsageTypeSummaryDto("Total", total));
        return list;
    }

    private static string ResolveCategory(string? activityType, string? subActivity)
    {
        var combined = $"{activityType} {subActivity}".ToLowerInvariant();
        if (combined.Contains("document summary", StringComparison.Ordinal)
            || combined.Contains("documentsummary", StringComparison.Ordinal))
        {
            return "Document Summary";
        }

        if (combined.Contains("ocr", StringComparison.Ordinal))
            return "OCR Agent";

        if (combined.Contains("ap agent", StringComparison.Ordinal)
            || combined.Contains("apagent", StringComparison.Ordinal)
            || combined.Contains("po line", StringComparison.Ordinal)
            || combined.Contains("supplier", StringComparison.Ordinal)
            || combined.Contains("duplicate", StringComparison.Ordinal)
            || combined.Contains("back order", StringComparison.Ordinal))
        {
            return "AP Agent";
        }

        return string.IsNullOrWhiteSpace(activityType)
            ? (string.IsNullOrWhiteSpace(subActivity) ? "Other" : subActivity.Trim())
            : activityType.Trim();
    }

    private static string ResolveDistributionLabel(string? activityType, string? subActivity, string? remarks)
    {
        var sub = subActivity?.Trim() ?? string.Empty;
        var activity = activityType?.Trim() ?? string.Empty;
        var remark = remarks?.Trim() ?? string.Empty;
        var combined = $"{activity} {sub} {remark}".ToLowerInvariant();

        if (combined.Contains("invoice ocr", StringComparison.Ordinal)
            || sub.Equals("AI OCR", StringComparison.OrdinalIgnoreCase)
            || activity.Contains("OCR", StringComparison.OrdinalIgnoreCase))
        {
            return "Invoice OCR Extraction";
        }

        if (combined.Contains("po line", StringComparison.Ordinal) || combined.Contains("line matching", StringComparison.Ordinal))
            return "PO Line Matching";

        if (combined.Contains("document summary", StringComparison.Ordinal) || combined.Contains("documentsummary", StringComparison.Ordinal))
            return "Document Summary";

        if (combined.Contains("supplier", StringComparison.Ordinal))
            return "Supplier Master Validation";

        if (combined.Contains("duplicate", StringComparison.Ordinal))
            return "Duplicate Invoice Check";

        if (combined.Contains("back order", StringComparison.Ordinal))
            return "Back Order Detection";

        if (!string.IsNullOrWhiteSpace(sub))
            return sub;

        return string.IsNullOrWhiteSpace(activity) ? "Other" : activity;
    }

    /// <summary>Maps transactions to pie-chart service labels (Overall Credit Split widget).</summary>
    private static string ResolveOverallCreditSplitLabel(string? activityType, string? subActivity, string? remarks)
    {
        var activity = activityType?.Trim() ?? string.Empty;
        var sub = subActivity?.Trim() ?? string.Empty;
        var remark = remarks?.Trim() ?? string.Empty;
        var combined = $"{activity} {sub} {remark}".ToLowerInvariant();

        if (combined.Contains("document summary", StringComparison.Ordinal)
            || combined.Contains("documentsummary", StringComparison.Ordinal))
        {
            return "Document Summary";
        }

        if (combined.Contains("supplier", StringComparison.Ordinal))
            return "Supplier Validation";

        if (combined.Contains("duplicate", StringComparison.Ordinal))
            return "Duplicate Detection";

        if (combined.Contains("back order", StringComparison.Ordinal)
            || combined.Contains("backorder", StringComparison.Ordinal))
        {
            return "Back Order Detection";
        }

        if (combined.Contains("ocr", StringComparison.Ordinal)
            || activity.Equals("OCR Agent", StringComparison.OrdinalIgnoreCase))
        {
            return "OCR Agent";
        }

        if (combined.Contains("ap agent", StringComparison.Ordinal)
            || combined.Contains("apagent", StringComparison.Ordinal)
            || activity.Equals("AP Agent", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("po line", StringComparison.Ordinal)
            || combined.Contains("line matching", StringComparison.Ordinal))
        {
            return "AP Agent";
        }

        if (!string.IsNullOrWhiteSpace(activity))
            return activity;

        return string.IsNullOrWhiteSpace(sub) ? "Other" : sub;
    }

    private static string BuildPeriodLabel(
        CreditUsagePeriod period,
        DateTime rangeStartUtc,
        DateTime rangeEndUtc,
        TimeZoneInfo ist)
    {
        var startIst = TimeZoneInfo.ConvertTimeFromUtc(rangeStartUtc, ist);
        return period switch
        {
            CreditUsagePeriod.Today => $"Today ({startIst:dd MMM yyyy})",
            CreditUsagePeriod.Yesterday => $"Yesterday ({startIst:dd MMM yyyy})",
            CreditUsagePeriod.Monthly => $"{startIst:MMMM yyyy}",
            CreditUsagePeriod.Quarterly => $"Q{((startIst.Month - 1) / 3) + 1} {startIst.Year}",
            CreditUsagePeriod.Yearly => $"{startIst.Year}",
            _ => $"{startIst:d} - {TimeZoneInfo.ConvertTimeFromUtc(rangeEndUtc, ist):d}"
        };
    }

    private static TimeZoneInfo IndiaTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        }
    }
}
