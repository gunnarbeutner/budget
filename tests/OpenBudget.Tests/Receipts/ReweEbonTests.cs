using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.Receipts;
using OpenBudget.Models;
using Xunit;

namespace OpenBudget.Tests.Receipts;

/// <summary>REWE in-store eBons: recognising the mails, parsing the till receipt and matching it with a card payment.</summary>
public sealed class ReweEbonTests
{
    /// <summary>A made-up eBon (invented market, products and numbers) laid out like the till receipt.</summary>
    internal static byte[] Pdf(string payment = "Geg. Mastercard EUR 18,00", string sum = "18,00", bool withId = true, string date = "03.11.2026") =>
        SyntheticInvoice.Build([[
            ["               R E W E"],
            ["            Beispielweg 9"],
            ["            12345 Musterstadt"],
            ["                                   EUR"],
            ["SPECKWUERFEL                     2,49 B"],
            ["LAUCH                            1,38 B"],
            ["             2 Stk x    0,69"],
            ["H-MILCH 3,5%                     2,78 B"],
            ["             2 Stk x    1,39"],
            ["BANANEN                          1,62 B"],
            ["   0,812 kg x    1,99 EUR/kg"],
            ["ORANGENSAFT                      1,99 A"],
            ["RABATT                          -0,20 A"],
            ["PFAND 0,25 EURO                  0,25 A *"],
            ["ZAHNP.PROFI                      4,99 A"],
            ["SPUELM.ZITRONE                   1,29 A"],
            ["LEERGUT                         -0,25 A *"],
            ["TROCKENSHAMPOO                   1,66 A"],
            [" --------------------------------------"],
            [" SUMME                   EUR     " + sum],
            [" ======================================"],
            [" " + payment],
            ["          * *  Kundenbeleg  * *"],
            ["Datum:                        " + date],
            ["Uhrzeit:                    17:44:26 Uhr"],
            ["Betrag EUR                         18,00"],
            .. (withId ? new[] { new[] { "ReceiptID: 000000000000000000000000000001" } } : []),
            [" Steuer  %          Netto        Steuer        Brutto"],
            ["  " + date + "     17:45     Bon-Nr.:1234"],
            ["  Markt:0999     Kasse:2   Bed.:111111"],
        ]]);

    [Theory]
    [InlineData("Dein REWE eBon vom 31.10.2026", true)]
    [InlineData("Dein REWE eBon", false)]
    [InlineData("Deine Rechnung zu B-AAA-BBB-CC1", false)]
    public void Recognises_ebon_subjects(string subject, bool expected) => Assert.Equal(expected, ReweEbon.IsEbonSubject(subject));

    [Fact]
    public void Recognises_the_attachment()
    {
        Assert.True(ReweEbon.IsEbonAttachment("REWE-eBon.pdf"));
        Assert.False(ReweEbon.IsEbonAttachment("Werbung.pdf"));
    }

    [Fact]
    public void Parses_items_quantities_discounts_and_the_card_payment()
    {
        var r = ReweEbon.Parse(Pdf());
        Assert.Equal("000000000000000000000000000001", r.InvoiceNumber);
        Assert.Equal(("Markt 0999 Bon 1234", "REWE Beispielweg 9, 12345 Musterstadt"), (r.OrderNumber, r.Store));
        Assert.Equal(new DateOnly(2026, 11, 3), r.OrderDate);
        Assert.Equal((1800L, 1800L, 0L), (r.TotalCents, r.PaymentCents, r.BonusCents));
        Assert.Equal(10, r.Items.Count);
        Assert.Equal(("2", 69L), (r.Items[1].Quantity, r.Items[1].UnitCents!.Value));
        Assert.Equal(("812g", 199L), (r.Items[3].Quantity, r.Items[3].UnitCents!.Value));
        Assert.Equal(179, r.Items.Single(i => i.Name == "ORANGENSAFT").TotalCents); // the discount is the juice's
        var deposit = r.Items.Single(i => i.Name == "PFAND 0,25 EURO");
        Assert.True(deposit.NoDiscount);
        Assert.Equal(ReceiptItemKind.Deposit, deposit.Kind);
        Assert.Equal(-25, r.Items.Single(i => i.Name == "LEERGUT").TotalCents);
    }

    [Fact]
    public void Abbreviated_drugstore_names_are_recognised()
    {
        Assert.Equal("drugstore", ReceiptCategorizer.Kind("ZAHNP.PROFI"));
        Assert.Equal("household", ReceiptCategorizer.Kind("SPUELM.ZITRONE"));
        Assert.Equal("drugstore", ReceiptCategorizer.Kind("TROCKENSHAMPOO"));
        Assert.Null(ReceiptCategorizer.Kind("H-MILCH 3,5%"));
    }

