using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaaSApp.Billing.Application.Contracts;
using SaaSApp.Billing.Application.Credits.Commands.UpdateCredit;
using SaaSApp.Billing.Application.Credits.Queries.GetCreditMaster;
using SaaSApp.Billing.Application.Credits.Queries.GetCreditMonthlyBalances;
using SaaSApp.Billing.Application.Credits.Queries.GetCreditUsageDashboard;
using SaaSApp.Security;
using SaaSApp.Users.Application.Contracts;

namespace SaaSApp.Api.Controllers;

/// <summary>Credit master balance updates and usage reporting (catalog dbo.creditMaster).</summary>
[ApiController]
[Route("api/billing/credits")]
[Authorize(Policy = AuthorizationPolicies.TenantUser)]
public sealed class BillingCreditsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ITenantContext _tenantContext;

    public BillingCreditsController(IMediator mediator, ITenantContext tenantContext)
    {
        _mediator = mediator;
        _tenantContext = tenantContext;
    }

    /// <summary>Consume credits and write a creditTransaction ledger row for the current month.</summary>
    [HttpPost("update")]
    [ProducesResponseType(typeof(CreditUpdateResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateCredit([FromBody] CreditUpdateRequest request, CancellationToken cancellationToken)
    {
        var tenantId = _tenantContext.TenantId ?? throw new InvalidOperationException("Tenant context is required.");
        var result = await _mediator.Send(
            new UpdateCreditCommand(tenantId, GetUserId(), request),
            cancellationToken);

        return Ok(new CreditUpdateResponse((int)result.Status, result.Message));
    }

    /// <summary>Current month credit master row for the tenant (defaults to current IST month). Includes calculated monthlyBalance.</summary>
    [HttpGet("master")]
    [ProducesResponseType(typeof(CreditMasterDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCreditMaster(
        [FromQuery] int? allocationMonth,
        [FromQuery] int? allocationYear,
        [FromQuery] string? creditType,
        CancellationToken cancellationToken)
    {
        var tenantId = _tenantContext.TenantId ?? throw new InvalidOperationException("Tenant context is required.");
        var result = await _mediator.Send(
            new GetCreditMasterQuery(tenantId, allocationMonth, allocationYear, creditType),
            cancellationToken);

        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>Monthly credit balances from creditMaster for a year (calculated monthlyBalance per month).</summary>
    [HttpGet("master/monthly")]
    [ProducesResponseType(typeof(IReadOnlyList<CreditMonthlyBalanceDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCreditMonthlyBalances(
        [FromQuery] int? year,
        [FromQuery] string? creditType,
        CancellationToken cancellationToken)
    {
        var tenantId = _tenantContext.TenantId ?? throw new InvalidOperationException("Tenant context is required.");
        var result = await _mediator.Send(
            new GetCreditMonthlyBalancesQuery(tenantId, year, creditType),
            cancellationToken);
        return Ok(result);
    }

    /// <summary>Credit usage dashboard (summary, distribution, overall split pie chart, timeline) for today, yesterday, monthly, quarterly, or yearly.</summary>
    [HttpPost("usage")]
    [ProducesResponseType(typeof(CreditUsageDashboardResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCreditUsage(
        [FromBody] CreditUsageReportRequest request,
        CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantContext.TenantId ?? throw new InvalidOperationException("Tenant context is required.");
        var result = await _mediator.Send(
            new GetCreditUsageDashboardQuery(tenantId, request),
            cancellationToken);
        return Ok(result);
    }

    private Guid? GetUserId()
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}

public sealed record CreditUpdateResponse(int Id, string Output);
