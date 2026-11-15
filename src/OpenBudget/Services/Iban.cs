namespace OpenBudget.Services;

/// <summary>IBAN comparison form.</summary>
public static class Iban
{
    /// <summary>The IBAN without whitespace, upper case; empty for null.</summary>
    public static string Normalize(string? iban)
    {
        if (string.IsNullOrEmpty(iban)) return "";
        return string.Concat(iban.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
    }
}
