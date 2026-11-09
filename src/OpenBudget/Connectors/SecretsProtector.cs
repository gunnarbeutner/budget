using System.Security.Cryptography;
using System.Text;

namespace OpenBudget.Connectors;

/// <summary>
/// Encrypts bank credentials and session state with AES-256-GCM. The 256-bit key comes from the
/// environment variable <see cref="KeyVariable"/> (base64) and is deliberately never stored in the
/// database or on the data volume: a leaked backup of the data alone reveals no credentials.
///
/// Without a (valid) key the app keeps running; <see cref="IsConfigured"/> is false and bank-login
/// features are disabled. A payload that cannot be decrypted (wrong or rotated key, tampering)
/// reports failure instead of throwing, so callers can ask the user to re-enter credentials.
///
/// Payload format: "v1:" + base64(nonce[12] + tag[16] + ciphertext).
/// </summary>
public sealed class SecretsProtector
{
    public const string KeyVariable = "OPENBUDGET_SECRETS_KEY";
    private const string Prefix = "v1:";
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly byte[]? _key;

    public SecretsProtector(IConfiguration configuration) : this(configuration[KeyVariable])
    {
    }

    /// <summary>Builds a protector from a base64 key (null/blank = not configured).</summary>
    public SecretsProtector(string? base64Key)
    {
        if (string.IsNullOrWhiteSpace(base64Key))
        {
            ConfigurationProblem = $"{KeyVariable} is not set.";
            return;
        }

        try
        {
            var key = Convert.FromBase64String(base64Key.Trim());
            if (key.Length != KeyBytes)
            {
                ConfigurationProblem = $"{KeyVariable} must be base64 of exactly 32 bytes (got {key.Length}).";
                return;
            }
            _key = key;
        }
        catch (FormatException)
        {
            ConfigurationProblem = $"{KeyVariable} is not valid base64.";
        }
    }

    public bool IsConfigured => _key != null;

    /// <summary>Why <see cref="IsConfigured"/> is false (never contains key material).</summary>
    public string? ConfigurationProblem { get; }

    /// <summary>A fresh random key in the format the variable expects.</summary>
    public static string GenerateKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeyBytes));

    public string Encrypt(string plaintext, string purpose)
    {
        var key = _key ?? throw new InvalidOperationException("The secrets key is not configured.");
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagBytes];
        using (var aes = new AesGcm(key, TagBytes))
            aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(purpose));

        var payload = new byte[NonceBytes + TagBytes + cipher.Length];
        nonce.CopyTo(payload, 0);
        tag.CopyTo(payload, NonceBytes);
        cipher.CopyTo(payload, NonceBytes + TagBytes);
        return Prefix + Convert.ToBase64String(payload);
    }

    /// <summary>Decrypts a payload produced by <see cref="Encrypt"/>; false when the key is missing/wrong or the data was altered.</summary>
    public bool TryDecrypt(string? payload, string purpose, out string plaintext)
    {
        plaintext = string.Empty;
        if (_key == null || string.IsNullOrEmpty(payload) || !payload.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        try
        {
            var data = Convert.FromBase64String(payload[Prefix.Length..]);
            if (data.Length < NonceBytes + TagBytes) return false;
            var nonce = data.AsSpan(0, NonceBytes);
            var tag = data.AsSpan(NonceBytes, TagBytes);
            var cipher = data.AsSpan(NonceBytes + TagBytes);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(_key, TagBytes);
            aes.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(purpose));
            plaintext = Encoding.UTF8.GetString(plain);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }
}
