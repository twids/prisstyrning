using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Prisstyrning.Security;

public sealed class CredentialEncryptionOptions
{
    public const string SectionName = "Security:CredentialEncryption";
    public string KeyFile { get; set; } = string.Empty;
    public bool PreserveLegacyDaikinTokenColumns { get; set; } = true;
}

public interface IAccountSecretProtector
{
    bool IsConfigured { get; }
    string Protect(string plaintext, string userId, string purpose);
    string Unprotect(string protectedValue, string userId, string purpose);
}

/// <summary>
/// Versioned AES-256-GCM envelope encryption. The account and credential kind
/// are authenticated as AAD so ciphertext cannot be moved between accounts or
/// repurposed as another credential.
/// </summary>
public sealed class AccountSecretProtector : IAccountSecretProtector
{
    private const string Prefix = "v1";
    private readonly byte[]? _key;

    public AccountSecretProtector(IOptions<CredentialEncryptionOptions> options)
    {
        _key = LoadKey(options.Value.KeyFile);
    }

    internal AccountSecretProtector(byte[] key)
    {
        if (key.Length != 32) throw new ArgumentException("Credential encryption key must be 32 bytes.", nameof(key));
        _key = key.ToArray();
    }

    public bool IsConfigured => _key is { Length: 32 };

    public string Protect(string plaintext, string userId, string purpose)
    {
        EnsureInputs(plaintext, userId, purpose);
        var key = _key ?? throw new InvalidOperationException("Credential encryption key is not configured.");
        var nonce = RandomNumberGenerator.GetBytes(12);
        var clear = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[clear.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, clear, cipher, tag, Aad(userId, purpose));
        CryptographicOperations.ZeroMemory(clear);
        return string.Join('.', Prefix, Convert.ToBase64String(nonce), Convert.ToBase64String(cipher), Convert.ToBase64String(tag));
    }

    public string Unprotect(string protectedValue, string userId, string purpose)
    {
        EnsureInputs(protectedValue, userId, purpose);
        var key = _key ?? throw new InvalidOperationException("Credential encryption key is not configured.");
        var parts = protectedValue.Split('.');
        if (parts.Length != 4 || !parts[0].Equals(Prefix, StringComparison.Ordinal))
            throw new CryptographicException("Unsupported credential envelope.");
        var nonce = Convert.FromBase64String(parts[1]);
        var cipher = Convert.FromBase64String(parts[2]);
        var tag = Convert.FromBase64String(parts[3]);
        if (nonce.Length != 12 || tag.Length != 16) throw new CryptographicException("Invalid credential envelope.");
        var clear = new byte[cipher.Length];
        using var aes = new AesGcm(key, tag.Length);
        aes.Decrypt(nonce, cipher, tag, clear, Aad(userId, purpose));
        try { return Encoding.UTF8.GetString(clear); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private static byte[]? LoadKey(string keyFile)
    {
        if (string.IsNullOrWhiteSpace(keyFile)) return null;
        try
        {
            var encoded = File.ReadAllText(keyFile).Trim();
            var key = Convert.FromBase64String(encoded);
            if (key.Length != 32) throw new InvalidOperationException("Credential encryption key must decode to exactly 32 bytes.");
            return key;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            throw new InvalidOperationException("Credential encryption key file could not be loaded.", exception);
        }
    }

    private static byte[] Aad(string userId, string purpose) =>
        Encoding.UTF8.GetBytes($"prisstyrning|{userId}|{purpose}|v1");

    private static void EnsureInputs(string value, string userId, string purpose)
    {
        if (string.IsNullOrEmpty(value)) throw new ArgumentException("Secret value must not be empty.", nameof(value));
        if (!AdminService.IsValidUserId(userId)) throw new ArgumentException("Invalid account identifier.", nameof(userId));
        if (string.IsNullOrWhiteSpace(purpose) || purpose.Length > 100) throw new ArgumentException("Invalid secret purpose.", nameof(purpose));
    }
}
