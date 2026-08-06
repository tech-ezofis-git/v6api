using System.Globalization;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Infrastructure.Services;

internal static class ApDashboardBuilder
{
  public static ApDashboardResult Build(
    ApDashboardRequest request,
    DateTime rangeStartUtc,
    DateTime rangeEndUtc,
    DateTime previousRangeStartUtc,
    DateTime previousRangeEndUtc,
    IReadOnlyList<ApDashboardInvoiceDto> currentInvoices,
    IReadOnlyList<ApDashboardInvoiceDto> previousInvoices,
    ApDashboardFilterOptionsDto filterOptions)
  {
    var periodLabel = BuildPeriodLabel(request.Period, rangeStartUtc, rangeEndUtc);
    var header = BuildHeader(currentInvoices, request, periodLabel);
    var kpis = BuildKpis(request.Period, currentInvoices, previousInvoices);
    var riskRadar = BuildSupplierRiskRadar(currentInvoices);
    var profitVsAp = BuildProfitVsAp(currentInvoices);
    var monthlyTrend = BuildMonthlyPaymentTrend(currentInvoices);
    var cashFlow = BuildCashFlowForecast(currentInvoices, rangeEndUtc);
    var topSuppliers = BuildTopSuppliers(currentInvoices, outstandingOnly: false);
    var outstandingSuppliers = BuildTopSuppliers(currentInvoices, outstandingOnly: true);
    var departments = BuildDepartmentSpend(currentInvoices);
    var geography = BuildGeography(currentInvoices);
    var activeFilters = ApDashboardFilterSupport.BuildActiveFilters(request);
    var profitability = BuildProfitabilityCashPosition(currentInvoices, rangeEndUtc);
    var supplierConcentration = BuildSupplierConcentrationRisk(currentInvoices, riskRadar);
    var agingOversight = BuildAgingProcessOversight(currentInvoices, rangeEndUtc);
    var invoiceAging = BuildInvoiceAgingAnalysis(currentInvoices, rangeEndUtc);

    return new ApDashboardResult(
      request.Period,
      periodLabel,
      rangeStartUtc,
      rangeEndUtc,
      header,
      kpis,
      riskRadar,
      profitVsAp,
      monthlyTrend,
      cashFlow,
      topSuppliers,
      outstandingSuppliers,
      departments,
      geography,
      filterOptions,
      activeFilters,
      profitability,
      supplierConcentration,
      agingOversight,
      invoiceAging,
      request.IncludeInvoiceDetails ? currentInvoices : null);
  }

  private static ApDashboardHeaderDto BuildHeader(
    IReadOnlyList<ApDashboardInvoiceDto> invoices,
    ApDashboardRequest request,
    string periodLabel)
  {
    var outstanding = invoices
      .Where(i => !IsPaid(i.PaymentStatus))
      .Sum(i => i.Amount);

    var overdue = invoices
      .Where(i => string.Equals(i.PaymentStatus, "overdue", StringComparison.OrdinalIgnoreCase))
      .Sum(i => i.Amount);

    var open = invoices.Count(i => !IsPaid(i.PaymentStatus));

    var dpo = invoices
      .Where(i => i.InvoiceDate.HasValue && i.ProcessingDays.HasValue)
      .Select(i => (double)i.ProcessingDays!.Value)
      .DefaultIfEmpty(0)
      .Average();

    var dpoRounded = (decimal)Math.Round(dpo, 0);
    var context = BuildContextLabel(request, periodLabel);

    return new ApDashboardHeaderDto(
      outstanding,
      FormatMoney(outstanding),
      overdue,
      FormatMoney(overdue),
      open,
      dpoRounded,
      $"{dpoRounded:0}d",
      context);
  }

