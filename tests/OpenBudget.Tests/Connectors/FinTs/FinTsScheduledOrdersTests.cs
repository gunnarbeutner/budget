using System.Globalization;
using System.Text;
using OpenBudget.Connectors;
using OpenBudget.Connectors.FinTs;
using Xunit;

namespace OpenBudget.Tests.Connectors.FinTs;

/// <summary>Standing orders (HICDB) and scheduled transfers (HICSB) as banks answer them, and their pain.001 content. All data is made up.</summary>
public sealed class FinTsScheduledOrdersTests
{
    internal const string OwnIban = "DE00000000000000000011";
    internal const string Pain03 = "urn?:iso?:std?:iso?:20022?:tech?:xsd?:pain.001.001.03";

    /// <summary>A pain.001.001.03 document with one transfer per entry, as the bank stores the order (UTF-8).</summary>
    internal static string Pain(string date, params (string Amount, string Name, string Iban, string? Bic, string Purpose)[] transfers)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Document xmlns=\"urn:iso:std:iso:20022:tech:xsd:pain.001.001.03\"><CstmrCdtTrfInitn>");
        sb.Append("<GrpHdr><MsgId>M1</MsgId><CreDtTm>2026-01-01T10:00:00</CreDtTm><NbOfTxs>1</NbOfTxs><InitgPty><Nm>Test Owner</Nm></InitgPty></GrpHdr>");
        sb.Append(CultureInfo.InvariantCulture, $"<PmtInf><PmtInfId>P1</PmtInfId><PmtMtd>TRF</PmtMtd><ReqdExctnDt>{date}</ReqdExctnDt><Dbtr><Nm>Test Owner</Nm></Dbtr><DbtrAcct><Id><IBAN>{OwnIban}</IBAN></Id></DbtrAcct>");
        foreach (var t in transfers)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<CdtTrfTxInf><PmtId><EndToEndId>NOTPROVIDED</EndToEndId></PmtId><Amt><InstdAmt Ccy=\"EUR\">{t.Amount}</InstdAmt></Amt>");
            if (t.Bic != null) sb.Append(CultureInfo.InvariantCulture, $"<CdtrAgt><FinInstnId><BIC>{t.Bic}</BIC></FinInstnId></CdtrAgt>");
            sb.Append(CultureInfo.InvariantCulture, $"<Cdtr><Nm>{t.Name}</Nm></Cdtr><CdtrAcct><Id><IBAN>{t.Iban}</IBAN></Id></CdtrAcct><RmtInf><Ustrd>{t.Purpose}</Ustrd></RmtInf></CdtTrfTxInf>");
        }
        return sb.Append("</PmtInf></CstmrCdtTrfInitn></Document>").ToString();
    }

    /// <summary>The binary data element for a document: its UTF-8 bytes, one char per byte.</summary>
    internal static string Bin(string xml) => FinTsWire.Binary(Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(xml)));

    private static readonly DateOnly Today = new(2026, 11, 12);

    [Fact]
    public void Standing_orders_are_read_with_their_rhythm_and_next_execution()
    {
        // Two orders as one bank message: monthly rent on the 1st (no end), quarterly insurance on the 15th until 2027.
        var wire =
            $"HICDB:4:1:3+{OwnIban}:TESTDEFFXXX+{Pain03}+{Bin(Pain("2024-01-01", ("850.00", "Example Landlord", "DE00000000000000000099", "TESTDEFF001", "Miete Wohnung 3")))}+ORDER-1+20240101:M:1:1'"
            + $"HICDB:5:1:3+{OwnIban}:TESTDEFFXXX+{Pain03}+{Bin(Pain("2025-02-15", ("120.50", "Example Insurance Größe", "DE00000000000000000077", null, "Beitrag Hausrat")))}+ORDER-2+20250215:M:3:15:20270815'";
        var orders = FinTsScheduledOrders.ReadStandingOrders(FinTsWire.Parse(wire), Today);

        Assert.Equal(2, orders.Count);
        var rent = orders[0];
        Assert.Equal(ScheduledPaymentKind.Standing, rent.Kind);
        Assert.Equal("ORDER-1", rent.BankOrderId);
        Assert.Equal(-850.00m, rent.Amount);
        Assert.Equal("EUR", rent.Currency);
        Assert.Equal("Example Landlord", rent.CounterpartyName);
        Assert.Equal("DE00000000000000000099", rent.CounterpartyIban);
        Assert.Equal("TESTDEFF001", rent.CounterpartyBic);
        Assert.Equal("Miete Wohnung 3", rent.Purpose);
        Assert.Equal((ScheduleUnit.Month, 1, 1), (rent.IntervalUnit!.Value, rent.IntervalCount!.Value, rent.ExecutionDay!.Value));
        Assert.Equal(new DateOnly(2024, 1, 1), rent.FirstDate);
        Assert.Null(rent.LastDate);
        Assert.Equal(new DateOnly(2026, 12, 1), rent.NextDate);

        var insurance = orders[1];
        Assert.Equal("Example Insurance Größe", insurance.CounterpartyName); // UTF-8 in the binary element
        Assert.Null(insurance.CounterpartyBic);
        Assert.Equal(new DateOnly(2027, 8, 15), insurance.LastDate);
        Assert.Equal(new DateOnly(2026, 11, 15), insurance.NextDate); // Feb, May, Aug, Nov: the 15th of November is still ahead
    }

    [Fact]
    public void Ended_standing_orders_are_left_out()
    {
        var wire = $"HICDB:4:1:3+{OwnIban}+{Pain03}+{Bin(Pain("2025-01-05", ("10.00", "Example Club", "DE00000000000000000055", null, "Beitrag")))}+ORDER-3+20250105:M:1:5:20251005'";
        Assert.Empty(FinTsScheduledOrders.ReadStandingOrders(FinTsWire.Parse(wire), Today));
    }

    [Fact]
    public void Scheduled_transfers_take_the_execution_date_from_the_document()
    {
        var wire =
            $"HICSB:4:1:3+{OwnIban}+{Pain03}+{Bin(Pain("2026-12-24", ("49.99", "Example Shop", "DE00000000000000000033", "TESTDEFF002", "Bestellung 4711")))}+T-1+20261224'"
            + $"HICSB:5:1:3+{OwnIban}+{Pain03}+{Bin(Pain("2026-10-01", ("5.00", "Old", "DE00000000000000000034", null, "already executed")))}+T-2'";
        var transfer = Assert.Single(FinTsScheduledOrders.ReadScheduledTransfers(FinTsWire.Parse(wire), Today));
        Assert.Equal(ScheduledPaymentKind.Scheduled, transfer.Kind);
        Assert.Equal("T-1", transfer.BankOrderId);
        Assert.Equal(new DateOnly(2026, 12, 24), transfer.NextDate);
        Assert.Equal(-49.99m, transfer.Amount);
        Assert.Null(transfer.IntervalUnit);
    }

    [Fact]
    public void Pain_version_09_with_a_dated_group_and_bicfi_is_read()
    {
        const string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Document xmlns=\"urn:iso:std:iso:20022:tech:xsd:pain.001.001.09\"><CstmrCdtTrfInitn>"
            + "<PmtInf><ReqdExctnDt><Dt>2027-01-04</Dt></ReqdExctnDt><CdtTrfTxInf><Amt><InstdAmt Ccy=\"EUR\">300</InstdAmt></Amt>"
            + "<CdtrAgt><FinInstnId><BICFI>TESTDEFF003</BICFI></FinInstnId></CdtrAgt><Cdtr><Nm>Example Savings</Nm></Cdtr>"
            + "<CdtrAcct><Id><IBAN>DE00000000000000000022</IBAN></Id></CdtrAcct><RmtInf><Ustrd>Sparen</Ustrd></RmtInf></CdtTrfTxInf></PmtInf></CstmrCdtTrfInitn></Document>";
        var t = Assert.Single(Pain001Parser.Parse(Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(xml))));
        Assert.Equal(new DateOnly(2027, 1, 4), t.ExecutionDate);
        Assert.Equal(300m, t.Amount);
        Assert.Equal("TESTDEFF003", t.Bic);
    }

    [Fact]
    public void An_unreadable_document_is_a_protocol_error()
    {
        Assert.Throws<FinTsProtocolException>(() => Pain001Parser.Parse("<Document><unclosed>"));
    }
}

