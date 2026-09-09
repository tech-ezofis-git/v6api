using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace SaaSApp.Api.Services;

public sealed class BrandingOptions
{
    public const string SectionName = "Branding";

    /// <summary>Fixed secret (at least 32 characters) used to encrypt/decrypt branding names.</summary>
    public string EncryptionKey { get; set; } = "Ezofis.Branding.ChangeMe.Key.32chars!";
}

public interface IBrandingCryptoService
{
    string Encrypt(string brandingName);
    string Decrypt(string encryptedBrandingName);
}

public sealed class BrandingCryptoService : IBrandingCryptoService
{
    private readonly byte[] _key;

    public BrandingCryptoService(IOptions<BrandingOptions> options, IConfiguration configuration)
    {
        var keyText = options.Value.EncryptionKey
            ?? configuration["Branding:EncryptionKey"]
            ?? "Ezofis.Branding.ChangeMe.Key.32chars!";
        if (string.IsNullOrWhiteSpace(keyText) || keyText.Trim().Length < 16)
            throw new InvalidOperationException("Branding:EncryptionKey must be at least 16 characters.");

        // Derive a stable 32-byte AES key from the configured secret.
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(keyText.Trim()));
    }

    public string Encrypt(string brandingName)
    {
        if (string.IsNullOrWhiteSpace(brandingName))
            throw new ArgumentException("Branding name is required.", nameof(brandingName));

        var plain = Encoding.UTF8.GetBytes(brandingName.Trim());
        var iv = RandomNumberGenerator.GetBytes(16);

        using var aes = Aes.Create();
        aes.Key = _key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        var cipher = encryptor.TransformFinalBlock(plain, 0, plain.Length);

        // IV + cipher → Base64Url (safe for path segments)
        var payload = new byte[iv.Length + cipher.Length];
        Buffer.BlockCopy(iv, 0, payload, 0, iv.Length);
        Buffer.BlockCopy(cipher, 0, payload, iv.Length, cipher.Length);
        return Base64UrlEncode(payload);
    }

    public string Decrypt(string encryptedBrandingName)
    {
        if (string.IsNullOrWhiteSpace(encryptedBrandingName))
            throw new ArgumentException("Encrypted branding name is required.", nameof(encryptedBrandingName));

        var payload = Base64UrlDecode(encryptedBrandingName.Trim());
        if (payload.Length <= 16)
            throw new ArgumentException("Invalid encrypted branding name.");

        var iv = payload.AsSpan(0, 16).ToArray();
        var cipher = payload.AsSpan(16).ToArray();

        using var aes = Aes.Create();
        aes.Key = _key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        var plain = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
        return Encoding.UTF8.GetString(plain);
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
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
