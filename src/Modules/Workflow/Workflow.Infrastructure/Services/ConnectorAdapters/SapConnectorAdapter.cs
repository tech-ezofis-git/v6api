using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Infrastructure.Services.ConnectorAdapters;

/// <summary>
/// SAP S/4HANA Cloud — Phase 1 OAuth connect only (token/status/refresh/disconnect).
/// Catalog AuthUrl/TokenUrl/ClientId/Secret/RedirectUri must be filled from the tenant's
/// Communication Arrangement (scenario SAP_COM_0053) OAuth 2.0 Details.
/// Optional connector configJson: <c>{"apiBaseUrl":"https://my######-api.s4hana.cloud.sap"}</c>
/// for a later PO-lookup phase. PKCE may be required by S/4 — enable when confirmed.
/// </summary>
internal sealed class SapConnectorAdapter : ConnectorProviderAdapterBase
{
    public SapConnectorAdapter(IHttpClientFactory httpClientFactory) : base(httpClientFactory) { }

    public override string ProviderCode => "SAP";
    public override bool SupportsFiles => false;
    public override bool SupportsGmail => false;
    public override bool SupportsQuickBooks => false;

    public override string BuildAuthorizeUrl(ConnectorProviderConfig config, string state)
    {
        if (string.IsNullOrWhiteSpace(config.AuthUrl))
            throw new InvalidOperationException(
                "SAP AuthUrl is not configured. Set catalog.ConnectorProviders AuthUrl from S/4 OAuth 2.0 Details.");

        var query = new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId,
            ["redirect_uri"] = config.RedirectUri,
            ["response_type"] = "code",
            ["state"] = state
        };
        if (!string.IsNullOrWhiteSpace(config.Scopes))
            query["scope"] = config.Scopes;

        return AppendQuery(config.AuthUrl, query);
    }
}
