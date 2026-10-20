using SaaSApp.Workflow.Infrastructure.Services;
using Xunit;

namespace SaaSApp.Workflow.Infrastructure.Tests;

public sealed class EzfbColumnNamingTests
{
    [Theory]
    [InlineData("Company Unit", "Company_Unit")]
    [InlineData("Material Code", "Material_Code")]
    [InlineData("Brand", "Brand")]
    [InlineData("Varient", "Varient")]
    [InlineData("Packaging Component", "Packaging_Component")]
    [InlineData("Diameter-Neck", "DiameterNeck")]
    [InlineData("Size(D)", "SizeD")]
    [InlineData("Dimension (L)", "Dimension_L")]
    public void ToColumnNameFromLabel_GeneratesExpectedIdentifiers(string label, string expected)
    {
        Assert.True(EzfbColumnNaming.TryToColumnNameFromLabel(label, out var column));
        Assert.Equal(expected, column);
    }

    [Fact]
    public void TryResolveEzfbColumn_ResolvesStoredColumnName()
    {
        var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Material_Code",
            "Company_Unit"
        };

        var resolved = EzfbColumnNaming.TryResolveEzfbColumn(
            "Company_Unit",
            "Company Unit",
            "6335ba18-9181-46c9-8b19-e1f0cafd59bb",
            existingCols,
            out var col,
            out var kind);

        Assert.True(resolved);
        Assert.Equal("Company_Unit", col);
        Assert.Equal(EzfbColumnNaming.EzfbColumnMatchKind.StoredColumnName, kind);
    }

    [Fact]
    public void TryResolveEzfbColumn_ResolvesLabelFallbackWhenColumnNameNull()
    {
        var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Packaging_Component"
        };

        var resolved = EzfbColumnNaming.TryResolveEzfbColumn(
            null,
            "Packaging Component",
            "55af1960-bb4b-4adb-9d04-8bcf8f17e5e5",
            existingCols,
            out var col,
            out var kind);

        Assert.True(resolved);
        Assert.Equal("Packaging_Component", col);
        Assert.Equal(EzfbColumnNaming.EzfbColumnMatchKind.SanitizedName, kind);
    }
}
