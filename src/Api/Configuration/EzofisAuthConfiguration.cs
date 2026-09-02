using Microsoft.Extensions.Configuration;

namespace SaaSApp.Api.Configuration;

/// <summary>
/// Resolves Ezofis JWT signing settings. Falls back to appsettings.Production.json on disk when
/// environment variables blank out or override merged config (common with Azure .env.azure).
/// </summary>
internal static class EzofisAuthConfiguration
{
    public static string? ResolveSigningKey(IConfiguration configuration)
    {
        var key = configuration["EzofisAuth:SigningKey"];
        if (!string.IsNullOrWhiteSpace(key))
            return key.Trim();

        return ReadProductionJsonValue("EzofisAuth:SigningKey");
    }

    public static string ResolveIssuer(IConfiguration configuration)
    {
        var issuer = configuration["EzofisAuth:Issuer"];
        if (!string.IsNullOrWhiteSpace(issuer))
            return issuer.Trim();

        return ReadProductionJsonValue("EzofisAuth:Issuer") ?? "Ezofis";
    }

    public static string ResolveAudience(IConfiguration configuration)
    {
        var audience = configuration["EzofisAuth:Audience"];
        if (!string.IsNullOrWhiteSpace(audience))
            return audience.Trim();

        return ReadProductionJsonValue("EzofisAuth:Audience") ?? "Ezofis";
    }

    private static string? ReadProductionJsonValue(string key)
    {
        var prodPath = Path.Combine(AppContext.BaseDirectory, "appsettings.Production.json");
        if (!File.Exists(prodPath))
            return null;

        var prodConfig = new ConfigurationBuilder()
            .AddJsonFile(prodPath, optional: false, reloadOnChange: false)
            .Build();

        var value = prodConfig[key];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
