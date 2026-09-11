using SaaSApp.Workflow.Application.Connectors;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Infrastructure.Services;

public sealed class SapPurchaseOrderLookupService : ISapPurchaseOrderLookupService
{
    private readonly IConnectorService _connectorService;
    private readonly IHttpClientFactory _httpClientFactory;

    public SapPurchaseOrderLookupService(
        IConnectorService connectorService,
        IHttpClientFactory httpClientFactory)
    {
        _connectorService = connectorService;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<ConnectorSapPoLookupResponse> LookupAsync(
        Guid connectorId,
        string poNumber,
        CancellationToken cancellationToken = default)
    {
        if (connectorId == Guid.Empty)
            throw new InvalidOperationException("Connector id is required.");

        var normalized = (poNumber ?? string.Empty).Trim();
        if (normalized.Length == 0)
            throw new ArgumentException("poNumber is required.", nameof(poNumber));

        var connector = await _connectorService.GetByIdAsync(connectorId, cancellationToken);
        if (connector is null)
            throw new InvalidOperationException("Connector not found.");

        if (!SapConnectorProviderCodes.IsSap(connector.ProviderCode))
            throw new InvalidOperationException(
                $"Connector provider '{connector.ProviderCode}' is not SAP. Expected SAP / SAP_XSUAA / SAP_*.");

        if (SapSamplePurchaseOrderResolver.TryResolve(
                connector.ConfigJson,
                normalized,
                out var purchaseOrder,
                out var source))
        {
            return new ConnectorSapPoLookupResponse(true, normalized, source, purchaseOrder);
        }

        // Optional customer gateway URL in ConfigJson.live.purchaseOrderLookupUrl
        string? liveReason = null;
        if (SapLivePurchaseOrderLookup.TryGetLookupUrl(connector.ConfigJson, normalized, out var url, out liveReason)
            && !string.IsNullOrWhiteSpace(url))
        {
            var http = _httpClientFactory.CreateClient(nameof(SapPurchaseOrderLookupService));
            http.Timeout = TimeSpan.FromSeconds(30);
            var (livePo, liveSource, reason) = await SapLivePurchaseOrderLookup.LookupAsync(
                http,
                url!,
                normalized,
                bearerToken: null,
                cancellationToken);
            if (livePo != null)
                return new ConnectorSapPoLookupResponse(true, normalized, liveSource ?? "sap_live", livePo);
            return new ConnectorSapPoLookupResponse(
                false,
                normalized,
                null,
                null,
                reason ?? "not_found");
        }

        var reasonOut = liveReason == "live_sap_not_configured"
            ? "not_found (sample miss; live SAP gateway URL not configured on connector ConfigJson.live.purchaseOrderLookupUrl)"
            : "not_found";
        return new ConnectorSapPoLookupResponse(false, normalized, null, null, reasonOut);
    }
}
