using SaaSApp.Workflow.Application.Workflows;
using SaaSApp.Workflow.Infrastructure.Services;

namespace SaaSApp.Workflow.Infrastructure.Tests;

public sealed class SapGapClosureTests
{
    private const string SampleConfig = """
        {
          "provider": "SAP",
          "mode": "sample",
          "sampleVendors": [
            { "id": "V-ACME", "displayName": "ACME Supplies", "email": "ap@acme.example" }
          ],
          "samplePurchaseOrders": [
            {
              "po_number": "PO-60001",
              "vendor": "ACME Supplies",
              "total": 1500.00,
              "currency": "USD",
              "lines": []
            },
            {
              "po_number": "PO-SAP-1001",
              "vendor": "Contoso Trading",
              "total": 2500.00,
              "currency": "USD",
              "lines": []
            }
          ]
        }
        """;

    [Fact]
    public void VendorResolver_ReturnsSampleVendorsAndDerivedPoVendors()
    {
        var items = SapSampleVendorResolver.Resolve(SampleConfig, "Vendor", null, 50);
        Assert.Contains(items, i => i.DisplayName == "ACME Supplies");
        Assert.Contains(items, i => i.DisplayName == "Contoso Trading");
        Assert.All(items, i => Assert.Equal(SapSampleVendorResolver.SampleSource, i.Source));
    }

    [Fact]
    public void VendorResolver_FiltersByQuery()
    {
        var items = SapSampleVendorResolver.Resolve(SampleConfig, "Vendor", "Contoso", 50);
        Assert.Single(items);
        Assert.Equal("Contoso Trading", items[0].DisplayName);
    }

    [Fact]
    public void PoMasterJson_RoundTripsSettings()
    {
        var sapId = Guid.Parse("983bddbe-6a1a-4cd8-a024-9b4d84ba9981");
        var json = WorkflowPoMasterJson.Upsert(
            """{"Settings":{"General":{"Name":"AP Doc"}}}""",
            "SAP",
            sapId,
            null);

        Assert.True(WorkflowPoMasterJson.TryRead(json, out var source, out var connectorId, out _));
        Assert.Equal("SAP", source);
        Assert.Equal(sapId, connectorId);
    }

    [Fact]
    public void LiveLookup_DetectsMissingGatewayUrl()
    {
        Assert.False(SapLivePurchaseOrderLookup.TryGetLookupUrl(
            """{"mode":"sample"}""",
            "PO-1",
            out _,
            out var reason));
        Assert.Equal("live_sap_not_configured", reason);
    }

    [Fact]
    public void LiveLookup_BuildsUrlFromTemplate()
    {
        Assert.True(SapLivePurchaseOrderLookup.TryGetLookupUrl(
            """{"live":{"purchaseOrderLookupUrl":"https://gw.example/po/{poNumber}"}}""",
            "PO-60001",
            out var url,
            out _));
        Assert.Equal("https://gw.example/po/PO-60001", url);
    }
}