  private static string BuildContextLabel(ApDashboardRequest request, string periodLabel)
  {
    var periodPart = request.Period switch
    {
      ApDashboardPeriod.Today => "today",
      ApDashboardPeriod.Tomorrow => "tomorrow",
      ApDashboardPeriod.ThisWeek => "week",
      ApDashboardPeriod.ThisMonth => "month",
      ApDashboardPeriod.LastMonth => "last month",
      ApDashboardPeriod.ThisQuarter => "quarter",
      ApDashboardPeriod.ThisYear => "year",
      _ => periodLabel
    };

    var supplierPart = !string.IsNullOrWhiteSpace(request.Supplier)
        && !request.Supplier.Equals("all", StringComparison.OrdinalIgnoreCase)
      ? request.Supplier.Trim()
      : !string.IsNullOrWhiteSpace(request.Department)
        && !request.Department.Equals("all", StringComparison.OrdinalIgnoreCase)
        ? request.Department.Trim()
        : "all suppliers";

    return $"Real-time · {periodPart} · {supplierPart}";
  }

  private static ApDashboardSupplierRiskRadarDto BuildSupplierRiskRadar(
    IReadOnlyList<ApDashboardInvoiceDto> invoices)
  {
    var outstanding = invoices.Where(i => !IsPaid(i.PaymentStatus)).ToList();
    var bySupplier = outstanding
      .GroupBy(i => string.IsNullOrWhiteSpace(i.Supplier) ? "Unknown" : i.Supplier.Trim(), StringComparer.OrdinalIgnoreCase)
      .Select(g =>
      {
        var amount = g.Sum(x => x.Amount);
        var openCount = g.Count();
        var overdueCount = g.Count(x => string.Equals(x.PaymentStatus, "overdue", StringComparison.OrdinalIgnoreCase));
        var highCount = g.Count(x => string.Equals(x.RiskLevel, "high", StringComparison.OrdinalIgnoreCase));
        var mediumCount = g.Count(x => string.Equals(x.RiskLevel, "medium", StringComparison.OrdinalIgnoreCase));
        var risk = ResolveSupplierRiskLevel(amount, overdueCount, openCount, highCount, mediumCount);
        return new ApDashboardSupplierRiskDto(
          g.Key,
          risk,
          amount,
          FormatMoney(amount),
          openCount,
          overdueCount,
          g.Select(x => x.CountryCode).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)),
          g.Select(x => x.Currency).FirstOrDefault() ?? "USD");
      })
      .OrderByDescending(x => RiskRank(x.RiskLevel))
      .ThenByDescending(x => x.OutstandingAmount)
      .ToList();

    var totalExposure = bySupplier.Sum(x => x.OutstandingAmount);
    var totalSuppliers = bySupplier.Count;

    var segments = new[] { "low", "medium", "high" }
      .Select(key =>
      {
        var bucket = bySupplier.Where(x => string.Equals(x.RiskLevel, key, StringComparison.OrdinalIgnoreCase)).ToList();
        var amount = bucket.Sum(x => x.OutstandingAmount);
        var percent = totalExposure <= 0 || totalSuppliers == 0
          ? 0m
          : Math.Round(bucket.Count * 100m / totalSuppliers, 0);
        return new ApDashboardRiskSegmentDto(
          key,
          key switch
          {
            "low" => "Low",
            "medium" => "Medium",
            _ => "High"
          },
          bucket.Count,
          amount,
          FormatMoney(amount),
          percent);
      })
      .ToList();

    return new ApDashboardSupplierRiskRadarDto(
      "Supplier Risk Radar",
      "Which vendors carry the most risk exposure?",
      totalSuppliers,
      totalExposure,
      FormatMoney(totalExposure),
      segments,
      bySupplier.Where(x => !string.Equals(x.RiskLevel, "low", StringComparison.OrdinalIgnoreCase)).Take(10).ToList());
  }

  private static string ResolveSupplierRiskLevel(
    decimal outstanding,
    int overdueCount,
    int openCount,
    int highInvoiceCount,
    int mediumInvoiceCount)
  {
    var score = 0;
    if (overdueCount > 0)
      score += Math.Min(3, overdueCount);
    if (highInvoiceCount > 0)
      score += 2;
    if (mediumInvoiceCount > 0)
      score += 1;
    if (outstanding >= 100_000m)
      score += 2;
    else if (outstanding >= 25_000m)
      score += 1;
    if (openCount >= 5)
      score += 1;

    if (score >= 4)
      return "high";
    if (score >= 2)
      return "medium";
    return "low";
  }

  private static int RiskRank(string risk) => risk.ToLowerInvariant() switch
  {
    "high" => 3,
    "medium" => 2,
    _ => 1
  };

  private static IReadOnlyList<ApDashboardKpiDto> BuildKpis(
    ApDashboardPeriod period,
    IReadOnlyList<ApDashboardInvoiceDto> current,
    IReadOnlyList<ApDashboardInvoiceDto> previous)
  {
    var vsLabel = ComparisonPeriodSuffix(period);
    return
    [
      BuildKpi("total_outstanding", "Total Outstanding", vsLabel, current, previous,
        i => !IsPaid(i.PaymentStatus), i => i.Amount),
      BuildKpi("total_paid", "Total Paid", vsLabel, current, previous,
        i => IsPaid(i.PaymentStatus), i => i.Amount),
      BuildKpi("pending_payments", "Pending Payments", vsLabel, current, previous,
        i => string.Equals(i.PaymentStatus, "pending", StringComparison.OrdinalIgnoreCase), i => i.Amount),
      BuildKpi("due_today", "Due Today", vsLabel, current, previous,
        i => string.Equals(i.PaymentStatus, "due_today", StringComparison.OrdinalIgnoreCase), i => i.Amount),
      BuildKpi("overdue_amount", "Overdue", vsLabel, current, previous,
        i => string.Equals(i.PaymentStatus, "overdue", StringComparison.OrdinalIgnoreCase), i => i.Amount),
      BuildAvgProcessingKpi(vsLabel, current, previous)
    ];
  }

  private static ApDashboardKpiDto BuildKpi(
    string key,
    string label,
    string vsLabel,
    IReadOnlyList<ApDashboardInvoiceDto> current,
    IReadOnlyList<ApDashboardInvoiceDto> previous,
    Func<ApDashboardInvoiceDto, bool> filter,
    Func<ApDashboardInvoiceDto, decimal> selector)
  {
    var value = current.Where(filter).Sum(selector);
    var prev = previous.Where(filter).Sum(selector);
    var change = ComputeChangePercent(value, prev);
    var invertGood = key is "overdue_amount" or "pending_payments" or "total_outstanding";
    var trend = TrendFromChange(change, value, prev, invertGood);
    var (changeDirection, changeLabel, periodLabel, fullLabel) =
      BuildComparisonParts(change, value, prev, vsLabel);
    return new ApDashboardKpiDto(
      key,
      label,
      FormatMoney(value),
      value,
      change,
      trend,
      fullLabel,
      prev,
      changeDirection,
      changeLabel,
      periodLabel);
  }

  private static ApDashboardKpiDto BuildAvgProcessingKpi(
    string vsLabel,
    IReadOnlyList<ApDashboardInvoiceDto> current,
    IReadOnlyList<ApDashboardInvoiceDto> previous)
  {
    var currentDays = current
      .Where(i => i.ProcessingDays is > 0)
      .Select(i => i.ProcessingDays!.Value)
      .ToList();
    var previousDays = previous
      .Where(i => i.ProcessingDays is > 0)
      .Select(i => i.ProcessingDays!.Value)
      .ToList();

    var value = currentDays.Count == 0 ? 0m : (decimal)currentDays.Average();
    var prev = previousDays.Count == 0 ? 0m : (decimal)previousDays.Average();
    var change = ComputeChangePercent(value, prev);
    var trend = TrendFromChange(change, value, prev, invertGood: true);
    var (changeDirection, changeLabel, periodLabel, fullLabel) =
      BuildComparisonParts(change, value, prev, vsLabel);
    return new ApDashboardKpiDto(
      "avg_processing_time",
      "Avg. Processing Time",
      $"{value:0.0} d",
      value,
      change,
      trend,
      fullLabel,
      prev,
      changeDirection,
      changeLabel,
      periodLabel);
  }

  private static ApDashboardSeriesDto BuildProfitVsAp(IReadOnlyList<ApDashboardInvoiceDto> invoices)
  {
    var points = invoices
      .Where(i => i.InvoiceDate.HasValue)
      .GroupBy(i => new { i.InvoiceDate!.Value.Year, i.InvoiceDate!.Value.Month })
      .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
      .Select(g =>
      {
        var ap = g.Sum(x => x.Amount);
        var matched = g.Count(x => IsMatched(x.MatchedStatus));
        var matchRate = g.Count() == 0 ? 0m : Math.Round(matched * 100m / g.Count(), 1);
        return new ApDashboardSeriesPointDto(
          FormatMonthYearLabel(g.Key.Year, g.Key.Month),
          ap,
          matchRate,
          "currency",
          "percent");
      })
      .ToList();

    return new ApDashboardSeriesDto(
      "Profit vs AP spending",
      "Dual axis: AP amount and match-rate % (proxy for profit efficiency)",
      points);
  }

  private static ApDashboardSeriesDto BuildMonthlyPaymentTrend(IReadOnlyList<ApDashboardInvoiceDto> invoices)
  {
    var points = invoices
      .Where(i => i.InvoiceDate.HasValue)
      .GroupBy(i => new { i.InvoiceDate!.Value.Year, i.InvoiceDate!.Value.Month })
      .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
      .Select(g => new ApDashboardSeriesPointDto(
        FormatMonthYearLabel(g.Key.Year, g.Key.Month),
        g.Where(x => IsPaid(x.PaymentStatus)).Sum(x => x.Amount),
        null,
        "currency",
        null))
      .ToList();

    return new ApDashboardSeriesDto(
      "Monthly payment trend",
      "Cash leaving the building, month by month",
      points);
  }

  /// <summary>Month label with year so Jul 2025 and Jul 2026 do not collide on charts.</summary>
  private static string FormatMonthYearLabel(int year, int month) =>
    new DateTime(year, month, 1).ToString("MMM yyyy", CultureInfo.InvariantCulture);

  private static ApDashboardSeriesDto BuildCashFlowForecast(
    IReadOnlyList<ApDashboardInvoiceDto> invoices,
    DateTime rangeEndUtc)
  {
    var pending = invoices
      .Where(i => !IsPaid(i.PaymentStatus))
      .ToList();

    var points = new List<ApDashboardSeriesPointDto>();
    var weekStart = rangeEndUtc.Date;
    for (var w = 1; w <= 10; w++)
    {
      var weekEnd = weekStart.AddDays(7);
      var dueInWeek = pending
        .Where(i => i.DueDate.HasValue && i.DueDate.Value.Date >= weekStart && i.DueDate.Value.Date < weekEnd)
        .Sum(i => i.Amount);
      points.Add(new ApDashboardSeriesPointDto($"W{w}", dueInWeek, null, "currency", null));
      weekStart = weekEnd;
    }

    return new ApDashboardSeriesDto(
      "Cash flow forecast",
      "Liquidity projection and cash needs over next 10 weeks",
      points);
  }

  private static IReadOnlyList<ApDashboardSupplierAmountDto> BuildTopSuppliers(
    IReadOnlyList<ApDashboardInvoiceDto> invoices,
    bool outstandingOnly)
  {
    var filtered = outstandingOnly
      ? invoices.Where(i => !IsPaid(i.PaymentStatus))
      : invoices.AsEnumerable();

    return filtered
      .GroupBy(i => string.IsNullOrWhiteSpace(i.Supplier) ? "Unknown" : i.Supplier.Trim(), StringComparer.OrdinalIgnoreCase)
      .Select(g => new ApDashboardSupplierAmountDto(
        g.Key,
        g.Sum(x => x.Amount),
        g.Select(x => x.Currency).FirstOrDefault() ?? "USD"))
      .OrderByDescending(x => x.Amount)
      .Take(10)
      .ToList();
  }

  private static IReadOnlyList<ApDashboardDepartmentSpendDto> BuildDepartmentSpend(
    IReadOnlyList<ApDashboardInvoiceDto> invoices)
  {
    var total = invoices.Sum(i => i.Amount);
    if (total <= 0)
      return [];

    return invoices
      .GroupBy(i => string.IsNullOrWhiteSpace(i.Department) ? "General" : i.Department.Trim(), StringComparer.OrdinalIgnoreCase)
      .Select(g =>
      {
        var amount = g.Sum(x => x.Amount);
        return new ApDashboardDepartmentSpendDto(
          g.Key,
          amount,
          Math.Round(amount * 100m / total, 0),
          g.Select(x => x.Currency).FirstOrDefault() ?? "USD");
      })
      .OrderByDescending(x => x.Amount)
      .ToList();
  }

  private static IReadOnlyList<ApDashboardGeographyDto> BuildGeography(
    IReadOnlyList<ApDashboardInvoiceDto> invoices)
  {
    var total = invoices.Sum(i => i.Amount);
    if (total <= 0)
      return [];

    return invoices
      .GroupBy(i => NormalizeCountry(i.CountryCode), StringComparer.OrdinalIgnoreCase)
      .Select(g =>
      {
        var amount = g.Sum(x => x.Amount);
        var suppliers = g.Select(x => x.Supplier).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return new ApDashboardGeographyDto(
          g.Key,
          CountryName(g.Key),
          amount,
          suppliers,
          Math.Round(amount * 100m / total, 0),
          g.Select(x => x.Currency).FirstOrDefault() ?? "USD");
      })
      .OrderByDescending(x => x.Amount)
      .ToList();
  }

  private static ApDashboardPanelSectionDto BuildProfitabilityCashPosition(
    IReadOnlyList<ApDashboardInvoiceDto> invoices,
    DateTime asOfUtc)
  {
    var outstanding = invoices.Where(i => !IsPaid(i.PaymentStatus)).ToList();
    var totalAp = outstanding.Sum(i => i.Amount);
    var overdue = outstanding
      .Where(i => string.Equals(i.PaymentStatus, "overdue", StringComparison.OrdinalIgnoreCase))
      .Sum(i => i.Amount);
    var matchShare = invoices.Count == 0
      ? 0m
      : invoices.Count(i => IsMatched(i.MatchedStatus)) / (decimal)invoices.Count;
    var overdueShare = totalAp <= 0 ? 0m : overdue / totalAp;
    // Margin proxy from match quality eroded by overdue share (no GL P&amp;L in AP tables).
    var profitMargin = Math.Round(Math.Clamp((1m - overdueShare) * matchShare * 25m, 0m, 40m), 1);

    var weekAmounts = BuildNextWeekAmounts(outstanding, asOfUtc, weekCount: 4);
    var next4Weeks = weekAmounts.Sum();
    var peakWeekIndex = 1;
    var peakAmount = 0m;
    for (var i = 0; i < weekAmounts.Count; i++)
    {
      if (weekAmounts[i] < peakAmount)
        continue;
      peakAmount = weekAmounts[i];
      peakWeekIndex = i + 1;
    }

    return new ApDashboardPanelSectionDto(
      "profitability_cash_position",
      "Profitability & Cash Position",
      "Payables growth eating margin · future liquidity needs",
      [
        new ApDashboardKpiDto(
          "profit_margin",
          "Profit Margin",
          $"{profitMargin.ToString("0.0", CultureInfo.InvariantCulture)}%",
          profitMargin,
          null,
          null),
        new ApDashboardKpiDto(
          "next_4_weeks_forecast",
          "Next 4 Weeks",
          FormatMoney(next4Weeks),
          next4Weeks,
          null,
          null),
        new ApDashboardKpiDto(
          "peak_week",
          "Peak Week",
          $"Week {peakWeekIndex}",
          peakAmount,
          null,
          null,
          ComparisonLabel: peakAmount > 0 ? FormatMoney(peakAmount) : null)
      ]);
  }

  private static ApDashboardPanelSectionDto BuildSupplierConcentrationRisk(
    IReadOnlyList<ApDashboardInvoiceDto> invoices,
    ApDashboardSupplierRiskRadarDto riskRadar)
  {
    var outstanding = invoices.Where(i => !IsPaid(i.PaymentStatus)).ToList();
    var bySupplier = outstanding
      .GroupBy(i => string.IsNullOrWhiteSpace(i.Supplier) ? "Unknown" : i.Supplier.Trim(), StringComparer.OrdinalIgnoreCase)
      .Select(g => g.Sum(x => x.Amount))
      .OrderByDescending(a => a)
      .ToList();

    var total = bySupplier.Sum();
    var top3 = bySupplier.Take(3).Sum();
    var top3Pct = total <= 0 ? 0m : Math.Round(top3 * 100m / total, 1);
    var activeSuppliers = riskRadar.TotalSuppliers;
    var highRisk = riskRadar.Segments
      .FirstOrDefault(s => string.Equals(s.Key, "high", StringComparison.OrdinalIgnoreCase))
      ?.SupplierCount ?? 0;

    return new ApDashboardPanelSectionDto(
      "supplier_concentration_risk",
      "Supplier Concentration & Risk",
      "Where spend concentrates · vendor risk exposure",
      [
        new ApDashboardKpiDto(
          "active_suppliers",
          "Active Suppliers",
          activeSuppliers.ToString(CultureInfo.InvariantCulture),
          activeSuppliers,
          null,
          null),
        new ApDashboardKpiDto(
          "high_risk_suppliers",
          "High Risk",
          highRisk.ToString(CultureInfo.InvariantCulture),
          highRisk,
          null,
          highRisk > 0 ? "down" : "flat"),
        new ApDashboardKpiDto(
          "top3_concentration",
          "Top-3 Concentration",
          $"{top3Pct.ToString("0.0", CultureInfo.InvariantCulture)}%",
          top3Pct,
          null,
          null)
      ]);
  }

  private static ApDashboardPanelSectionDto BuildAgingProcessOversight(
    IReadOnlyList<ApDashboardInvoiceDto> invoices,
    DateTime asOfUtc)
  {
    var outstanding = invoices.Where(i => !IsPaid(i.PaymentStatus)).ToList();
    var aging90 = outstanding
      .Where(i => DaysPastDue(i, asOfUtc) >= 90)
      .Sum(i => i.Amount);

    var criticalExceptions = outstanding.Count(i =>
      string.Equals(i.RiskLevel, "high", StringComparison.OrdinalIgnoreCase)
      || string.Equals(i.ApprovalStatus, "rejected", StringComparison.OrdinalIgnoreCase)
      || string.Equals(i.ApprovalStatus, "hold", StringComparison.OrdinalIgnoreCase)
      || (!string.IsNullOrWhiteSpace(i.MatchedStatus)
          && (i.MatchedStatus.Contains("unmatch", StringComparison.OrdinalIgnoreCase)
              || i.MatchedStatus.Contains("partial", StringComparison.OrdinalIgnoreCase)
              || i.MatchedStatus.Contains("exception", StringComparison.OrdinalIgnoreCase))));

    var decided = invoices.Where(i =>
      string.Equals(i.ApprovalStatus, "approved", StringComparison.OrdinalIgnoreCase)
      || string.Equals(i.ApprovalStatus, "partially_approved", StringComparison.OrdinalIgnoreCase)
      || string.Equals(i.ApprovalStatus, "rejected", StringComparison.OrdinalIgnoreCase)
      || string.Equals(i.ApprovalStatus, "paid", StringComparison.OrdinalIgnoreCase)).ToList();
    var approved = decided.Count(i =>
      string.Equals(i.ApprovalStatus, "approved", StringComparison.OrdinalIgnoreCase)
      || string.Equals(i.ApprovalStatus, "partially_approved", StringComparison.OrdinalIgnoreCase)
      || string.Equals(i.ApprovalStatus, "paid", StringComparison.OrdinalIgnoreCase));
    var approvalRate = decided.Count == 0
      ? 0m
      : Math.Round(approved * 100m / decided.Count, 1);

    return new ApDashboardPanelSectionDto(
      "aging_process_oversight",
      "Aging & Process Oversight",
      "Portfolio-level view of overdue exposure and approval cycles",
      [
        new ApDashboardKpiDto(
          "aging_90_plus",
          "90+ Days",
          FormatMoney(aging90),
          aging90,
          null,
          aging90 > 0 ? "down" : "flat"),
        new ApDashboardKpiDto(
          "critical_exceptions",
          "Critical Exceptions",
          criticalExceptions.ToString(CultureInfo.InvariantCulture),
          criticalExceptions,
          null,
          criticalExceptions > 0 ? "down" : "flat"),
        new ApDashboardKpiDto(
          "approval_rate",
          "Approval Rate",
          $"{approvalRate.ToString("0.0", CultureInfo.InvariantCulture)}%",
          approvalRate,
          null,
          null)
      ]);
  }

  private static ApDashboardAgingAnalysisDto BuildInvoiceAgingAnalysis(
    IReadOnlyList<ApDashboardInvoiceDto> invoices,
    DateTime asOfUtc)
  {
    var outstanding = invoices.Where(i => !IsPaid(i.PaymentStatus)).ToList();
    var definitions = new (string Key, string Label, Func<int, bool> Match)[]
    {
      ("current", "Current", d => d <= 0),
      ("1_30", "1–30 Days", d => d is >= 1 and <= 30),
      ("31_60", "31–60 Days", d => d is >= 31 and <= 60),
      ("61_90", "61–90 Days", d => d is >= 61 and <= 90),
      ("90_plus", "90+ Days", d => d >= 91)
    };

    var total = outstanding.Sum(i => i.Amount);
    var buckets = definitions
      .Select(def =>
      {
        var rows = outstanding.Where(i => def.Match(DaysPastDue(i, asOfUtc))).ToList();
        var amount = rows.Sum(x => x.Amount);
        return new ApDashboardAgingBucketDto(
          def.Key,
          def.Label,
          amount,
          FormatMoney(amount),
          rows.Count,
          total <= 0 ? 0m : Math.Round(amount * 100m / total, 1));
      })
      .ToList();

    return new ApDashboardAgingAnalysisDto(
      "Invoice aging analysis",
      "Outstanding AP by days past due",
      total,
      FormatMoney(total),
      buckets);
  }

  private static List<decimal> BuildNextWeekAmounts(
    IReadOnlyList<ApDashboardInvoiceDto> outstanding,
    DateTime asOfUtc,
    int weekCount)
  {
    var amounts = new List<decimal>(weekCount);
    var weekStart = asOfUtc.Date;
    for (var w = 0; w < weekCount; w++)
    {
      var weekEnd = weekStart.AddDays(7);
      amounts.Add(outstanding
        .Where(i => i.DueDate.HasValue && i.DueDate.Value.Date >= weekStart && i.DueDate.Value.Date < weekEnd)
        .Sum(i => i.Amount));
      weekStart = weekEnd;
    }

    return amounts;
  }

  private static int DaysPastDue(ApDashboardInvoiceDto invoice, DateTime asOfUtc)
  {
    if (!invoice.DueDate.HasValue)
      return 0;

    var days = (asOfUtc.Date - invoice.DueDate.Value.Date).TotalDays;
    return days <= 0 ? 0 : (int)Math.Floor(days);
  }

  private static string BuildPeriodLabel(ApDashboardPeriod period, DateTime start, DateTime end) =>
    period switch
    {
      ApDashboardPeriod.Today => start.ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
      ApDashboardPeriod.Tomorrow => start.ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
      ApDashboardPeriod.ThisWeek => $"{start:dd MMM} – {end:dd MMM yyyy}",
      ApDashboardPeriod.ThisMonth => start.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
      ApDashboardPeriod.LastMonth => start.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
      ApDashboardPeriod.ThisQuarter => $"Q{((start.Month - 1) / 3) + 1} {start.Year}",
      ApDashboardPeriod.ThisYear => start.Year.ToString(CultureInfo.InvariantCulture),
      _ => $"{start:yyyy-MM-dd} – {end:yyyy-MM-dd}"
    };

  private static decimal? ComputeChangePercent(decimal current, decimal previous)
  {
    // Both empty → 0% (flat).
    if (previous == 0 && current == 0)
      return 0;

    // No baseline in previous period but activity now → treat as full increase.
    if (previous == 0)
      return 100m;

    return Math.Round((current - previous) * 100m / previous, 1);
  }

  private static string TrendFromChange(
    decimal? change,
    decimal current,
    decimal previous,
    bool invertGood)
  {
    if (change is null)
      return current > previous
        ? (invertGood ? "down" : "up")
        : current < previous
          ? (invertGood ? "up" : "down")
          : "flat";

    if (change == 0)
      return "flat";

    var up = change > 0;
    if (invertGood)
      up = !up;
    return up ? "up" : "down";
  }

  private static (string ChangeDirection, string ChangeLabel, string PeriodLabel, string FullLabel)
    BuildComparisonParts(
      decimal? changePercent,
      decimal current,
      decimal previous,
      string vsLabel)
  {
    var periodLabel = vsLabel;

    if ((previous == 0 && current == 0) || changePercent is null or 0)
    {
      const string flat = "Flat";
      return ("flat", flat, periodLabel, $"{flat} {periodLabel}");
    }

    var direction = changePercent > 0 ? "up" : "down";
    var pct = Math.Abs(changePercent.Value).ToString("0.#", CultureInfo.InvariantCulture);
    var changeLabel = $"{pct}% {direction}";
    return (direction, changeLabel, periodLabel, $"{changeLabel} {periodLabel}");
  }

  private static string ComparisonPeriodSuffix(ApDashboardPeriod period) =>
    period switch
    {
      ApDashboardPeriod.Today => "vs yesterday",
      ApDashboardPeriod.Tomorrow => "vs today",
      ApDashboardPeriod.ThisWeek => "vs last week",
      ApDashboardPeriod.LastMonth => "vs prior month",
      ApDashboardPeriod.ThisQuarter => "vs last quarter",
      ApDashboardPeriod.ThisYear => "vs last year",
      ApDashboardPeriod.Custom => "vs prior period",
      _ => "vs last month"
    };

  private static bool IsPaid(string status) =>
    string.Equals(status, "paid", StringComparison.OrdinalIgnoreCase)
    || string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase);

  private static bool IsMatched(string? status) =>
    !string.IsNullOrWhiteSpace(status)
    && (status.Contains("match", StringComparison.OrdinalIgnoreCase)
        || status.Contains("approv", StringComparison.OrdinalIgnoreCase));

  private static string FormatMoney(decimal value) =>
    value >= 1_000_000 ? $"${value / 1_000_000m:0.0}M"
    : value >= 1_000 ? $"${value / 1_000m:0.0}K"
    : $"${value:0}";

  private static string NormalizeCountry(string? code) =>
    string.IsNullOrWhiteSpace(code) ? "UN" : code.Trim().ToUpperInvariant();

  private static string CountryName(string code) => ApCountryCatalog.DisplayName(code);
}
