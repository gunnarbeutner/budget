using System.Security.Cryptography;

namespace OpenBudget.Auth;

internal static class TokenGenerator
{
    /// <summary>A random initial password that satisfies Identity's default password policy.</summary>
    public static string NewPassword()
    {
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string digits = "23456789";
        const string symbols = "-_.!#$%";
        const string all = lower + upper + digits + symbols;

        var chars = new List<char>
        {
            lower[RandomNumberGenerator.GetInt32(lower.Length)],
            upper[RandomNumberGenerator.GetInt32(upper.Length)],
            digits[RandomNumberGenerator.GetInt32(digits.Length)],
            symbols[RandomNumberGenerator.GetInt32(symbols.Length)],
        };
        while (chars.Count < 20) chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);

        // Fisher-Yates so the guaranteed classes don't sit at fixed positions.
        for (int i = chars.Count - 1; i > 0; i--)
        {
            int j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars.ToArray());
    }
}
