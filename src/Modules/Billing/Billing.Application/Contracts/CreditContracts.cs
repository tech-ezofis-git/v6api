namespace SaaSApp.Billing.Application.Contracts;

public enum CreditUpdateStatus
{
    Failed = 0,
    Success = 1,
    LimitExceeded = 2
}

public enum CreditUsagePeriod
{
    Today,
    Yesterday,
    Monthly,
    Quarterly,
    Yearly
}

public sealed record CreditUpdateRequest(
    string ActivityType,
    string SubActivity,
    string Identify,
    int IdentifyId,
    string? Remarks,
    int Credit = 1,
    string? Env = null,
    int? InputTokens = null,
    int? OutputTokens = null,
    int? TotalTokens = null);

public sealed record CreditUpdateResult(CreditUpdateStatus Status, string Message);

public sealed record CreditMasterDto(
    int Id,
    Guid TenantId,
    int AllocationMonth,
    int AllocationYear,
    string? CreditType,
    int InitialCredit,
    int BalanceCredit,
    string? Remarks,
    string? Status,
    int? OverallConsumedCredit,
    DateTime? ValidFromDate,
    DateTime? ValidToDate,
    int? CarryForwardCredit = null,
    int? TopUpBalanceCredit = null,
    int? ExtraConsumedCredit = null,
    /// <summary>Calculated remaining for the allocation month (initial + carryForward + topUp − overallConsumed).</summary>
    int MonthlyBalance = 0);

/// <summary>One month’s credit master balance for yearly / history views.</summary>
public sealed record CreditMonthlyBalanceDto(
    int Id,
    Guid TenantId,
    int AllocationMonth,
    int AllocationYear,
    string Label,
    string? CreditType,
    int InitialCredit,
    int? CarryForwardCredit,
    int? TopUpBalanceCredit,
    int? OverallConsumedCredit,
    int? ExtraConsumedCredit,
    int BalanceCredit,
    int MonthlyBalance,
    string? Status);

public static class CreditBalanceCalculator
{
    /// <summary>
    /// Monthly remaining balance from credit master fields:
    /// initial + carryForward + topUp − overallConsumed.
    /// </summary>
    public static int CalculateMonthlyBalance(
        int initialCredit,
        int? carryForwardCredit = null,
        int? topUpBalanceCredit = null,
        int? overallConsumedCredit = null)
    {
        var opening = initialCredit
                      + (carryForwardCredit ?? 0)
                      + (topUpBalanceCredit ?? 0);
        var consumed = overallConsumedCredit ?? 0;
        return opening - consumed;
    }

    public static int CalculateMonthlyBalance(CreditMasterDto master) =>
        CalculateMonthlyBalance(
            master.InitialCredit,
            master.CarryForwardCredit,
            master.TopUpBalanceCredit,
            master.OverallConsumedCredit);
}

public sealed record CreditTransactionItemDto(
    int Id,
    /// <summary>Agent / service that consumed credits (UI column: Agent). Formerly activityType / Category.</summary>
    string? Agent,
    string? SubActivityType,
    /// <summary>Document or invoice filename/reference (UI column: Filename). Formerly identifyTable / Reference.</summary>
    string? FileName,
    int? IdentifyId,
    string? Remarks,
    int Credit,
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens,
    DateTime? CreatedAt);

public sealed record CreditUsageResult(
    CreditUsagePeriod Period,
    DateTime RangeStartUtc,
    DateTime RangeEndUtc,
    int TotalCreditsConsumed,
    int TransactionCount,
    IReadOnlyList<CreditTransactionItemDto> Transactions);

public sealed record CreditUsageReportRequest(
    string Period = "monthly",
    int? Year = null,
    int? Month = null);

public sealed record CreditUsageTypeSummaryDto(string Type, int CreditsUsed);

public sealed record CreditUsageTimelinePointDto(string Label, int CreditsUsed, DateTime? BucketStartUtc);

public sealed record CreditUsageMonthSummaryDto(int Year, int Month, string Label, int CreditsUsed);

/// <summary>Dashboard payload for credit usage widgets (summary, distribution, timeline, monthly list, pie chart).</summary>
public sealed record CreditUsageDashboardResult(
    CreditUsagePeriod Period,
    string PeriodLabel,
    DateTime RangeStartUtc,
    DateTime RangeEndUtc,
    int TotalCreditsConsumed,
    int TransactionCount,
    IReadOnlyList<CreditUsageTypeSummaryDto> HighestConsumption,
    IReadOnlyList<CreditUsageTypeSummaryDto> DistributionReport,
    /// <summary>Service-level split for the Overall Credit Split pie chart (AP Agent, OCR Agent, etc.).</summary>
    IReadOnlyList<CreditUsageTypeSummaryDto> OverallCreditSplit,
    IReadOnlyList<CreditUsageTimelinePointDto> Timeline,
    IReadOnlyList<CreditUsageMonthSummaryDto>? MonthlyConsumption,
    IReadOnlyList<CreditTransactionItemDto> Transactions);

public static class CreditUsagePeriodParser
{
    public static CreditUsagePeriod Parse(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "today" => CreditUsagePeriod.Today,
            "yesterday" => CreditUsagePeriod.Yesterday,
            "monthly" => CreditUsagePeriod.Monthly,
            "quarterly" => CreditUsagePeriod.Quarterly,
            "yearly" => CreditUsagePeriod.Yearly,
            _ => Enum.TryParse<CreditUsagePeriod>(value, true, out var parsed)
                ? parsed
                : CreditUsagePeriod.Monthly
        };
}
