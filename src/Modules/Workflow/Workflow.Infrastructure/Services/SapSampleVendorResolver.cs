using System.Text.Json;
using SaaSApp.Workflow.Application.Connectors;

namespace SaaSApp.Workflow.Infrastructure.Services;

/// <summary>
/// Resolve Vendor/Customer/Item from SAP connector ConfigJson:
/// sampleVendors array, or distinct vendors derived from samplePurchaseOrders.
/// </summary>
public static class SapSampleVendorResolver
{
    public const string SampleSource = "sap_sample";

    public static IReadOnlyList<MasterResolveItemDto> Resolve(
        string? configJson,
        string type,
        string? q,
        int maxResults)
    {
        if (string.IsNullOrWhiteSpace(configJson) || maxResults <= 0)
            return Array.Empty<MasterResolveItemDto>();

        try
        {
            using var doc = JsonDocument.Parse(configJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Array.Empty<MasterResolveItemDto>();

            var root = doc.RootElement;
            var mode = GetString(root, "mode");
            var hasVendors = root.TryGetProperty("sampleVendors", out var vendorsEl)
                && vendorsEl.ValueKind == JsonValueKind.Array
                && vendorsEl.GetArrayLength() > 0;
            var hasPos = root.TryGetProperty("samplePurchaseOrders", out var posEl)
                && posEl.ValueKind == JsonValueKind.Array
                && posEl.GetArrayLength() > 0;

            if (!string.Equals(mode, "sample", StringComparison.OrdinalIgnoreCase) && !hasVendors && !hasPos)
                return Array.Empty<MasterResolveItemDto>();

            var items = new List<MasterResolveItemDto>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (hasVendors)
            {
                foreach (var item in vendorsEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;
                    var name = GetString(item, "displayName", "name", "vendor", "Vendor", "Vendor Name");
                    if (string.IsNullOrWhiteSpace(name))
                        continue;
                    var id = GetString(item, "id", "vendorId", "code") ?? name;
                    var email = GetString(item, "email", "Email");
                    var itemType = GetString(item, "type") ?? "Vendor";
                    if (!string.Equals(itemType, type, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!MatchesQuery(q, id, name, email))
                        continue;
                    if (!seen.Add(id))
                        continue;
                    items.Add(new MasterResolveItemDto(
                        id, type, name, email, SampleSource, id, null));
                    if (items.Count >= maxResults)
                        return items;
                }
            }

            // Derive unique vendors from sample POs when type=Vendor.
            if (string.Equals(type, "Vendor", StringComparison.OrdinalIgnoreCase) && hasPos)
            {
                foreach (var po in posEl.EnumerateArray())
                {
                    if (po.ValueKind != JsonValueKind.Object)
                        continue;
                    var name = GetString(po, "vendor", "Vendor", "Vendor Name", "vendorName");
                    if (string.IsNullOrWhiteSpace(name))
                        continue;
                    var id = GetString(po, "vendor_id", "vendorId") ?? name;
                    if (!MatchesQuery(q, id, name, null))
                        continue;
                    if (!seen.Add(id))
                        continue;
                    items.Add(new MasterResolveItemDto(
                        id, type, name, null, SampleSource, id, null));
                    if (items.Count >= maxResults)
                        return items;
                }
            }

            return items;
        }
        catch (JsonException)
        {
            return Array.Empty<MasterResolveItemDto>();
        }
    }

    private static bool MatchesQuery(string? q, string? id, string? name, string? email)
    {
        if (string.IsNullOrWhiteSpace(q))
            return true;
        return (id?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (email?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
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
            foreach (var prop in obj.EnumerateObject())
            {
                if (!prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    var s = prop.Value.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(s))
                        return s;
                }
            }
        }
        return null;
    }
}
