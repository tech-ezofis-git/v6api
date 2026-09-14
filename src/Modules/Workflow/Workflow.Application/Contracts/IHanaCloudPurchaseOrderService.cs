using SaaSApp.Workflow.Application.Connectors;

namespace SaaSApp.Workflow.Application.Contracts;

public interface IHanaCloudPurchaseOrderService
{
    /// <summary>
    /// Read purchase orders from the HANA Cloud database stored on dbo.connector.HanaDatabaseJson.
    /// Throws <see cref="InvalidOperationException"/> when the connector or HANA settings are missing.
    /// </summary>
    Task<ConnectorHanaPoLookupResponse> ListAsync(
        Guid connectorId,
        string? poNumber,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Save instance id, invoice number, and match status on the HANA purchase-order row.
    /// Throws <see cref="InvalidOperationException"/> when the connector or PO is missing.
    /// </summary>
    Task<ConnectorHanaPoMatchResponse> MatchAsync(
        Guid connectorId,
        ConnectorHanaPoMatchRequest request,
        CancellationToken cancellationToken = default);
}
