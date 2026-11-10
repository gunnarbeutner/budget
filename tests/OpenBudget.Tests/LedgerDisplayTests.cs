using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>Display helpers of the Browse ledger: German money format, category pill colors, purpose clean-up and when
/// end-of-day balances are shown.</summary>
public sealed class LedgerDisplayTests
{
    [Theory]
    [InlineData(0.0, "0,00 €")]
    [InlineData(1234.56, "1.234,56 €")]
    [InlineData(-1234.56, "−1.234,56 €")]
    [InlineData(-0.5, "−0,50 €")]
    public void FormatMoneyDe_uses_german_grouping_a_real_minus_and_a_trailing_symbol(decimal value, string expected) =>
        Assert.Equal(expected, Formatting.FormatMoneyDe(value));

    [Fact]
    public void FormatMoneyDe_shows_other_currencies_by_code() =>
        Assert.Equal("10,00 USD", Formatting.FormatMoneyDe(10m, "USD"));

    [Fact]
    public void Every_palette_entry_and_derived_pair_is_readable_in_both_themes()
    {
        string?[] own = ["#ff0000", "#00ff00", "#0000ff", "#ffff00", "#000000", "#ffffff", "#7f7f7f", "9,9,9", "250,200,10", "0.2,0.6,0.9", null];
        var pills = CategoryColors.Palette.Select((_, i) => CategoryColors.For(i, null))
            .Concat(own.Select(c => CategoryColors.For(1, c)));
        foreach (var p in pills)
        {
            Assert.True(CategoryColors.Contrast(p.Text, p.Background) >= 4.5, $"light {p.Text} on {p.Background}");
            Assert.True(CategoryColors.Contrast(p.DarkText, p.DarkBackground) >= 4.5, $"dark {p.DarkText} on {p.DarkBackground}");
        }
    }

    [Fact]
    public void A_category_without_color_gets_a_stable_palette_entry_and_a_palette_key_keeps_its_exact_pair()
    {
        Assert.Equal(CategoryColors.For(12345, null), CategoryColors.For(12345, null));
        Assert.Equal(CategoryColors.Palette[2].Background, CategoryColors.For(2, null).Background);
        var (key, bg) = CategoryColors.Palette[4];
        var pill = CategoryColors.For(999, key.ToUpperInvariant());
        Assert.Equal((key, bg), (pill.Text, pill.Background));
        Assert.Equal(CategoryColors.For(7, null), CategoryColors.For(7, "not a color"));
    }

    [Theory]
    [InlineData("#abc", "#aabbcc")]
    [InlineData("12,34,56", "#0c2238")]
    [InlineData("1,0.5,0", "#ff8000")]
    [InlineData("nope", null)]
    [InlineData("", null)]
    public void Stored_colors_are_read_as_hex_or_moneymoney_components(string stored, string? css) =>
        Assert.Equal(css, CategoryColors.ToCss(stored));

    [Theory]
    [InlineData("  Order 123   for\tMarch \n", "Order 123 for March")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void Purpose_text_only_collapses_whitespace(string? raw, string expected) =>
        Assert.Equal(expected, PurposeText.Clean(raw));

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, false, false)]   // no account or account group selected: no running balance
    [InlineData(true, false, false, false)]   // account without a known balance
    [InlineData(true, true, true, false)]     // filter or search: the rows between two days are not all the bookings
    public void End_of_day_balances_are_shown_only_for_an_unfiltered_account_or_account_group(bool oneAccount, bool hasBalance, bool filtered, bool expected) =>
        Assert.Equal(expected, RunningBalance.Applies(oneAccount, hasBalance, filtered));

    [Fact]
    public void End_of_day_balances_walk_back_from_the_current_balance()
    {
        static DateTime At(int day, int hour) => new(2026, 11, day, hour, 0, 0, DateTimeKind.Utc);
        var flows = new[] { (At(2, 8), 3120m), (At(2, 9), -18.44m), (At(5, 7), -950m), (At(5, 8), 59.84m), (At(5, 9), -10.64m), (At(6, 10), -42.17m), (At(6, 11), -10m) };
        var balance = new RunningBalance(2348.12m, flows, d => DateOnly.FromDateTime(d));

        Assert.Equal(2348.12m, balance.AtEndOf(new DateOnly(2026, 11, 6)));
        Assert.Equal(2400.29m, balance.AtEndOf(new DateOnly(2026, 11, 5)));
        Assert.Equal(3301.09m, balance.AtEndOf(new DateOnly(2026, 11, 2)));
        Assert.Equal(199.53m, balance.AtEndOf(new DateOnly(2026, 11, 1)));
    }

    [Fact]
    public void End_of_day_balances_of_an_account_group_are_the_sums_of_its_accounts()
    {
        static DateTime At(int day) => new(2026, 1, day, 9, 0, 0, DateTimeKind.Utc);
        static DateOnly Day(int day) => new(2026, 1, day);
        (DateTime, decimal)[] giro = [(At(10), -100m), (At(14), -25m)];
        (DateTime, decimal)[] savings = [(At(12), 50m)];
        var giroBalance = new RunningBalance(1000m, giro, d => DateOnly.FromDateTime(d));
        var savingsBalance = new RunningBalance(250.50m, savings, d => DateOnly.FromDateTime(d));

        var group = RunningBalance.ForAccounts([(1000m, giro), (250.50m, savings)], d => DateOnly.FromDateTime(d));

        foreach (var d in new[] { 9, 10, 11, 12, 13, 14, 15 })
            Assert.Equal(giroBalance.AtEndOf(Day(d)) + savingsBalance.AtEndOf(Day(d)), group.AtEndOf(Day(d)));
        Assert.Equal(1250.50m, group.AtEndOf(Day(14)));
        Assert.Equal(1325.50m, group.AtEndOf(Day(9)));
    }
}
