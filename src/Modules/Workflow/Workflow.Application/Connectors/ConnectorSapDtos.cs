using System.Text.Json.Serialization;

namespace SaaSApp.Workflow.Application.Connectors;

/// <summary>AP Agent payload: look up an SAP Purchase Order by PO number.</summary>
public sealed record ConnectorSapPoLookupRequest(string PoNumber);

/// <summary>AP-friendly PO line (snake_case), matching samplePurchaseOrders ConfigJson.</summary>
public sealed class ConnectorSapPoLineDto
{
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("qty")]
    public decimal? Qty { get; init; }

    [JsonPropertyName("unit_price")]
    public decimal? UnitPrice { get; init; }

    [JsonPropertyName("amount")]
    public decimal? Amount { get; init; }

    [JsonPropertyName("item_no")]
    public string? ItemNo { get; init; }

    [JsonPropertyName("line_no")]
    public int? LineNo { get; init; }
}

/// <summary>
/// Same conceptual fields as QB PO lookup for AP Agent matching:
/// po_number, vendor, total, currency, lines.
/// </summary>
public sealed class ConnectorSapPurchaseOrderDto
{
    [JsonPropertyName("po_number")]
    public string? PoNumber { get; init; }

    [JsonPropertyName("vendor")]
    public string? Vendor { get; init; }

    [JsonPropertyName("total")]
    public decimal? Total { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }

    [JsonPropertyName("lines")]
    public IReadOnlyList<ConnectorSapPoLineDto> Lines { get; init; } = Array.Empty<ConnectorSapPoLineDto>();
}

public sealed record ConnectorSapPoLookupResponse(
    bool Found,
    string PoNumber,
    string? Source,
    ConnectorSapPurchaseOrderDto? PurchaseOrder,
    string? Reason = null);
