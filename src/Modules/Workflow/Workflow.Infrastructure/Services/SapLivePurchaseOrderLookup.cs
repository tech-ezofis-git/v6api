using System.Net.Http.Headers;
using System.Text.Json;
using SaaSApp.Workflow.Application.Connectors;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>
/// Optional live SAP PO lookup via ConfigJson gateway URL (customer/iPaaS),
/// not a built-in BAPI/OData client. Example ConfigJson:
/// <code>
/// { "live": { "purchaseOrderLookupUrl": "https://gateway/po/{poNumber}" } }
/// </code>
/// </summary>
public static class SapLivePurchaseOrderLookup
{
    public static bool TryGetLookupUrl(string? configJson, string poNumber, out string? url, out string? reason)
    {
        url = null;
        reason = null;
        if (string.IsNullOrWhiteSpace(configJson))
        {
            reason = "live_sap_not_configured";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(configJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                reason = "live_sap_not_configured";
                return false;
            }

            var root = doc.RootElement;
            string? template = null;
            if (root.TryGetProperty("live", out var live) && live.ValueKind == JsonValueKind.Object)
            {
                template = GetString(live, "purchaseOrderLookupUrl", "poLookupUrl", "url");
            }
            template ??= GetString(root, "purchaseOrderLookupUrl", "livePurchaseOrderLookupUrl");

            if (string.IsNullOrWhiteSpace(template))
            {
                reason = "live_sap_not_configured";
                return false;
            }

            url = template
                .Replace("{poNumber}", Uri.EscapeDataString(poNumber), StringComparison.OrdinalIgnoreCase)
                .Replace("{po_number}", Uri.EscapeDataString(poNumber), StringComparison.OrdinalIgnoreCase);
            return true;
        }
        catch (JsonException)
        {
            reason = "live_sap_not_configured";
            return false;
        }
    }

    public static async Task<(ConnectorSapPurchaseOrderDto? Po, string? Source, string? Reason)> LookupAsync(
        HttpClient http,
        string url,
        string poNumber,
        string? bearerToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(bearerToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return (null, null, "not_found");

        if (!response.IsSuccessStatusCode)
        {
            return (null, null, $"live_http_{(int)response.StatusCode}");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
            return (null, null, "not_found");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("found", out var foundEl)
            && foundEl.ValueKind is JsonValueKind.False)
        {
            return (null, null, "not_found");
        }

        JsonElement poEl = root;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("purchaseOrder", out var nested) && nested.ValueKind == JsonValueKind.Object)
                poEl = nested;
            else if (root.TryGetProperty("purchase_order", out nested) && nested.ValueKind == JsonValueKind.Object)
                poEl = nested;
        }

        if (poEl.ValueKind != JsonValueKind.Object)
            return (null, null, "invalid_live_response");

        // Reuse sample mapper via a tiny JSON round-trip into sample resolver shape.
        var wrapped = $"{{\"mode\":\"sample\",\"samplePurchaseOrders\":[{poEl.GetRawText()}]}}";
        if (SapSamplePurchaseOrderResolver.TryResolve(wrapped, poNumber, out var po, out _))
            return (po, "sap_live", null);

        return (null, null, "invalid_live_response");
    }

    private static string? GetString(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
            }
        }
        return null;
    }
}