/// <summary>The execution dates of standing orders.</summary>
public sealed class PaymentScheduleTests
{
    private static List<DateOnly> Dates(DateOnly first, ScheduleUnit? unit, int? interval, int? day, DateOnly? last, DateOnly from, DateOnly to) =>
        PaymentSchedule.Occurrences(first, unit, interval, day, last, from, to).ToList();

    [Fact]
    public void Monthly_on_the_31st_falls_back_to_the_last_day_of_short_months()
    {
        var dates = Dates(new(2026, 1, 31), ScheduleUnit.Month, 1, 31, null, new(2026, 1, 1), new(2026, 4, 30));
        Assert.Equal([new(2026, 1, 31), new(2026, 2, 28), new(2026, 3, 31), new(2026, 4, 30)], dates);
    }

    [Fact]
    public void Quarterly_keeps_the_cycle_of_the_first_execution_and_stops_at_the_last()
    {
        var dates = Dates(new(2025, 2, 15), ScheduleUnit.Month, 3, 15, new(2027, 2, 15), new(2026, 6, 1), new(2027, 12, 31));
        Assert.Equal([new(2026, 8, 15), new(2026, 11, 15), new(2027, 2, 15)], dates);
    }

    [Fact]
    public void Ultimo_codes_count_back_from_the_month_end()
    {
        Assert.Equal([new(2026, 2, 28)], Dates(new(2026, 1, 1), ScheduleUnit.Month, 1, PaymentSchedule.Ultimo, null, new(2026, 2, 1), new(2026, 2, 28)));
        Assert.Equal([new(2026, 2, 27)], Dates(new(2026, 1, 1), ScheduleUnit.Month, 1, PaymentSchedule.UltimoMinus1, null, new(2026, 2, 1), new(2026, 2, 28)));
        Assert.Equal([new(2026, 2, 26)], Dates(new(2026, 1, 1), ScheduleUnit.Month, 1, PaymentSchedule.UltimoMinus2, null, new(2026, 2, 1), new(2026, 2, 28)));
    }

