using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>The shared text fold, payee key, IBAN form and amount similarity, and stored series keys following the payee key. All data is made up.</summary>
public sealed class TextNormalisationTests
{
    [Theory]
    [InlineData("Größe Übung Ärger", "groesse uebung aerger")]
    [InlineData("Café Olé", "cafe ole")]
    [InlineData("München", "muenchen")]
    [InlineData("STRAẞE", "strasse")]
    public void The_fold_spells_out_umlauts_and_strips_other_accents(string text, string expected) =>
        Assert.Equal(expected, TextFold.Fold(text));

    [Fact]
    public void The_fold_can_upper_case_collapse_whitespace_and_keep_accents()
    {
        Assert.Equal("GROESSE CAFE", TextFold.Fold("größe \t  café", TextCase.Upper, collapseWhitespace: true));
        Assert.Equal("CAFÉ  STRASSE", TextFold.Fold("café  straße", TextCase.Upper, stripAccents: false));
        Assert.Equal("", TextFold.Fold(null));
    }

    [Fact]
    public void Search_and_rule_folds_are_views_of_the_one_fold()
    {
        Assert.Equal("nuernberg cafe", SearchText.Fold("Nürnberg Café"));
        Assert.Equal("NUERNBERG CAFÉ", RuleExpression.Fold("Nürnberg   Café"));
    }

    [Theory]
    [InlineData("Amzn Mktp DE*XYZ123", "amzn mktp de")]
    [InlineData("PAYPAL *STREAMIO", "streamio")]
    [InlineData("SumUp *Cafe Rose", "cafe rose")]
    [InlineData("Example Fitness*ABC", "example fitness")]
    [InlineData("Bäckerei Müller 2026-10 RE4711", "baeckerei mueller")]
    public void A_payee_key_reads_the_descriptor_and_drops_words_with_digits(string raw, string expected) =>
        Assert.Equal(expected, PayeeKey.Of(raw));

    [Fact]
    public void The_payee_falls_back_to_the_purpose()
    {
        Assert.Equal("example club", PayeeKey.Of(" ", "Example Club"));
    }

    [Fact]
    public void A_merchant_drops_stop_words_and_payment_processors()
    {
        Assert.Equal("rewe markt", PayeeKey.Merchant("REWE Markt GmbH"));
        Assert.Equal("ARENA GAMES", PayeeKey.Merchant("PayPal Europe Arena Games", textCase: TextCase.Upper));
        Assert.Contains("klarna", PayeeKey.PaymentProcessors);
        Assert.True(PayeeKey.IsStopWord("LASTSCHRIFT"));
        Assert.True(PayeeKey.IsStopWord("billing"));
    }

    [Theory]
    [InlineData("de89 3704 0044 0532 0130 00", "DE89370400440532013000")]
    [InlineData("DE89\t3704 0044", "DE8937040044")]
    [InlineData(null, "")]
    public void An_iban_loses_whitespace_and_is_upper_case(string? raw, string expected) =>
        Assert.Equal(expected, Iban.Normalize(raw));

    [Theory]
    [InlineData(-1000, -1050, true)]
    [InlineData(-1000, -1053, false)]
    [InlineData(-100, -110, true)]
    [InlineData(-100, -111, false)]
    public void Recurring_amounts_match_within_five_percent_or_ten_cents(long a, long b, bool same) =>
        Assert.Equal(same, AmountSimilarity.Recurring.Same(a, b));

    [Theory]
    [InlineData(-10000, -10100, true)]
    [InlineData(-10000, -10101, false)]
    [InlineData(-50, -51, true)]
    [InlineData(-50, 50, false)]
    [InlineData(0, 0, false)]
    public void Booking_amounts_match_within_one_percent_or_a_cent_of_the_same_sign(long a, long b, bool same) =>
        Assert.Equal(same, AmountSimilarity.Booking.Same(a, b));

    [Fact]
    public async Task Stored_marks_follow_their_series_to_the_current_key()
    {
        await using var t = new TestDb();
        t.AddAccount(10);
        t.AddGroup(1);
        t.AddLeaf(2, 1);
        var bookings = new List<RecurringBooking>();
        for (var i = 0; i < 4; i++)
        {
            var date = new DateOnly(2026, 7 + i, 3);
            t.AddTransaction(500 + i, 2, -1299, date.ToDateTime(TimeOnly.MinValue), accountId: 10, name: "Bäckerei Müller");
            bookings.Add(new RecurringBooking(500 + i, 10, date, -1299, "Bäckerei Müller", null, null, null));
        }
        for (var i = 0; i < 4; i++)
        {
            var date = new DateOnly(2026, 7 + i, 9);
            t.AddTransaction(600 + i, 2, -799, date.ToDateTime(TimeOnly.MinValue), accountId: 10, name: "Streamio Media");
            bookings.Add(new RecurringBooking(600 + i, 10, date, -799, "Streamio Media", null, null, null));
        }
        // Keys as the payee normalisation before TextFold wrote them: accents stripped, so "ä" became "a".
        t.Db.RecurringDismissals.Add(new RecurringDismissal { OwnerUserId = TestDb.DefaultOwner, SeriesKey = "10:backerei muller:500" });
        t.Db.RecurringDismissals.Add(new RecurringDismissal { OwnerUserId = TestDb.DefaultOwner, SeriesKey = "10:streamio media:600", CancelledOn = new DateOnly(2026, 11, 1) });
        t.Db.RecurringDismissals.Add(new RecurringDismissal { OwnerUserId = TestDb.DefaultOwner, SeriesKey = "10:gone:999" });
        await t.Db.SaveChangesAsync();

        await using (var db = await t.System.CreateDbContextAsync())
            await RecurringPaymentService.RekeyMarksAsync(db, default);

        var series = RecurringPaymentDetector.Detect(bookings, [], new HashSet<string>(StringComparer.Ordinal), new DateOnly(2026, 11, 15));
        await using var check = await t.System.CreateDbContextAsync();
        var marks = await check.RecurringDismissals.ToDictionaryAsync(d => d.SeriesKey, d => d.CancelledOn, StringComparer.Ordinal);
        Assert.Equal(3, marks.Count);
        Assert.True(marks.ContainsKey("10:gone:999"));
        Assert.All(series, s => Assert.True(marks.ContainsKey(s.Key), s.Key));
        Assert.Equal(new DateOnly(2026, 11, 1), marks[series.Single(s => s.Payee == "Streamio Media").Key]);
        Assert.Null(marks["10:baeckerei mueller:500"]);
    }
}