    [Fact]
    public void Cash_is_not_a_payment_to_match_and_the_number_falls_back_to_market_till_and_receipt()
    {
        var r = ReweEbon.Parse(Pdf(payment: "Geg. BAR EUR 20,00", withId: false));
        Assert.Equal(0, r.PaymentCents);
        Assert.Equal("M0999-K2-B1234-20261103", r.InvoiceNumber);
    }

    [Fact]
    public void Items_named_rewe_decimal_counts_and_a_date_from_the_receipt_line_are_read()
    {
        string[] lines =
        [
            "R E W E", "Beispielweg 9", "12345 Musterstadt", "UID Nr.: DE000000000", "EUR",
            "REWE GOUDA MA 2,69 B",
            "SCHWEINEFILET 5,20 B",
            "0.521 Stk x 9,98",
            "EMMENTALER 45% 1,23 B",
            "0,113 kg x 10,90 EUR/kg",
            "--------------------------------------",
            "SUMME EUR 9,12",
            "Geg. VISA EUR 9,12",
            "29.03.2025 13:38 Bon-Nr.:7372",
            "Markt:0999 Kasse:2 Bed.:000000",
            "REWE Markt GmbH",
        ];
        var r = ReweEbon.Parse(lines);
        Assert.Equal(["REWE GOUDA MA", "SCHWEINEFILET", "EMMENTALER 45%"], r.Items.Select(i => i.Name));
        Assert.Equal(("0.521", 998L), (r.Items[1].Quantity, r.Items[1].UnitCents!.Value));
        Assert.Equal("113g", r.Items[2].Quantity);
        Assert.Equal(new DateOnly(2025, 3, 29), r.OrderDate);
        Assert.Equal("M0999-K2-B7372-20250329", r.InvoiceNumber);
        Assert.Equal((912L, 912L), (r.TotalCents, r.PaymentCents));
    }

    [Fact]
    public void Lines_that_do_not_add_up_are_rejected() =>
        Assert.Throws<InvalidDataException>(() => ReweEbon.Parse(Pdf(sum: "18,01")));

    [Fact]
    public void Matches_a_card_payment_within_five_days_but_never_a_cash_receipt()
    {
        var day = new DateOnly(2026, 11, 3);
        ReceiptBooking Booking(long id, int days, string name) => new(id, day.AddDays(days), -1800, name, null);
        var card = new ReceiptToMatch(1, 1800, day, day, ReceiptShops.ReweEbon);
        Assert.Equal(10, ReceiptMatcher.Match([card], [Booking(10, 2, "PAYBACK")]).Matches[1]);
        Assert.Empty(ReceiptMatcher.Match([card], [Booking(10, 6, "VISA REWE MARKT")]).Matches);
        Assert.Empty(ReceiptMatcher.Match([new ReceiptToMatch(1, 0, day, day, ReceiptShops.ReweEbon)], [Booking(10, 0, "REWE SAGT DANKE")]).Matches);
        // An online invoice does not take a PAYBACK booking.
        Assert.Empty(ReceiptMatcher.Match([card with { Shop = ReceiptShops.Rewe }], [Booking(10, 2, "PAYBACK")]).Matches);
    }

    [Fact]
    public async Task A_stored_ebon_splits_its_card_payment()
    {
        await using var t = new TestDb();
        await ReceiptWorld.SeedAsync(t);
        await ReceiptWorld.AllowAsync(t, ReceiptWorld.Drugstore);
        await ReceiptWorld.AddDebitAsync(t, ReceiptWorld.Id(600), -1800, days: 1);
        var store = new ReceiptStore();
        await using (var db = await t.System.CreateDbContextAsync())
        {
            Assert.True(await ReceiptStore.AddAsync(db, ReceiptWorld.Alice, ReceiptShops.ReweEbon, ReweEbon.Parse(Pdf()), "m", DateTime.UtcNow, default));
            await store.MatchAsync(db, ReceiptWorld.Alice, default);
        }
        await using var check = await t.System.CreateDbContextAsync();
        var parts = await check.Transactions.Where(x => x.SplitParentId == ReceiptWorld.Id(600)).ToListAsync();
        Assert.Equal(-1800, parts.Sum(p => p.Amount));
        Assert.Equal(-794, parts.Single(p => p.CategoryId == ReceiptWorld.Drugstore).Amount);
    }
}