    [Fact]
    public void Every_two_weeks_on_friday()
    {
        // 2026-11-06 is a Friday.
        var dates = Dates(new(2026, 11, 6), ScheduleUnit.Week, 2, 5, null, new(2026, 11, 10), new(2026, 12, 31));
        Assert.Equal([new(2026, 11, 20), new(2026, 12, 4), new(2026, 12, 18)], dates);
    }

    [Fact]
    public void Without_a_rhythm_only_the_date_itself_counts()
    {
        Assert.Equal([new(2026, 12, 24)], Dates(new(2026, 12, 24), null, null, null, null, new(2026, 11, 1), new(2026, 12, 31)));
        Assert.Empty(Dates(new(2026, 12, 24), null, null, null, null, new(2027, 1, 1), new(2027, 12, 31)));
    }

    [Fact]
    public void Next_is_null_once_the_order_has_ended()
    {
        Assert.Null(PaymentSchedule.Next(new(2025, 1, 5), ScheduleUnit.Month, 1, 5, new(2025, 10, 5), new(2026, 11, 12)));
        Assert.Equal(new DateOnly(2026, 12, 5), PaymentSchedule.Next(new(2025, 1, 5), ScheduleUnit.Month, 1, 5, null, new(2026, 11, 12)));
    }
}
