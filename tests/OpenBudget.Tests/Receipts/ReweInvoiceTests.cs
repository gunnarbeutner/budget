using OpenBudget.Connectors.Receipts;
using OpenBudget.Models;
using Xunit;

namespace OpenBudget.Tests.Receipts;

/// <summary>Reading REWE invoices: recognising the mails, the PDF's text and the parsed lines and totals.</summary>
public sealed class ReweInvoiceTests
{
    [Theory]
    [InlineData("Deine Rechnung zu B-PUV-L7B-AD5", true)]
    [InlineData("Deine Rechnung zu B-AAA-BBB-CC1 ", true)]
    [InlineData("Re: Deine Rechnung zu B-AAA-BBB-CC1", false)]
    [InlineData("Deine Bestellung B-AAA-BBB-CC1", false)]
    [InlineData(null, false)]
    public void Recognises_invoice_subjects(string? subject, bool expected) => Assert.Equal(expected, ReweInvoice.IsInvoiceSubject(subject));

    [Theory]
    [InlineData("Rechnung_PN10000000000001.pdf", true)]
    [InlineData("rechnung_pn123.PDF", true)]
    [InlineData("AGB.pdf", false)]
    [InlineData("Rechnung_PN123.pdf.exe", false)]
    public void Recognises_invoice_attachments(string name, bool expected) => Assert.Equal(expected, ReweInvoice.IsInvoiceAttachment(name));

    [Fact]
    public void Pdf_text_comes_out_as_lines_with_plain_spaces()
    {
        var lines = ReceiptPdf.Lines(SyntheticInvoice.Pdf(SyntheticInvoice.StandardLines()));
        Assert.Contains("Beispiel Gouda gerieben 150g 1 B 1,89 € 1,89 €", lines);
        Assert.Contains("Beispiel Banane ca. 200g 958g B 1,99 €/kg 1,91 €", lines);
        Assert.Contains("Seite 1 von 2", lines);
        Assert.DoesNotContain(lines, l => l.Contains(' ', StringComparison.Ordinal));
    }

    [Fact]
    public void Parses_items_totals_and_dates_over_pages()
    {
        var receipt = ReweInvoice.Parse(SyntheticInvoice.Pdf(SyntheticInvoice.StandardLines(), bonus: -1.00m));

        Assert.Equal("PN10000000000001", receipt.InvoiceNumber);
        Assert.Equal("B-AAA-BBB-CC1", receipt.OrderNumber);
        Assert.Equal(new DateOnly(2026, 11, 2), receipt.OrderDate);
        Assert.Equal(new DateOnly(2026, 11, 3), receipt.DeliveryDate);
        Assert.Equal(9, receipt.Items.Count);
        Assert.Equal(1528, receipt.TotalCents);
        Assert.Equal(-100, receipt.BonusCents);
        Assert.Equal(1428, receipt.PaymentCents);

        var banana = receipt.Items.Single(i => i.Name.StartsWith("Beispiel Banane", StringComparison.Ordinal));
        Assert.Equal(("958g", 199L, 191L), (banana.Quantity, banana.UnitCents!.Value, banana.TotalCents));
        var empties = receipt.Items.Single(i => i.Name == "Leergut Glas");
        Assert.True(empties.NoDiscount);
        Assert.Equal((-90L, ReceiptItemKind.Deposit), (empties.TotalCents, empties.Kind));
        Assert.Equal(ReceiptItemKind.Fee, receipt.Items.Single(i => i.Name == "Liefergebühr").Kind);
        Assert.Equal("A/B", receipt.Items.Single(i => i.Name == "Liefergebühr").VatCode);
        Assert.Equal(ReceiptItemKind.Product, receipt.Items[0].Kind);
    }

    [Fact]
    public void Header_fields_next_to_the_address_column_are_read()
    {
        // The PDF's text puts the address and the invoice fields side by side, and "Produkt" below the table header.
        string[] lines =
        [
            "Rechnung",
            "REWE Markt GmbH, Domstr. 20, 50668 Köln",
            "Erika Beispiel Belegnummer PN10000000000002",
            "Beispielweg 1",
            "Deutschland Bestellnummer B-AAA-BBB-CC2",
            "Bestelldatum 01.11.2026",
            "Liefertermin 02.11.2026",
            "Beispielweg 1 Rechnungsdatum 02.11.2026",
            "Menge MwSt. Einzelpreis Gesamt",
            "Produkt",
            "Beispiel Haferflocken 500g 1 B 0,85 € 0,85 €",
            "Beispiel Seife 500ml 1 A 2,49 € 2,49 €",
            "Gesamtsumme 3,34 €",
            "Zahlungsbetrag 3,34 €",
        ];

        var receipt = ReweInvoice.Parse(lines);

        Assert.Equal(("PN10000000000002", "B-AAA-BBB-CC2"), (receipt.InvoiceNumber, receipt.OrderNumber));
        Assert.Equal(new DateOnly(2026, 11, 2), receipt.InvoiceDate);
        Assert.Equal(2, receipt.Items.Count);
    }

    [Fact]
    public void Without_bonus_the_payment_is_the_total()
    {
        var receipt = ReweInvoice.Parse(SyntheticInvoice.Pdf(SyntheticInvoice.StandardLines()));
        Assert.Equal((0L, 1528L), (receipt.BonusCents, receipt.PaymentCents));
    }

    [Fact]
    public void Lines_that_do_not_add_up_are_rejected()
    {
        var pdf = SyntheticInvoice.Pdf(SyntheticInvoice.StandardLines(), totalOverride: 99.99m);
        Assert.Throws<InvalidDataException>(() => ReweInvoice.Parse(pdf));
    }

    [Fact]
    public void Something_else_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => ReweInvoice.Parse(SyntheticInvoice.Build([[["Some other document"]]])));
        Assert.Throws<InvalidDataException>(() => ReweInvoice.Parse([.. "not a pdf"u8.ToArray()]));
    }
}
