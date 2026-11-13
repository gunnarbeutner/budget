namespace OpenBudget.Services;

/// <summary>
/// Totals of bookings that may be in different currencies. Amounts are never converted or added across currencies:
/// each currency keeps its own total, and the text lists them one after another ("−175,50 € + 190,94 USD").
/// Pure: no DB access.
/// </summary>
public static class CurrencySums
{
    /// <summary>The ISO code a booking or account currency stands for; a missing one means EUR.</summary>
    public static string Normalize(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? "EUR" : currency.Trim().ToUpperInvariant();

    /// <summary>One total per currency, the base currency (EUR when none is given) first and the others alphabetically.</summary>
    public static List<CurrencyAmount> Group(IEnumerable<(string? Currency, decimal Amount)> amounts, string? baseCurrency = null) =>
        Order(amounts.GroupBy(a => Normalize(a.Currency), StringComparer.Ordinal)
            .Select(g => new CurrencyAmount(g.Key, g.Sum(a => a.Amount))), baseCurrency);

    /// <summary>The totals with the base currency (EUR when none is given) first and the others alphabetically.</summary>
    public static List<CurrencyAmount> Order(IEnumerable<CurrencyAmount> sums, string? baseCurrency = null)
    {
        var first = Normalize(baseCurrency);
        return sums.OrderBy(s => Normalize(s.Currency) == first ? 0 : 1)
            .ThenBy(s => Normalize(s.Currency), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The totals as text: each currency formatted on its own; later totals are joined with " + " or, when negative,
    /// " − " and their absolute amount ("−175,50 € − 12,50 USD"). No totals at all read as zero in the base currency.
    /// </summary>
    public static string Format(IEnumerable<CurrencyAmount> sums, string? baseCurrency = null)
    {
        var ordered = Order(sums, baseCurrency);
        if (ordered.Count == 0) return Formatting.FormatMoneyDe(0m, Normalize(baseCurrency));
        var text = new System.Text.StringBuilder(Formatting.FormatMoneyDe(ordered[0].Amount, Normalize(ordered[0].Currency)));
        foreach (var s in ordered.Skip(1))
            text.Append(s.Amount < 0m ? " − " : " + ").Append(Formatting.FormatMoneyDe(Math.Abs(s.Amount), Normalize(s.Currency)));
        return text.ToString();
    }

    /// <summary>Whether every total is below zero (the text then reads as negative).</summary>
    public static bool AllNegative(IReadOnlyCollection<CurrencyAmount> sums) => sums.Count > 0 && sums.All(s => s.Amount < 0m);
}
