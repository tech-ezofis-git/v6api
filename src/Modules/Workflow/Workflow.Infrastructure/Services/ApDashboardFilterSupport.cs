using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Infrastructure.Services;

internal static class ApDashboardFilterSupport
{
  private const decimal HighValueThreshold = 100_000m;
  private const decimal LowValueThreshold = 1_000m;

  public static readonly IReadOnlyList<ApDashboardFilterOptionDto> ApprovalStatusOptions =
  [
    new("all", "All Statuses"),
    new("approved", "Approved"),
    new("partially_approved", "Partially Approved"),
    new("rejected", "Rejected"),
    new("paid", "Paid"),
    new("processing", "Processing"),
    new("hold", "Hold"),
    new("overdue", "Overdue"),
    new("due_today", "Due Today"),
    new("pending", "Pending")
  ];

  public static readonly IReadOnlyList<ApDashboardFilterOptionDto> RequestStatusOptions =
  [
    new("all", "All Request Statuses"),
    new("pending", "Pending"),
    new("processing", "Processing"),
    new("completed", "Completed"),
    new("hold", "On Hold"),
    new("rejected", "Rejected")
  ];

  public static readonly IReadOnlyList<ApDashboardFilterOptionDto> PoAmountTierOptions =
  [
    new("all", "All Amounts"),
    new("high_value", "High Value (> $100K)"),
    new("low_value", "Low Value (< $1K)")
  ];

  public static string ResolveApprovalStatus(
    string paymentStatus,
    string? matchedStatus,
    string? review)
  {
    var matched = matchedStatus?.Trim() ?? string.Empty;
    var reviewNorm = review?.Trim() ?? string.Empty;
    var payment = paymentStatus?.Trim() ?? string.Empty;

    if (payment.Equals("paid", StringComparison.OrdinalIgnoreCase))
      return "paid";

    if (ContainsAny(matched, reviewNorm, "reject", "declined", "denied"))
      return "rejected";

    if (ContainsAny(matched, reviewNorm, "partial", "partially"))
      return "partially_approved";

    if (ContainsAny(matched, reviewNorm, "hold", "on hold"))
      return "hold";

    if (payment.Equals("approved", StringComparison.OrdinalIgnoreCase)
        || ContainsAny(matched, reviewNorm, "approv", "verified"))
      return "approved";

    if (payment.Equals("overdue", StringComparison.OrdinalIgnoreCase))
      return "overdue";

    if (payment.Equals("due_today", StringComparison.OrdinalIgnoreCase))
      return "due_today";

    if (payment.Equals("pending", StringComparison.OrdinalIgnoreCase))
      return "processing";

    return "processing";
  }

  public static string ResolveRequestStatus(string? instanceStatus)
  {
    if (string.IsNullOrWhiteSpace(instanceStatus))
      return "pending";

    var status = instanceStatus.Trim();
    if (status.Equals("Completed", StringComparison.OrdinalIgnoreCase))
      return "completed";
    if (status.Equals("Running", StringComparison.OrdinalIgnoreCase))
      return "processing";
    if (status.Equals("Paused", StringComparison.OrdinalIgnoreCase))
      return "hold";
    if (status.Equals("Failed", StringComparison.OrdinalIgnoreCase)
        || status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase))
      return "rejected";
    if (status.Equals("Pending", StringComparison.OrdinalIgnoreCase))
      return "pending";

