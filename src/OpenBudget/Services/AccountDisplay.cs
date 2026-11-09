using System.Globalization;

namespace OpenBudget.Services;

/// <summary>Pure display helpers for the account header (IBAN, "last updated").</summary>
public static class AccountDisplay
{
    /// <summary>IBAN in groups of four ("DE00 1234 ..."); null/blank gives null. Non-IBAN text is only regrouped.</summary>
    public static string? FormatIban(string? iban)
    {
        if (string.IsNullOrWhiteSpace(iban)) return null;
        var compact = new string(iban.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        return string.Join(' ', Enumerable.Range(0, (compact.Length + 3) / 4).Select(i => compact.Substring(i * 4, Math.Min(4, compact.Length - i * 4))));
    }

    /// <summary>
    /// Which timestamp a account's "last updated" shows: the connector link's last successful sync when
    /// there is one, otherwise the account's own refresh timestamp (MoneyMoney's last fetch, unix seconds).
    /// </summary>
    public static DateTime? SelectLastUpdated(DateTime? linkLastSuccessUtc, long? refreshTimestamp)
    {
        if (linkLastSuccessUtc is { } l) return DateTime.SpecifyKind(l, DateTimeKind.Utc);
        return refreshTimestamp is > 0 ? DateTimeOffset.FromUnixTimeSeconds(refreshTimestamp.Value).UtcDateTime : null;
    }

    /// <summary>
    /// "Today, 20:10" / "Yesterday, 20:10" / "10/03/2026, 20:10" in <paramref name="zone"/>; the
    /// day words are passed in so callers can localize them.
    /// </summary>
    public static string FormatLastUpdated(DateTime utc, TimeZoneInfo zone, DateTime nowUtc, CultureInfo culture, string today, string yesterday)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone);
        var time = local.ToString("t", culture);
        var day = (nowLocal.Date - local.Date).Days;
        return day switch
        {
            0 => $"{today}, {time}",
            1 => $"{yesterday}, {time}",
            _ => $"{local.ToString("d", culture)}, {time}",
        };
    }
}
