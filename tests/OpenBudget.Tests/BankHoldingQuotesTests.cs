using OpenBudget.Models;
using OpenBudget.Services.Quotes;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>Bank-depot holdings priced as of their depot's refresh and brought up to newer stored closes.</summary>
public class BankHoldingQuotesTests
{
    private const long Day10 = 19732;   // 2024-01-10
    private const long Day8 = Day10 - 2;

    private static Holding Bank(string isin = "IE00B5BMR087", double price = 100, string cur = "EUR") => new()
    {
        Id = 1,
        OwnerUserId = TestDb.DefaultOwner,
        AccountId = 20,
        Isin = isin,
        Quantity = 3,
        Price = price,
        PriceCurrency = cur,
        Amount = 3 * price,
        AmountCurrency = cur,
        AssetClass = 6,
    };

    private static readonly Dictionary<long, long> Refreshed = new() { [20] = Day8 * 86400 + 3600 * 15 };
    private static readonly Dictionary<string, string> Symbols = new(StringComparer.OrdinalIgnoreCase) { ["IE00B5BMR087"] = "SXR8.DE" };

    private static MarketQuote Close(long day, double close, string cur = "EUR") => new() { Symbol = "SXR8.DE", Day = day, Close = close, Currency = cur };

    [Fact]
    public void A_newer_close_replaces_the_banks_price_and_scales_the_value()
    {
        var h = Bank();
        var quotable = BankHoldingQuotes.Apply([h], Refreshed, Symbols, [Close(Day10, 102)]);
        Assert.Contains(h, quotable);
        Assert.Equal((102.0, 306.0, 3.0, Day10 * 86400), (h.Price!.Value, h.Amount!.Value, h.Quantity, h.PriceTime!.Value));
    }

    [Fact]
    public void Without_a_newer_close_the_holding_is_priced_as_of_its_depots_refresh_day()
    {
        var h = Bank();
        BankHoldingQuotes.Apply([h], Refreshed, Symbols, [Close(Day8, 95)]);
        Assert.Equal((100.0, Day8 * 86400), (h.Price!.Value, h.PriceTime!.Value));

        var unresolved = Bank(isin: "XX0000000000");
        Assert.Empty(BankHoldingQuotes.Apply([unresolved], Refreshed, Symbols, [Close(Day10, 102)]));
        Assert.Equal((100.0, Day8 * 86400), (unresolved.Price!.Value, unresolved.PriceTime!.Value));
    }

    [Fact]
    public void A_close_in_another_currency_or_far_from_the_banks_price_is_not_taken()
    {
        var usd = Bank(cur: "USD");
        BankHoldingQuotes.Apply([usd], Refreshed, Symbols, [Close(Day10, 102)]);
        Assert.Equal(100.0, usd.Price);

        var units = Bank();
        BankHoldingQuotes.Apply([units], Refreshed, Symbols, [Close(Day10, 10200)]);
        Assert.Equal((100.0, Day8 * 86400), (units.Price!.Value, units.PriceTime!.Value));
    }

    [Fact]
    public void PortfolioPerformance_holdings_are_left_alone()
    {
        var pp = Bank();
        pp.PpSecurityUuid = "sec";
        Assert.Empty(BankHoldingQuotes.Apply([pp], Refreshed, Symbols, [Close(Day10, 102)]));
        Assert.Null(pp.PriceTime);
    }

    [Fact]
    public async Task The_holdings_view_uses_the_newer_close_and_dates_its_prices_by_the_oldest_holding()
    {
        await using var t = new TestDb();
        t.AddProject(1);
        var depot = t.AddAccount(20, "Broker");
        depot.Type = 7;
        depot.RefreshTimestamp = Day8 * 86400 + 3600;
        var other = t.AddAccount(21, "Other broker");
        other.Type = 7;
        other.RefreshTimestamp = (Day8 - 3) * 86400;
        t.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = 1, AccountId = 20 });
        t.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = 1, AccountId = 21 });
        t.Db.Holdings.Add(Bank());
        var stale = Bank(isin: "DE0000000001");
        stale.Id = 2;
        stale.AccountId = 21;
        t.Db.Holdings.Add(stale);
        t.Db.PpSecurities.Add(new PpSecurity { OwnerUserId = TestDb.DefaultOwner, Uuid = "u1", Isin = "IE00B5BMR087", TickerSymbol = "SXR8.DE" });
        t.Db.MarketQuotes.Add(Close(Day10, 110));
        t.Db.MarketQuotes.Add(Close(Day10 - 1, 105));
        await t.Db.SaveChangesAsync();

        var view = (await t.Portfolio.GetProjectHoldingsAsync(1))!;

        Assert.Equal(330m + 300m, view.TotalValue);
        Assert.Equal(110m, view.Rows.Single(r => r.Isin == "IE00B5BMR087").Price);
        Assert.Equal(DateOnly.FromDayNumber((int)(Day8 - 3) + 719162), view.PricesAsOf);
        Assert.Equal(TestDb.DefaultOwner, view.QuoteOwnerId);
    }
}