    return NormalizeKey(status);
  }

  public static List<ApDashboardInvoiceDto> ApplyFilters(
    IEnumerable<ApDashboardInvoiceDto> invoices,
    ApDashboardRequest request)
  {
    IEnumerable<ApDashboardInvoiceDto> q = invoices;

    if (HasFilterValue(request.Department))
    {
      var department = request.Department!.Trim();
      q = q.Where(i => (i.Department ?? string.Empty).Contains(department, StringComparison.OrdinalIgnoreCase));
    }

    if (HasFilterValue(request.Supplier))
    {
      var supplier = request.Supplier!.Trim();
      q = q.Where(i => i.Supplier.Contains(supplier, StringComparison.OrdinalIgnoreCase));
    }

    var invoiceNoFilter = HasFilterValue(request.InvoiceNo)
      ? request.InvoiceNo
      : HasFilterValue(request.InvoiceNumber) ? request.InvoiceNumber : null;
    if (HasFilterValue(invoiceNoFilter))
    {
      var invoiceNo = invoiceNoFilter!.Trim();
      q = q.Where(i =>
        (i.InvoiceNumber ?? string.Empty).Contains(invoiceNo, StringComparison.OrdinalIgnoreCase)
        || (i.ReferenceNumber ?? string.Empty).Contains(invoiceNo, StringComparison.OrdinalIgnoreCase));
    }

    if (HasFilterValue(request.Currency))
    {
      var currency = request.Currency!.Trim();
      q = q.Where(i => i.Currency.Equals(currency, StringComparison.OrdinalIgnoreCase));
    }

    if (HasFilterValue(request.Status))
    {
      var status = NormalizeKey(request.Status!);
      q = q.Where(i => MatchesApprovalStatus(i, status));
    }

    if (HasFilterValue(request.RequestStatus))
    {
      var requestStatus = NormalizeKey(request.RequestStatus!);
      q = q.Where(i => string.Equals(i.RequestStatus, requestStatus, StringComparison.OrdinalIgnoreCase));
    }

    if (HasFilterValue(request.PoAmountTier))
    {
      var tier = NormalizeKey(request.PoAmountTier!);
      q = tier switch
      {
        "high_value" => q.Where(i => i.Amount > HighValueThreshold),
        "low_value" => q.Where(i => i.Amount > 0 && i.Amount < LowValueThreshold),
        _ => q
      };
    }

    return q.ToList();
  }

  public static ApDashboardFilterOptionsDto BuildFilterOptions(IReadOnlyList<ApDashboardInvoiceDto> invoices)
  {
    var departments = invoices
      .Select(i => i.Department?.Trim())
      .Where(d => !string.IsNullOrWhiteSpace(d))
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
      .Select(d => d!)
      .ToList();

    var suppliers = invoices
      .Select(i => i.Supplier?.Trim())
      .Where(s => !string.IsNullOrWhiteSpace(s) && !s.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
      .Select(s => s!)
      .ToList();

    var currencies = invoices
      .Select(i => i.Currency?.Trim())
      .Where(c => !string.IsNullOrWhiteSpace(c))
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
      .Select(c => c!)
      .ToList();

    return new ApDashboardFilterOptionsDto(
      departments,
      suppliers,
      currencies,
      ApprovalStatusOptions,
      RequestStatusOptions,
      PoAmountTierOptions);
  }

  public static ApDashboardActiveFiltersDto BuildActiveFilters(ApDashboardRequest request) =>
    new(
      request.Period,
      NullIfAll(request.Department),
      NullIfAll(request.Supplier),
      NullIfAll(request.Status),
      NullIfAll(request.Currency),
      NullIfAll(request.RequestStatus),
      NullIfAll(request.PoAmountTier),
      request.WorkflowId,
      NullIfAll(HasFilterValue(request.InvoiceNo) ? request.InvoiceNo : request.InvoiceNumber));

  private static bool MatchesApprovalStatus(ApDashboardInvoiceDto invoice, string normalizedStatus) =>
    string.Equals(invoice.ApprovalStatus, normalizedStatus, StringComparison.OrdinalIgnoreCase)
    || string.Equals(invoice.PaymentStatus, normalizedStatus, StringComparison.OrdinalIgnoreCase);

  private static bool ContainsAny(string left, string right, params string[] tokens) =>
    tokens.Any(t =>
      left.Contains(t, StringComparison.OrdinalIgnoreCase)
      || right.Contains(t, StringComparison.OrdinalIgnoreCase));

  private static bool HasFilterValue(string? value)
  {
    if (string.IsNullOrWhiteSpace(value))
      return false;

    var key = NormalizeKey(value);
    return key is not ("all" or "all_statuses" or "all_request_statuses" or "all_amounts");
  }

  private static string? NullIfAll(string? value) =>
    HasFilterValue(value) ? value!.Trim() : null;

  private static string NormalizeKey(string value) =>
    value.Trim()
      .Replace(' ', '_')
      .Replace('-', '_')
      .ToLowerInvariant();
}
