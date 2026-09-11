using SaaSApp.Workflow.Application.Connectors;
using SaaSApp.Workflow.Application.Contracts;
using SaaSApp.Workflow.Infrastructure.Services;

namespace SaaSApp.Workflow.Infrastructure.Tests;

public sealed class SapPurchaseOrderLookupServiceTests
{
    private const string SampleConfigJson = """
        {
          "provider": "SAP",
          "mode": "sample",
          "samplePurchaseOrders": [
            {
              "po_number": "PO-60001",
              "vendor": "APEX INDUSTRIAL COMPONENTS LTD",
              "total": 5203.65,
              "currency": "CAD",
              "lines": [
                { "description": "Bearing assembly kit", "qty": 5, "unit_price": 650.00, "amount": 3250.00 },
                { "description": "Seal pack", "qty": 10, "unit_price": 125.00, "amount": 1250.00 },
                { "description": "Freight", "qty": 1, "unit_price": 703.65, "amount": 703.65 }
              ]
            },
            {
              "po_number": "PO-SAP-1001",
              "vendor": "Contoso Trading",
              "total": 2500.00,
              "currency": "USD",
              "lines": [
                { "description": "Service retainer", "qty": 1, "unit_price": 2500, "amount": 2500 }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task Lookup_Hit_Po60001_ReturnsSampleWithSource()
    {
        var connectorId = Guid.Parse("983bddbe-6a1a-4cd8-a024-9b4d84ba9981");
        var connectors = new FakeConnectorService();
        connectors.Add(MakeConnector(connectorId, "SAP_XSUAA", SampleConfigJson));
        var sut = new SapPurchaseOrderLookupService(connectors, new FakeHttpClientFactory());

        var result = await sut.LookupAsync(connectorId, "PO-60001");

        Assert.True(result.Found);
        Assert.Equal("PO-60001", result.PoNumber);
        Assert.Equal(SapSamplePurchaseOrderResolver.SampleSource, result.Source);
        Assert.NotNull(result.PurchaseOrder);
        Assert.Equal("PO-60001", result.PurchaseOrder!.PoNumber);
        Assert.Equal("APEX INDUSTRIAL COMPONENTS LTD", result.PurchaseOrder.Vendor);
        Assert.Equal(5203.65m, result.PurchaseOrder.Total);
        Assert.Equal("CAD", result.PurchaseOrder.Currency);
        Assert.Equal(3, result.PurchaseOrder.Lines.Count);
        Assert.Equal("Bearing assembly kit", result.PurchaseOrder.Lines[0].Description);
    }

    [Fact]
    public async Task Lookup_UnknownPo_ReturnsNotFound_NoInventedPo()
    {
        var connectorId = Guid.NewGuid();
        var connectors = new FakeConnectorService();
        connectors.Add(MakeConnector(connectorId, "SAP", SampleConfigJson));
        var sut = new SapPurchaseOrderLookupService(connectors, new FakeHttpClientFactory());

        var result = await sut.LookupAsync(connectorId, "PO-DOES-NOT-EXIST");

        Assert.False(result.Found);
        Assert.Equal("PO-DOES-NOT-EXIST", result.PoNumber);
        Assert.Null(result.Source);
        Assert.Null(result.PurchaseOrder);
    }

    [Fact]
    public async Task Lookup_NonSapConnector_Throws()
    {
        var connectorId = Guid.NewGuid();
        var connectors = new FakeConnectorService();
        connectors.Add(MakeConnector(connectorId, "QUICKBOOKS", SampleConfigJson));
        var sut = new SapPurchaseOrderLookupService(connectors, new FakeHttpClientFactory());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.LookupAsync(connectorId, "PO-60001"));

        Assert.Contains("not SAP", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SAP")]
    [InlineData("SAP_XSUAA")]
    [InlineData("sap_btp")]
    public void ProviderCodes_IsSap_AcceptsSapFamily(string code)
        => Assert.True(SapConnectorProviderCodes.IsSap(code));

    [Theory]
    [InlineData("QUICKBOOKS")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("SALESFORCE")]
    public void ProviderCodes_IsSap_RejectsNonSap(string? code)
        => Assert.False(SapConnectorProviderCodes.IsSap(code));

    private static ConnectorDto MakeConnector(Guid id, string providerCode, string? configJson)
        => new(
            Id: id,
            TenantId: Guid.Parse("b843b988-00ec-44e3-aca2-b8470133ef63"),
            Name: "Test SAP",
            ProviderCode: providerCode,
            ConfigJson: configJson,
            OAuthStatus: "Connected",
            ExternalAccountEmail: null,
            TokenExpiresAtUtc: null,
            IsDefault: true,
            CreatedAtUtc: DateTime.UtcNow,
            ModifiedAtUtc: null,
            CreatedBy: Guid.Empty,
            ModifiedBy: null,
            IsDeleted: false);

    private sealed class FakeConnectorService : IConnectorService
    {
        private readonly Dictionary<Guid, ConnectorDto> _byId = new();

        public void Add(ConnectorDto dto) => _byId[dto.Id] = dto;

        public Task<ConnectorDto> CreateAsync(ConnectorUpsertRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ConnectorDto?> UpdateAsync(Guid id, ConnectorUpsertRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ConnectorDto>> ListAsync(ConnectorListRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ConnectorDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(_byId.TryGetValue(id, out var dto) ? dto : null);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
