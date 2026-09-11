using SaaSApp.Workflow.Application.Connectors;

namespace SaaSApp.Workflow.Application.Contracts;

public interface ISapPurchaseOrderLookupService
{
    /// <summary>
    /// Look up a purchase order on an SAP connector (sample ConfigJson now; live SAP later).
    /// Throws <see cref="InvalidOperationException"/> when the connector is missing or not SAP.
    /// </summary>
    Task<ConnectorSapPoLookupResponse> LookupAsync(
        Guid connectorId,
        string poNumber,
        CancellationToken cancellationToken = default);
}
