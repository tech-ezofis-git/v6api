using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaaSApp.Security;
using SaaSApp.Workflow.Application.Contracts;
using WorkflowTenantContext = SaaSApp.Workflow.Application.Contracts.ITenantContext;

namespace SaaSApp.Api.Controllers;

/// <summary>
/// AP Command Center dashboard — KPIs and charts from workflow agentDataValidation + ezfb form data.
/// </summary>
[ApiController]
[Route("api/reports/ap-dashboard")]
[Authorize(Policy = AuthorizationPolicies.TenantUser)]
public sealed class ApDashboardController : ControllerBase
{
  private readonly IApDashboardQueryService _dashboard;
  private readonly WorkflowTenantContext _tenantContext;

  public ApDashboardController(IApDashboardQueryService dashboard, WorkflowTenantContext tenantContext)
  {
    _dashboard = dashboard;
    _tenantContext = tenantContext;
  }

  /// <summary>
  /// AP dashboard report for the current tenant.
  /// </summary>
  /// <remarks>
  /// UI filters supported:
  /// <list type="bullet">
  /// <item><c>period</c> — today, tomorrow, thisWeek, thisMonth, lastMonth, thisQuarter, thisYear, custom</item>
  /// <item><c>department</c> — MRO, IT Services, etc. (spend category)</item>
  /// <item><c>supplier</c> — supplier name search</item>
  /// <item><c>status</c> — approved, partially_approved, rejected, paid, processing, hold, overdue, due_today, pending, all</item>
  /// <item><c>currency</c> — USD, EUR, INR, GBP, all</item>
  /// <item><c>requestStatus</c> — pending, processing, completed, hold, rejected, all</item>
  /// <item><c>poAmountTier</c> — high_value (&gt; $100K), low_value (&lt; $1K), all</item>
  /// </list>
  /// Response includes:
  /// <list type="bullet">
  /// <item><c>header</c> — AP Command Center (TOTAL AP, OVERDUE, OPEN INVOICES, DPO)</item>
  /// <item><c>supplierRiskRadar</c> — Low / Medium / High risk donut + top risk suppliers</item>
  /// <item><c>filterOptions</c> / <c>activeFilters</c> — dropdown values and applied chips</item>
  /// <item><c>profitabilityCashPosition</c> — Profit Margin, Next 4 Weeks, Peak Week</item>
  /// <item><c>supplierConcentrationRisk</c> — Active Suppliers, High Risk, Top-3 Concentration</item>
  /// <item><c>agingProcessOversight</c> — 90+ Days, Critical Exceptions, Approval Rate</item>
  /// <item><c>invoiceAgingAnalysis</c> — aging buckets for the Invoice aging analysis chart</item>
  /// <item><c>insights</c> — AI insight strings from Python <c>/api/v1/insights</c></item>
  /// </list>
  /// </remarks>
  [HttpPost]
  [ProducesResponseType(typeof(ApDashboardResult), StatusCodes.Status200OK)]
  public async Task<IActionResult> GetDashboard(
    [FromBody] ApDashboardRequest? request,
    CancellationToken cancellationToken = default)
  {
    request ??= new ApDashboardRequest();
    var tenantId = _tenantContext.TenantId ?? throw new InvalidOperationException("Tenant context is required.");
    var result = await _dashboard.GetDashboardAsync(tenantId, request, cancellationToken);
    return Ok(result);
  }

  /// <summary>Convenience GET with query-string filters (same payload as POST).</summary>
  [HttpGet]
  [ProducesResponseType(typeof(ApDashboardResult), StatusCodes.Status200OK)]
  public Task<IActionResult> GetDashboardQuery(
    [FromQuery] Guid? workflowId,
    [FromQuery] ApDashboardPeriod period = ApDashboardPeriod.ThisMonth,
    [FromQuery] DateTime? fromUtc = null,
    [FromQuery] DateTime? toUtc = null,
    [FromQuery] string? department = null,
    [FromQuery] string? supplier = null,
    [FromQuery] string? status = null,
    [FromQuery] string? currency = null,
    [FromQuery] string? requestStatus = null,
    [FromQuery] string? poAmountTier = null,
    [FromQuery] bool includeInvoiceDetails = false,
    CancellationToken cancellationToken = default) =>
    GetDashboard(
      new ApDashboardRequest(
        workflowId,
        period,
        fromUtc,
        toUtc,
        department,
        supplier,
        status,
        currency,
        requestStatus,
        poAmountTier,
        includeInvoiceDetails),
      cancellationToken);
}
