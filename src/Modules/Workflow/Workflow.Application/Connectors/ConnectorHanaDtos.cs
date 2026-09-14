namespace SaaSApp.Workflow.Application.Connectors;

/// <summary>Optional filter for GET/POST /api/connector/{id}/hana/purchase-orders.</summary>
public sealed record ConnectorHanaPoLookupRequest(string? PoNumber = null);

public sealed record ConnectorHanaPoLineDto(
    int? ItemNumber,
    string? ItemCategory,
    string? MaterialId,
    string? MaterialDescription,
    string? MaterialGroup,
    string? Plant,
    decimal? OrderQuantity,
    string? UnitOfMeasure,
    decimal? NetPrice,
    decimal? PriceUnit,
    decimal? NetValue);

public sealed record ConnectorHanaPoMatchLinkDto(
    string? InstanceId,
    string? InvoiceNumber,
    string? Status);

public sealed record ConnectorHanaPurchaseOrderDto(
    string? PoNumber,
    string? PoType,
    string? SupplierId,
    string? SupplierName,
    string? CompanyCode,
    string? PurchasingOrg,
    string? PurchasingGroup,
    string? Currency,
    string? PoDate,
    string? ApprovalStatus,
    string? CreatedBy,
    decimal? Total,
    IReadOnlyList<ConnectorHanaPoLineDto> Items,
    IReadOnlyList<ConnectorHanaPoMatchLinkDto> Matches);

/// <summary>
/// One row in PO_INVOICE_MATCH. The same PO can have many instance ids.
/// instanceId + poNumber identify the row. Send invoiceNumber to create the link.
/// Send status later to update that instance only.
/// </summary>
public sealed record ConnectorHanaPoMatchRequest(
    string PoNumber,
    string? InstanceId = null,
    string? InvoiceNumber = null,
    string? Status = null);

public sealed record ConnectorHanaPoMatchResponse(
    bool Updated,
    bool Created,
    Guid ConnectorId,
    string PoNumber,
    string? InstanceId,
    string? InvoiceNumber,
    string? Status);

public sealed record ConnectorHanaPoLookupResponse(
    bool Found,
    Guid ConnectorId,
    string? PoNumber,
    int Count,
    IReadOnlyList<ConnectorHanaPurchaseOrderDto> Items);
