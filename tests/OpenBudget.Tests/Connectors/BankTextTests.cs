using System.Text;
using OpenBudget.Connectors;
using OpenBudget.Connectors.FinTs;
using Xunit;

namespace OpenBudget.Tests.Connectors;

public sealed class BankTextTests
{
    /// <summary>UTF-8 bytes of <paramref name="text"/> read as ISO-8859-1, as a FinTS message carries them.</summary>
    private static string Wire(string text) => Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(text));

    private static string Camt(string declaration, string purpose, string name) =>
        declaration + "<Document xmlns=\"urn:iso:std:iso:20022:tech:xsd:camt.052.001.02\"><BkToCstmrAcctRpt><Rpt><Ntry><Amt Ccy=\"EUR\">25.00</Amt><CdtDbtInd>CRDT</CdtDbtInd><Sts>BOOK</Sts>"
        + "<BookgDt><Dt>2026-11-12</Dt></BookgDt><AcctSvcrRef>000100</AcctSvcrRef><NtryDtls><TxDtls><BkTxCd><Prtry><Cd>NTRF</Cd></Prtry></BkTxCd>"
        + $"<RltdPties><Dbtr><Nm>{name}</Nm></Dbtr></RltdPties><RmtInf><Ustrd>{purpose}</Ustrd></RmtInf></TxDtls></NtryDtls></Ntry></Rpt></BkToCstmrAcctRpt></Document>";

    [Theory]
    [InlineData("Ã\u009Cberweisung Miete", "Überweisung Miete")] // "Ü" read as ISO-8859-1
    [InlineData("Ãœberweisung", "Überweisung")] // "Ü" read as Windows-1252
    [InlineData("MÃ¼ller GrÃ¼ÃŸe", "Müller Grüße")]
    [InlineData("Preis 5 â‚¬", "Preis 5 €")]
    public void Double_encoded_text_is_repaired(string broken, string expected) => Assert.Equal(expected, BankText.Repair(broken));

    [Theory]
    [InlineData("Überweisung Müller")]
    [InlineData("SÃO PAULO LTDA")]
    [InlineData("Ã la carte")]
    [InlineData("Straße Ã")]
    [InlineData("Ünal Öztürk")]
    [InlineData("plain ascii")]
    public void Ordinary_text_is_left_alone(string text) => Assert.Equal(text, BankText.Repair(text));

    [Fact]
    public void Transactions_get_name_purpose_and_booking_text_repaired()
    {
        var t = new RemoteTransaction(1m, "EUR", new DateOnly(2026, 11, 12), null, "JÃ¼rgen", "Ã\u009Cberweisung", true, BookingText: "Gutschrift");
        var r = BankText.Repair(t);
        Assert.Equal("Jürgen", r.Name);
        Assert.Equal("Überweisung", r.Purpose);
        Assert.Equal("Gutschrift", r.BookingText);
    }

    [Fact]
    public void Camt_declared_utf8_is_read_as_utf8()
    {
        var xml = Wire(Camt("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", "Überweisung Miete", "Jürgen Weiß"));
        var t = Assert.Single(CamtParser.Parse(xml, bookedDocument: true));
        Assert.Equal("Überweisung Miete", t.Purpose);
        Assert.Equal("Jürgen Weiß", t.Name);
    }

    [Fact]
    public void Camt_without_declaration_is_read_as_utf8()
    {
        var t = Assert.Single(CamtParser.Parse(Wire(Camt("", "Überweisung", "Müller")), bookedDocument: true));
        Assert.Equal("Überweisung", t.Purpose);
        Assert.Equal("Müller", t.Name);
    }

    [Fact]
    public void Camt_declared_latin1_keeps_its_umlauts()
    {
        var xml = Camt("<?xml version=\"1.0\" encoding=\"ISO-8859-1\"?>", "Überweisung", "Müller");
        var t = Assert.Single(CamtParser.Parse(xml, bookedDocument: true));
        Assert.Equal("Überweisung", t.Purpose);
        Assert.Equal("Müller", t.Name);
    }

    [Fact]
    public void Camt_with_latin1_bytes_under_a_utf8_declaration_is_read_as_it_stands()
    {
        var xml = Camt("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", "Überweisung", "Müller");
        var t = Assert.Single(CamtParser.Parse(xml, bookedDocument: true));
        Assert.Equal("Überweisung", t.Purpose);
        Assert.Equal("Müller", t.Name);
    }

    [Fact]
    public void Mt940_with_utf8_inside_the_latin1_wire_is_repaired()
    {
        var mt940 = ":20:STARTUMS\r\n:25:12345678/1234567\r\n:28C:0/1\r\n:60F:C261031EUR1000,00\r\n"
            + ":61:2611121112C25,00NTRFNONREF//006200\r\n:86:166?00" + Wire("Überweisung") + "?20SVWZ+Miete November?32" + Wire("Jürgen Weiß") + "\r\n"
            + ":62F:C261112EUR1025,00\r\n-";
        var t = BankText.Repair(Assert.Single(Mt940Parser.Parse(mt940, "EUR", booked: true)));
        Assert.Equal("Überweisung", t.BookingText);
        Assert.Equal("Jürgen Weiß", t.Name);
        Assert.Equal("Miete November", t.Purpose);
    }
}
