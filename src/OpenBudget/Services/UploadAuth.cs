using System.Security.Cryptography;
using System.Text;

namespace OpenBudget.Services;

/// <summary>
/// Bearer-token check for the MoneyMoney DB upload/delete endpoints. The comparison is
/// constant-time: a plain <c>string.Equals</c> short-circuits on the first differing byte, which
/// leaks how much of the token a guess got right through response timing.
/// </summary>
internal static class UploadAuth
{
    private const string BearerPrefix = "Bearer ";

    /// <summary>
    /// True when <paramref name="authorizationHeader"/> carries exactly the expected bearer token.
    /// Returns false when the expected token is unset, or the header is missing/malformed.
    /// </summary>
    public static bool IsAuthorized(string? authorizationHeader, string? expectedToken)
    {
        if (string.IsNullOrEmpty(expectedToken)) return false;
        if (string.IsNullOrEmpty(authorizationHeader)) return false;
        if (!authorizationHeader.StartsWith(BearerPrefix, StringComparison.Ordinal)) return false;

        var presentedBytes = Encoding.UTF8.GetBytes(authorizationHeader[BearerPrefix.Length..]);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedToken);
        return CryptographicOperations.FixedTimeEquals(presentedBytes, expectedBytes);
    }
}
