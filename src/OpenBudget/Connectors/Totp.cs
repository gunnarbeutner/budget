using System.Globalization;
using System.Security.Cryptography;

namespace OpenBudget.Connectors;

/// <summary>
/// A time-based one-time password generator (RFC 6238) for an authenticator-app secret. <see cref="Parse"/> accepts the
/// base32 key as shown by the site (spaces, dashes and lowercase allowed) or an <c>otpauth://totp/…</c> URI, whose
/// secret, digits, period and algorithm are honoured. The secret is never printed (<see cref="ToString"/>).
/// </summary>
public sealed class TotpSecret
{
    private readonly byte[] _key;

    private TotpSecret(byte[] key, int digits, int period, HashAlgorithmName algorithm)
    {
        _key = key;
        Digits = digits;
        Period = period;
        Algorithm = algorithm;
    }

    public int Digits { get; }

    /// <summary>The time step in seconds.</summary>
    public int Period { get; }

    public HashAlgorithmName Algorithm { get; }

    /// <summary>A secret from raw key bytes (test vectors use this).</summary>
    public static TotpSecret FromKey(byte[] key, int digits = 6, int period = 30, HashAlgorithmName? algorithm = null) =>
        new(key.ToArray(), digits, period, algorithm ?? HashAlgorithmName.SHA1);

    /// <summary>Parses a base32 key or otpauth URI; throws <see cref="FormatException"/> with a message that never contains the value.</summary>
    public static TotpSecret Parse(string text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0) throw new FormatException("The TOTP secret is empty.");
        if (!s.StartsWith("otpauth:", StringComparison.OrdinalIgnoreCase))
            return new TotpSecret(DecodeBase32(s), 6, 30, HashAlgorithmName.SHA1);

        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || !string.Equals(uri.Host, "totp", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Only otpauth://totp/ URIs are supported.");
        string? secret = null;
        int digits = 6, period = 30;
        var algorithm = HashAlgorithmName.SHA1;
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            var name = Uri.UnescapeDataString(eq < 0 ? part : part[..eq]).ToUpperInvariant();
            var value = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            switch (name)
            {
                case "SECRET": secret = value; break;
                case "DIGITS":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out digits) || digits is < 6 or > 10)
                        throw new FormatException("The otpauth URI has an unsupported number of digits.");
                    break;
                case "PERIOD":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out period) || period is < 1 or > 3600)
                        throw new FormatException("The otpauth URI has an unsupported period.");
                    break;
                case "ALGORITHM":
                    algorithm = value.ToUpperInvariant() switch
                    {
                        "SHA1" => HashAlgorithmName.SHA1,
                        "SHA256" => HashAlgorithmName.SHA256,
                        "SHA512" => HashAlgorithmName.SHA512,
                        _ => throw new FormatException("The otpauth URI has an unsupported algorithm."),
                    };
                    break;
                default: break; // issuer and other labels are not needed
            }
        }
        if (string.IsNullOrWhiteSpace(secret)) throw new FormatException("The otpauth URI has no secret.");
        return new TotpSecret(DecodeBase32(secret), digits, period, algorithm);
    }

    public static bool TryParse(string? text, out TotpSecret? secret)
    {
        try
        {
            secret = Parse(text ?? "");
            return true;
        }
        catch (FormatException)
        {
            secret = null;
            return false;
        }
    }

    /// <summary>The time step (counter) of an instant.</summary>
    public long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / Period;

    /// <summary>The code for an instant, shifted by <paramref name="stepOffset"/> time steps (-1: the previous window).</summary>
    public string CodeAt(DateTimeOffset time, int stepOffset = 0) => CodeForStep(StepAt(time) + stepOffset);

    /// <summary>HOTP (RFC 4226) for a counter, with dynamic truncation.</summary>
    public string CodeForStep(long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        byte[] hash = Algorithm.Name switch
        {
            "SHA256" => HMACSHA256.HashData(_key, counter),
            "SHA512" => HMACSHA512.HashData(_key, counter),
#pragma warning disable CA5350 // RFC 6238 defaults to HMAC-SHA1; sites hand out SHA-1 secrets
            _ => HMACSHA1.HashData(_key, counter),
#pragma warning restore CA5350
        };
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var code = binary % (long)Math.Pow(10, Digits);
        return code.ToString(CultureInfo.InvariantCulture).PadLeft(Digits, '0');
    }

    public override string ToString() => $"TotpSecret {{ Digits = {Digits}, Period = {Period}, Algorithm = {Algorithm.Name}, Key = *** }}";

    private static byte[] DecodeBase32(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var raw in text)
        {
            if (raw is ' ' or '-' or '\t' or '=') continue;
            var index = alphabet.IndexOf(char.ToUpperInvariant(raw), StringComparison.Ordinal);
            if (index < 0) throw new FormatException("The TOTP secret is not valid base32 (letters A–Z and digits 2–7).");
            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)(buffer >> bits));
                buffer &= (1 << bits) - 1;
            }
        }
        if (bytes.Count < 10) throw new FormatException("The TOTP secret is too short.");
        return bytes.ToArray();
    }
}

/// <summary>Which code a code form gets from a stored TOTP secret, by how many were already entered.</summary>
public static class TotpDecision
{
    /// <summary>
    /// The current window's code first; after one rejection the adjacent window's (the next one in the second half of
    /// the current window, else the previous one), covering a clock skew of up to one step; then none (ask the user).
    /// </summary>
    public static string? Next(int attempts, TotpSecret secret, DateTimeOffset now) => attempts switch
    {
        0 => secret.CodeAt(now),
        1 => secret.CodeAt(now, now.ToUnixTimeSeconds() % secret.Period >= secret.Period / 2 ? 1 : -1),
        _ => null,
    };
}
