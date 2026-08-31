using System.Text.Json;
using System.Text.Json.Serialization;

namespace SaaSApp.Workflow.Application.Contracts;

/// <summary>AP Command Center dashboard filters (matches UI filter bar).</summary>
public sealed record ApDashboardRequest(
  Guid? WorkflowId = null,
  ApDashboardPeriod Period = ApDashboardPeriod.ThisMonth,
  DateTime? FromUtc = null,
  DateTime? ToUtc = null,
  /// <summary>Department / spend category (e.g. MRO, IT Services). Use "all" to clear.</summary>
  string? Department = null,
  /// <summary>Supplier name search (partial match).</summary>
  string? Supplier = null,
  /// <summary>Approval status: all, approved, partially_approved, rejected, paid, processing, hold, overdue, due_today, pending.</summary>
  string? Status = null,
  /// <summary>Currency code (USD, EUR, INR, GBP). Use "all" to clear.</summary>
  string? Currency = null,
  /// <summary>Workflow request status: all, pending, processing, completed, hold, rejected.</summary>
  string? RequestStatus = null,
  /// <summary>PO / invoice amount tier: all, high_value (&gt; $100K), low_value (&lt; $1K).</summary>
  string? PoAmountTier = null,
  bool IncludeInvoiceDetails = false);

public sealed record ApDashboardFilterOptionDto(string Key, string Label);

public sealed record ApDashboardFilterOptionsDto(
  IReadOnlyList<string> Departments,
  IReadOnlyList<string> Suppliers,
  IReadOnlyList<string> Currencies,
  IReadOnlyList<ApDashboardFilterOptionDto> ApprovalStatuses,
  IReadOnlyList<ApDashboardFilterOptionDto> RequestStatuses,
  IReadOnlyList<ApDashboardFilterOptionDto> PoAmountTiers);

public sealed record ApDashboardActiveFiltersDto(
  ApDashboardPeriod Period,
  string? Department,
  string? Supplier,
  string? Status,
  string? Currency,
  string? RequestStatus,
  string? PoAmountTier,
  Guid? WorkflowId);

[JsonConverter(typeof(ApDashboardPeriodJsonConverter))]
public enum ApDashboardPeriod
{
  ThisMonth,
  LastMonth,
  ThisQuarter,
  ThisYear,
  Custom,
  /// <summary>Calendar today (UTC).</summary>
  Today,
  /// <summary>Calendar tomorrow (UTC).</summary>
  Tomorrow,
  /// <summary>Current calendar week Monday–Sunday (UTC).</summary>
  ThisWeek
}

/// <summary>Serialize/deserialize AP dashboard period as camelCase strings (thisMonth) without affecting other API enums.</summary>
public sealed class ApDashboardPeriodJsonConverter : JsonStringEnumConverter
{
  public ApDashboardPeriodJsonConverter()
    : base(JsonNamingPolicy.CamelCase, allowIntegerValues: true)
  {
  }
}

public sealed record ApDashboardResult(
  ApDashboardPeriod Period,
  string PeriodLabel,
  DateTime RangeStartUtc,
  DateTime RangeEndUtc,
  /// <summary>AP Command Center summary strip (TOTAL AP, OVERDUE, OPEN INVOICES, DPO).</summary>
  ApDashboardHeaderDto Header,
  IReadOnlyList<ApDashboardKpiDto> Kpis,
  /// <summary>Supplier Risk Radar donut (Low / Medium / High).</summary>
  ApDashboardSupplierRiskRadarDto SupplierRiskRadar,
  ApDashboardSeriesDto ProfitVsApSpending,
  ApDashboardSeriesDto MonthlyPaymentTrend,
  ApDashboardSeriesDto CashFlowForecast,
  IReadOnlyList<ApDashboardSupplierAmountDto> TopSuppliersByInvoice,
  IReadOnlyList<ApDashboardSupplierAmountDto> OutstandingBySupplier,
  IReadOnlyList<ApDashboardDepartmentSpendDto> DepartmentSpend,
  IReadOnlyList<ApDashboardGeographyDto> SupplierGeography,
  ApDashboardFilterOptionsDto FilterOptions,
  ApDashboardActiveFiltersDto ActiveFilters,
  /// <summary>Profitability &amp; Cash Position accordion (profit margin, next 4 weeks, peak week).</summary>
  ApDashboardPanelSectionDto ProfitabilityCashPosition,
  /// <summary>Supplier Concentration &amp; Risk accordion (active, high risk, top-3 concentration).</summary>
  ApDashboardPanelSectionDto SupplierConcentrationRisk,
  /// <summary>Aging &amp; Process Oversight accordion (90+ days, critical exceptions, approval rate).</summary>
  ApDashboardPanelSectionDto AgingProcessOversight,
  /// <summary>Invoice aging analysis chart buckets (current / 1–30 / 31–60 / 61–90 / 90+).</summary>
  ApDashboardAgingAnalysisDto InvoiceAgingAnalysis,
  IReadOnlyList<ApDashboardInvoiceDto>? Invoices = null,
  /// <summary>AI insights from agents <c>/chat</c> with <c>intent=insight</c> (attached after dashboard build).</summary>
  IReadOnlyList<string>? Insights = null);

