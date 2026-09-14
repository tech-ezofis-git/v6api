using SaaSApp.Workflow.Infrastructure.Services;

namespace SaaSApp.Workflow.Infrastructure.Tests;

public sealed class HanaCloudPurchaseOrderMapperTests
{
    [Fact]
    public void ParseItems_SumsNetValue_FromHanaJson()
    {
        const string raw = """
            [
              {"item_number": 1, "material_description": "Frame", "order_quantity": 2, "net_price": 10.5, "net_value": 21.0},
              {"item_number": 2, "material_description": "Seat", "order_quantity": 2, "net_price": 4, "net_value": 8}
            ]
            """;

        var lines = HanaCloudPurchaseOrderMapper.ParseItems(raw);

        Assert.Equal(2, lines.Count);
        Assert.Equal("Frame", lines[0].MaterialDescription);
        Assert.Equal(10.5m, lines[0].NetPrice);
        Assert.Equal(29.0m, HanaCloudPurchaseOrderMapper.SumNetValue(lines));
    }
}
