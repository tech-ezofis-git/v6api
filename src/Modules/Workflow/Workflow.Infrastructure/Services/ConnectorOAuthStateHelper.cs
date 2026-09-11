using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SaaSApp.Workflow.Infrastructure.Services;

internal sealed class ConnectorOAuthStatePayload
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid ConnectorId { get; set; }
    public string ProviderCode { get; set; } = string.Empty;
    public string? SuccessRedirectUrl { get; set; }
    public long Exp { get; set; }
}

internal static class ConnectorOAuthStateHelper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string Create(ConnectorOAuthStatePayload payload, string signingKey)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var body = ToBase64Url(Encoding.UTF8.GetBytes(json));
        var sig = Sign(body, signingKey);
        return $"{body}.{sig}";
    }

    public static bool TryParse(string? state, string signingKey, out ConnectorOAuthStatePayload? payload, out string? error)
    {
        payload = null;
        error = null;
        try
        {
            payload = Parse(state ?? string.Empty, signingKey);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static ConnectorOAuthStatePayload Parse(string state, string signingKey)
    {
        if (string.IsNullOrWhiteSpace(state))
            throw new InvalidOperationException("OAuth state is missing.");

        // Providers sometimes turn '+' into space when echoing state; normalize before verify.
        state = state.Trim().Replace(' ', '+');

        var parts = state.Split('.', 2);
        if (parts.Length != 2)
            throw new InvalidOperationException("OAuth state is invalid.");

        var body = parts[0];
        var sig = parts[1];
        var expected = Sign(body, signingKey);
        if (!FixedEquals(expected, sig))
            throw new InvalidOperationException("OAuth state signature is invalid.");

        var json = Encoding.UTF8.GetString(FromBase64Url(body));
        var payload = JsonSerializer.Deserialize<ConnectorOAuthStatePayload>(json, JsonOptions)
            ?? throw new InvalidOperationException("OAuth state payload is invalid.");

        if (payload.Exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            throw new InvalidOperationException("OAuth state has expired. Start authorization again.");

        return payload;
    }

    private static string Sign(string body, string signingKey)
    {
        if (string.IsNullOrWhiteSpace(signingKey))
            throw new InvalidOperationException("ConnectorOAuth state signing key is not configured.");

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        return ToBase64Url(hash);
    }

    private static bool FixedEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        if (ba.Length != bb.Length)
            return false;
        return CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
