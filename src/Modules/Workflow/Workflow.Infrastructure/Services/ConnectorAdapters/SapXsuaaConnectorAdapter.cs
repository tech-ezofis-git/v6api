using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SaaSApp.Workflow.Application.Contracts;

namespace SaaSApp.Workflow.Infrastructure.Services.ConnectorAdapters;

/// <summary>
/// SAP BTP XSUAA OAuth + S/4 purchase-order OData lookup using the stored access token.
/// Connector <c>ConfigJson</c> must include <c>apiBaseUrl</c> (S/4 or API Management host).
/// </summary>
internal sealed class SapXsuaaConnectorAdapter : ConnectorProviderAdapterBase
{
    public SapXsuaaConnectorAdapter(IHttpClientFactory httpClientFactory) : base(httpClientFactory) { }

    public override string ProviderCode => "SAP_XSUAA";
    public override bool SupportsFiles => false;
    public override bool SupportsGmail => false;
    public override bool SupportsSap => true;

    public override string BuildAuthorizeUrl(ConnectorProviderConfig config, string state)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = config.ClientId,
            ["redirect_uri"] = config.RedirectUri,
            ["state"] = state
        };
        if (!string.IsNullOrWhiteSpace(config.Scopes))
            query["scope"] = config.Scopes;

        return AppendQuery(config.AuthUrl, query);
    }

    public override Task<ConnectorOAuthTokenResult> ExchangeCodeAsync(
        ConnectorProviderConfig config,
        string code,
        CancellationToken cancellationToken = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = config.RedirectUri
        };
        return RequestXsuaaTokenAsync(config, form, cancellationToken);
    }

    public override async Task<ConnectorOAuthTokenResult> RefreshTokenAsync(
        ConnectorProviderConfig config,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        };
        var result = await RequestXsuaaTokenAsync(config, form, cancellationToken);
        return result with { RefreshToken = result.RefreshToken ?? refreshToken };
    }

    public override async Task<string?> GetSapPurchaseOrderRawByNumberAsync(
        string accessToken,
        string poNumber,
        string? extraConfigJson,
        CancellationToken cancellationToken = default)
    {
        var apiBaseUrl = ReadExtra(extraConfigJson, "apiBaseUrl")
            ?? ReadExtra(extraConfigJson, "ApiBaseUrl")
            ?? throw new InvalidOperationException(
                "SAP apiBaseUrl is missing. Update connector ConfigJson, e.g. " +
                "{\"apiBaseUrl\":\"https://my-s4-host\",\"odataService\":\"API_PURCHASEORDER_PROCESS_SRV\"}.");

        var service = ReadExtra(extraConfigJson, "odataService")
            ?? ReadExtra(extraConfigJson, "OdataService")
            ?? "API_PURCHASEORDER_PROCESS_SRV";
        var sapClient = ReadExtra(extraConfigJson, "sapClient")
            ?? ReadExtra(extraConfigJson, "SapClient");

        var baseRoot = apiBaseUrl.TrimEnd('/');
        var escapedPo = Uri.EscapeDataString(poNumber.Trim());
        var clientQs = string.IsNullOrWhiteSpace(sapClient)
            ? string.Empty
            : $"sap-client={Uri.EscapeDataString(sapClient.Trim())}&";

        // Prefer key access; fall back to $filter if key 404s.
        var byKey =
            $"{baseRoot}/sap/opu/odata/sap/{service}/A_PurchaseOrder('{escapedPo}')" +
            $"?{clientQs}$expand=to_PurchaseOrderItem&$format=json";

        using var client = CreateClient();
        var byKeyBody = await SendSapGetAsync(client, byKey, accessToken, cancellationToken);
        if (byKeyBody != null)
            return byKeyBody;

        var byFilter =
            $"{baseRoot}/sap/opu/odata/sap/{service}/A_PurchaseOrder" +
            $"?{clientQs}$filter=PurchaseOrder eq '{EscapeODataString(poNumber.Trim())}'" +
            "&$expand=to_PurchaseOrderItem&$format=json";

        var filterBody = await SendSapGetAsync(client, byFilter, accessToken, cancellationToken);
        if (filterBody == null)
            return null;

        using var doc = JsonDocument.Parse(filterBody);
        if (doc.RootElement.TryGetProperty("d", out var d)
            && d.TryGetProperty("results", out var results)
            && results.ValueKind == JsonValueKind.Array
            && results.GetArrayLength() > 0)
        {
            return results[0].GetRawText();
        }

        return null;
    }

    public override async Task<string> ListSapPurchaseOrdersRawAsync(
        string accessToken,
        int top,
        string? extraConfigJson,
        CancellationToken cancellationToken = default)
    {
        top = Math.Clamp(top, 1, 10);
        var apiBaseUrl = ReadExtra(extraConfigJson, "apiBaseUrl")
            ?? ReadExtra(extraConfigJson, "ApiBaseUrl")
            ?? throw new InvalidOperationException(
                "SAP apiBaseUrl is missing. Pass apiBaseUrl in the request body once, e.g. " +
                "{\"top\":2,\"apiBaseUrl\":\"https://my-s4-host\"}.");

        var service = ReadExtra(extraConfigJson, "odataService")
            ?? ReadExtra(extraConfigJson, "OdataService")
            ?? "API_PURCHASEORDER_PROCESS_SRV";
        var sapClient = ReadExtra(extraConfigJson, "sapClient")
            ?? ReadExtra(extraConfigJson, "SapClient");

        var baseRoot = apiBaseUrl.TrimEnd('/');
        var clientQs = string.IsNullOrWhiteSpace(sapClient)
            ? string.Empty
            : $"sap-client={Uri.EscapeDataString(sapClient.Trim())}&";

        // Small sample only — never pull full 69k set.
        var url =
            $"{baseRoot}/sap/opu/odata/sap/{service}/A_PurchaseOrder" +
            $"?{clientQs}$top={top}&$select=PurchaseOrder,Supplier,SupplierName,PurchaseOrderDate,DocumentCurrency,CompanyCode,PurchasingOrganization,PurchasingProcessingStatus" +
            "&$format=json";

        using var client = CreateClient();
        var body = await SendSapGetAsync(client, url, accessToken, cancellationToken);
        if (body == null)
            throw new InvalidOperationException("SAP PO list returned 404. Check apiBaseUrl / odataService.");

        return body;
    }

    private static async Task<string?> SendSapGetAsync(
        HttpClient client,
        string url,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var res = await client.SendAsync(req, cancellationToken);
        var body = await res.Content.ReadAsStringAsync(cancellationToken);

        if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        if (!res.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"SAP PO API failed ({(int)res.StatusCode}): {Truncate(body, 800)}");
        }

        return body;
    }

    private async Task<ConnectorOAuthTokenResult> RequestXsuaaTokenAsync(
        ConnectorProviderConfig config,
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, config.TokenUrl);
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.ClientId}:{config.ClientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        request.Content = new FormUrlEncodedContent(form);

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"SAP XSUAA token endpoint failed ({(int)response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var access = root.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("XSUAA token response missing access_token.");
        string? refresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        DateTime? expires = null;
        if (root.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var seconds))
            expires = DateTime.UtcNow.AddSeconds(seconds);

        string? email = null;
        if (root.TryGetProperty("email", out var emailEl) && emailEl.ValueKind == JsonValueKind.String)
            email = emailEl.GetString();

        return new ConnectorOAuthTokenResult(access, refresh, expires, email, null);
    }

    private static string EscapeODataString(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";
}