/// <summary>Collapsible dashboard panel with title, subtitle, and KPI cards.</summary>
public sealed record ApDashboardPanelSectionDto(
  string Key,
  string Title,
  string Subtitle,
  IReadOnlyList<ApDashboardKpiDto> Kpis);

/// <summary>Invoice aging analysis chart.</summary>
public sealed record ApDashboardAgingAnalysisDto(
  string Title,
  string? Subtitle,
  decimal TotalAmount,
  string TotalAmountDisplay,
  IReadOnlyList<ApDashboardAgingBucketDto> Buckets);

public sealed record ApDashboardAgingBucketDto(
  string Key,
  string Label,
  decimal Amount,
  string AmountDisplay,
  int InvoiceCount,
  decimal Percent);

/// <summary>AP Command Center header strip.</summary>
public sealed record ApDashboardHeaderDto(
  decimal TotalAp,
  string TotalApDisplay,
  decimal Overdue,
  string OverdueDisplay,
  int OpenInvoices,
  decimal DpoDays,
  string DpoDisplay,
  string ContextLabel);

/// <summary>Supplier Risk Radar donut chart.</summary>
public sealed record ApDashboardSupplierRiskRadarDto(
  string Title,
  string Subtitle,
  int TotalSuppliers,
  decimal TotalExposure,
  string TotalExposureDisplay,
  IReadOnlyList<ApDashboardRiskSegmentDto> Segments,
  IReadOnlyList<ApDashboardSupplierRiskDto> TopRiskSuppliers);

public sealed record ApDashboardRiskSegmentDto(
  string Key,
  string Label,
  int SupplierCount,
  decimal Amount,
  string AmountDisplay,
  decimal Percent);

public sealed record ApDashboardSupplierRiskDto(
  string Supplier,
  string RiskLevel,
  decimal OutstandingAmount,
  string OutstandingDisplay,
  int OpenInvoices,
  int OverdueInvoices,
  string? CountryCode,
  string Currency);

public sealed record ApDashboardKpiDto(
  string Key,
  string Label,
  string DisplayValue,
  decimal Value,
  decimal? ChangePercent,
  /// <summary>Semantic arrow for UI color: up | down | flat (inverted for “bad” KPIs like overdue).</summary>
  string? Trend,
  /// <summary>Full footer, e.g. <c>36% up vs last month</c>, <c>48% down vs last month</c>, <c>Flat vs last month</c>.</summary>
  string? ComparisonLabel = null,
  /// <summary>Previous-period raw value used for the change (for UI tooltips).</summary>
  decimal? PreviousValue = null,
  /// <summary>Numeric MoM direction only: <c>up</c> | <c>down</c> | <c>flat</c> (not inverted).</summary>
  string? ChangeDirection = null,
  /// <summary>Change part only, e.g. <c>36% up</c>, <c>48% down</c>, <c>Flat</c>.</summary>
  string? ComparisonChangeLabel = null,
  /// <summary>Period part only, e.g. <c>vs last month</c>.</summary>
  string? ComparisonPeriodLabel = null);

public sealed record ApDashboardSeriesDto(
  string Title,
  string? Subtitle,
  IReadOnlyList<ApDashboardSeriesPointDto> Points);

public sealed record ApDashboardSeriesPointDto(
  string Label,
  decimal? Primary,
  decimal? Secondary,
  string? PrimaryUnit = null,
  string? SecondaryUnit = null);

public sealed record ApDashboardSupplierAmountDto(
  string Supplier,
  decimal Amount,
  string Currency);

public sealed record ApDashboardDepartmentSpendDto(
  string Department,
  decimal Amount,
  decimal Percent,
  string Currency);

public sealed record ApDashboardGeographyDto(
  string CountryCode,
  string Country,
  decimal Amount,
  int SupplierCount,
  decimal Percent,
  string Currency);

public sealed record ApDashboardInvoiceDto(
  Guid WorkflowId,
  string? WorkflowName,
  Guid InstanceId,
  string? ReferenceNumber,
  string Supplier,
  decimal Amount,
  string Currency,
  DateTime? InvoiceDate,
  DateTime? DueDate,
  string? Department,
  string? CountryCode,
  string PaymentStatus,
  string ApprovalStatus,
  string RequestStatus,
  string? MatchedStatus,
  /// <summary>Per-invoice risk: low, medium, high.</summary>
  string RiskLevel,
  DateTime? CreatedAtUtc,
  decimal? ProcessingDays);
