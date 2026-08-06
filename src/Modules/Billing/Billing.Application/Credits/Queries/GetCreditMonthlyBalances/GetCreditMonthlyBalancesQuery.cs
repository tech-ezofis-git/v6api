using MediatR;
using SaaSApp.Billing.Application.Contracts;

namespace SaaSApp.Billing.Application.Credits.Queries.GetCreditMonthlyBalances;

public sealed record GetCreditMonthlyBalancesQuery(
    Guid TenantId,
    int? Year = null,
    string? CreditType = null) : IRequest<IReadOnlyList<CreditMonthlyBalanceDto>>;

public sealed class GetCreditMonthlyBalancesQueryHandler
    : IRequestHandler<GetCreditMonthlyBalancesQuery, IReadOnlyList<CreditMonthlyBalanceDto>>
{
    private readonly ICreditService _creditService;

    public GetCreditMonthlyBalancesQueryHandler(ICreditService creditService) =>
        _creditService = creditService;

    public Task<IReadOnlyList<CreditMonthlyBalanceDto>> Handle(
        GetCreditMonthlyBalancesQuery query,
        CancellationToken cancellationToken) =>
        _creditService.GetCreditMonthlyBalancesAsync(
            query.TenantId,
            query.Year,
            query.CreditType,
            cancellationToken);
}
