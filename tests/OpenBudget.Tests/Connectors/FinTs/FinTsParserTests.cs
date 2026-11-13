using OpenBudget.Connectors;
using OpenBudget.Connectors.FinTs;
using Xunit;

namespace OpenBudget.Tests.Connectors.FinTs;

public sealed class FinTsParserTests
{
    private static FinTsBankData DataOf(string bank)
    {
        var first = FinTsResponse.Parse(FinTsFixture.Load(bank).BankMessages.First());
        return FinTsBankData.Create(FinTsBankData.ExtractBpd(first.Segments) ?? [], FinTsBankData.ExtractUpd(first.Segments) ?? []);
    }

    [Fact]
    public void Camt_booked_and_pending_entries_come_from_the_right_documents()
    {
        var response = FinTsResponse.Parse(FinTsFixture.Load("ing").BankMessages.ElementAt(1));
        var hicaz = response.Of("HICAZ").Single();
        var booked = CamtParser.Parse(hicaz.Element(2), bookedDocument: true);
        var pending = CamtParser.Parse(hicaz.Element(3), bookedDocument: false);

        Assert.Equal(2, booked.Count);
        Assert.Equal(-42.50m, booked[0].Amount);
        Assert.Equal(new DateOnly(2026, 11, 3), booked[0].BookingDate);
        Assert.Equal("Example Grocer", booked[0].Name);
        Assert.Equal("Example purchase 1", booked[0].Purpose);
        Assert.Equal("TESTREF1", booked[0].BankTransactionId);
        Assert.Equal(1500.00m, booked[1].Amount);
        Assert.Equal("Example Employer", booked[1].Name);
        Assert.All(booked, t => Assert.True(t.Booked));

        var p = Assert.Single(pending);
        Assert.False(p.Booked);
        Assert.Equal(-9.99m, p.Amount);
        Assert.Null(p.BankTransactionId);
    }

    [Fact]
    public void Camt_reads_newer_versions_and_reversals()
    {
        const string xml = "<Document xmlns=\"urn:iso:std:iso:20022:tech:xsd:camt.052.001.08\"><BkToCstmrAcctRpt><Rpt><Ntry><Amt Ccy=\"USD\">5.00</Amt><CdtDbtInd>DBIT</CdtDbtInd><RvslInd>true</RvslInd><Sts><Cd>BOOK</Cd></Sts><BookgDt><DtTm>2026-02-03T10:00:00</DtTm></BookgDt><AcctSvcrRef>NOTPROVIDED</AcctSvcrRef><NtryDtls><TxDtls><RltdPties><Dbtr><Pty><Nm>Payer</Nm></Pty></Dbtr></RltdPties><RmtInf><Ustrd>one</Ustrd><Ustrd>two</Ustrd></RmtInf></TxDtls></NtryDtls></Ntry></Rpt></BkToCstmrAcctRpt></Document>";
        var t = Assert.Single(CamtParser.Parse(xml, bookedDocument: true));
        Assert.Equal(5.00m, t.Amount);
        Assert.Equal("USD", t.Currency);
        Assert.Equal("one two", t.Purpose);
        Assert.Null(t.BankTransactionId);
        Assert.Equal(new DateOnly(2026, 2, 3), t.BookingDate);
    }

    [Fact]
    public void Mt940_reads_sepa_tags_counterparty_and_bank_codes()
    {
        const string mt940 = ":20:STARTUMS\r\n:25:12345678/1234567\r\n:28C:0/1\r\n:60F:C261031EUR1000,00\r\n"
            + ":61:2611031103D12,30NMSCREF123//BANKREF9\r\n:86:105?00SEPA-Lastschrift?109310?20EREF+E2E-77?21MREF+M-0001?22CRED+DE00ZZZ0000001?23SVWZ+Monthly fee?30EXAMPLEXXXX?31DE00123456780000000001?32Example Utility?34MD06\r\n"
            + ":62F:C261103EUR987,70\r\n-";
        var t = Assert.Single(Mt940Parser.Parse(mt940, "EUR", booked: true));
        Assert.Equal("Monthly fee", t.Purpose);
        Assert.Equal("SEPA-Lastschrift", t.BookingText);
        Assert.Equal("DE00123456780000000001", t.CounterpartyIban);
        Assert.Equal("EXAMPLEXXXX", t.CounterpartyBic);
        Assert.NotNull(t.Sepa);
        Assert.Equal("E2E-77", t.Sepa!.EndToEndReference);
        Assert.Equal("M-0001", t.Sepa.MandateReference);
        Assert.Equal("DE00ZZZ0000001", t.Sepa.CreditorId);
        Assert.Equal("105", t.Sepa.TransactionCode);
        Assert.Equal("9310", t.Sepa.Primanota);
        Assert.Equal("MD06", t.Sepa.ReturnReason);
    }

    [Fact]
    public void Camt_reads_sepa_references_and_counterparty_account()
    {
        const string xml = "<Document xmlns=\"urn:iso:std:iso:20022:tech:xsd:camt.052.001.08\"><BkToCstmrAcctRpt><Rpt><Ntry><Amt Ccy=\"EUR\">9.99</Amt><CdtDbtInd>DBIT</CdtDbtInd><Sts><Cd>BOOK</Cd></Sts><BookgDt><Dt>2026-02-03</Dt></BookgDt>"
            + "<NtryDtls><TxDtls><Refs><EndToEndId>E2E-5</EndToEndId><MndtId>M-0002</MndtId></Refs><BkTxCd><Prtry><Cd>NDDT+105+9310</Cd></Prtry></BkTxCd>"
            + "<RltdPties><Cdtr><Pty><Nm>Example Streaming</Nm></Pty></Cdtr><CdtrAcct><Id><IBAN>DE00123456780000000002</IBAN></Id></CdtrAcct><CdtrSchmeId><Id><PrvtId><Othr><Id>DE00ZZZ0000003</Id></Othr></PrvtId></Id></CdtrSchmeId></RltdPties>"
            + "<RltdAgts><CdtrAgt><FinInstnId><BICFI>EXAMPLEXXXX</BICFI></FinInstnId></CdtrAgt></RltdAgts><RmtInf><Ustrd>Subscription</Ustrd></RmtInf><AddtlTxInf>SEPA-Lastschrift</AddtlTxInf></TxDtls></NtryDtls></Ntry></Rpt></BkToCstmrAcctRpt></Document>";
        var t = Assert.Single(CamtParser.Parse(xml, bookedDocument: true));
        Assert.Equal("Example Streaming", t.Name);
        Assert.Equal("DE00123456780000000002", t.CounterpartyIban);
        Assert.Equal("EXAMPLEXXXX", t.CounterpartyBic);
        Assert.Equal("SEPA-Lastschrift", t.BookingText);
        Assert.Equal("E2E-5", t.Sepa!.EndToEndReference);
        Assert.Equal("M-0002", t.Sepa.MandateReference);
        Assert.Equal("DE00ZZZ0000003", t.Sepa.CreditorId);
        Assert.Equal("NDDT+105+9310", t.Sepa.TransactionCode);
    }

    [Fact]
    public void Camt_with_a_doctype_is_refused()
    {
        Assert.Throws<FinTsProtocolException>(() => CamtParser.Parse("<!DOCTYPE x [<!ENTITY a \"b\">]><Document/>", true));
    }

    [Fact]
    public void Mt940_entries_are_read_with_name_purpose_sign_and_reference()
    {
        const string mt940 = ":20:STARTUMS\r\n:25:12345678/1234567\r\n:28C:0/1\r\n:60F:C261031EUR1000,00\r\n"
            + ":61:2611031103D12,30NMSCREF123//BANKREF9\r\n:86:177?00SEPA-Lastschrift?10931?20EREF+E1?21SVWZ+Cheap?22 stuff here?32Example Shop\r\n"
            + ":61:261105C1500,00NTRFNONREF\r\n:86:166?00Gutschrift?20Salary ?21November?32Example Employer\r\n"
            + ":62F:C261105EUR2487,70\r\n-";
        var list = Mt940Parser.Parse(mt940, "EUR", booked: true);
        Assert.Equal(2, list.Count);
        Assert.Equal(-12.30m, list[0].Amount);
        Assert.Equal(new DateOnly(2026, 11, 3), list[0].BookingDate);
        Assert.Equal("Example Shop", list[0].Name);
        Assert.Equal("Cheap stuff here", list[0].Purpose);
        Assert.Equal("BANKREF9", list[0].BankTransactionId);
        Assert.Equal(1500.00m, list[1].Amount);
        Assert.Equal("Salary November", list[1].Purpose);
        Assert.Null(list[1].BankTransactionId);
        Assert.All(list, t => Assert.Equal("EUR", t.Currency));
    }

    [Fact]
    public void Mt535_positions_have_isin_quantity_price_and_value()
    {
        const string mt535 = ":16R:GENL\r\n:28E:1/ONLY\r\n:16S:GENL\r\n:16R:FIN\r\n:35B:ISIN XX0000000001/DE/A0EXAM\r\nExample World ETF\r\n"
            + ":90B::MRKT//ACTU/EUR100,25\r\n:93B::AGGR//UNIT/10,5\r\n:19A::HOLD//EUR1052,63\r\n:16S:FIN\r\n"
            + ":16R:FIN\r\n:35B:ISIN XX0000000002\r\nExample Bond Fund\r\n:90A::MRKT//PRCT/101,5\r\n:93B::AGGR//FAMT/4000,\r\n:19A::HOLD//EUR4060,\r\n:16S:FIN\r\n-";
        var list = Mt535Parser.Parse(mt535);
        Assert.Equal(2, list.Count);
        Assert.Equal(("XX0000000001", "A0EXAM", "Example World ETF", 10.5m, 100.25m, "EUR", 1052.63m), (list[0].Isin, list[0].Wkn, list[0].Name, list[0].Quantity, list[0].Price, list[0].PriceCurrency, list[0].Value));
        Assert.Equal(("XX0000000002", 4000m, 4060m), (list[1].Isin, list[1].Quantity, list[1].Value));
    }

    [Fact]
    public void Balance_reads_booked_and_pending()
    {
        var hisal = FinTsWire.Parse("HISAL:4:5:3+9000002::280:50010517+Girokonto+EUR+C:1234,56:EUR:20261109+D:10,:EUR:20261109'").Single();
        var balance = FinTsBalance.FromSegment(hisal)!;
        Assert.Equal(1234.56m, balance.Booked);
        Assert.Equal(-10m, balance.WithPending);
    }

    [Fact]
    public void Ing_parameters_list_three_accounts_and_one_step_free_methods()
    {
        var data = DataOf("ing");
        Assert.Equal(9, data.BpdVersion);
        Assert.Equal(3, data.Accounts.Count);
        Assert.Equal([RemoteAccountType.Giro, RemoteAccountType.Savings, RemoteAccountType.Giro], data.Accounts.Select(a => a.Kind));
        Assert.Equal(5, data.BestVersion("HKSAL", [5, 6, 7]));
        Assert.Equal(1, data.BestVersion("HKCAZ", [1]));
        Assert.Equal(6, data.BestVersion("HKWPD", [5, 6]));
        Assert.False(data.NeedsTan("HKCAZ"));
        Assert.Null(data.BestVersion("HKEKA", [3, 4, 5]));
        Assert.Empty(data.Methods); // the single ING method comes as an old HITANS v1, which has no HKTAN
    }

    [Fact]
    public void Baader_parameters_have_two_tan_methods_with_the_right_hktan_versions()
    {
        var data = DataOf("baader");
        Assert.Equal(7, data.BestVersion("HKSAL", [5, 6, 7]));
        Assert.True(data.NeedsTan("HKCAZ"));
        Assert.False(data.NeedsTan("HKWPD"));
        var sms = data.Methods.Single(m => m.Id == "930");
        var app = data.Methods.Single(m => m.Id == "940");
        Assert.Equal((6, false, 6, true), (sms.HktanVersion, sms.Decoupled, sms.MaxInputLength, sms.Numeric));
        Assert.Equal((7, true, 999), (app.HktanVersion, app.Decoupled, app.MaxPolls));
        Assert.Equal("BDWBDEMMXXX", data.Accounts[0].Bic);
        Assert.Equal(RemoteAccountType.Giro, data.Accounts[0].Kind);
        Assert.Equal(RemoteAccountType.Depot, data.Accounts[1].Kind);
        Assert.Null(data.Accounts[1].Iban);
        Assert.Equal("9000011", data.Accounts[1].RemoteId);
    }

    [Fact]
    public void Psd_upd_carries_the_inventory_marker_per_account()
    {
        var data = DataOf("psd");
        Assert.Equal(3, data.Accounts.Count);
        Assert.Equal(42, data.UpdVersion);
        Assert.Equal(["2026-06-27-03.10.51.000453", "2025-12-03-16.16.10.000743", "2026-01-05-19.52.44.000423"], data.Accounts.Select(a => a.Inventory));
        Assert.All(data.Accounts, a => Assert.True(a.Allows("HKEKA")));
        Assert.Equal([RemoteAccountType.Savings, RemoteAccountType.Other, RemoteAccountType.Savings], data.Accounts.Select(a => a.Kind));
    }

    [Fact]
    public void Bpd_and_upd_survive_being_cached_as_text()
    {
        var data = DataOf("baader");
        var again = FinTsBankData.Create(FinTsWire.Parse(FinTsWire.Write(data.BpdSegments)), FinTsWire.Parse(FinTsWire.Write(data.UpdSegments)));
        Assert.Equal(data.Methods, again.Methods);
        Assert.Equal(data.Accounts.Select(a => a.RemoteId), again.Accounts.Select(a => a.RemoteId));
        Assert.Equal(data.BpdVersion, again.BpdVersion);
    }

    [Fact]
    public void Mt940_free_text_keeps_word_breaks_between_the_banks_lines()
    {
        const string mt940 = ":20:STARTUMS\r\n:25:12345678/1234567\r\n:28C:0/1\r\n:60F:C261031EUR1000,00\r\n"
            + ":61:2611121112D4,20NMSCNONREF\r\n:86:Example Bakery GmbH Fil 313 Nuernberg\r\nDatum 12.11.2026 Zeit 13:32\r\nKaufumsatz VISA Card\r\n"
            + ":62F:C261112EUR995,80\r\n-";
        var t = Assert.Single(Mt940Parser.Parse(mt940, "EUR", booked: true));
        Assert.Equal("Example Bakery GmbH Fil 313 Nuernberg Datum 12.11.2026 Zeit 13:32 Kaufumsatz VISA Card", t.Purpose);
        // The identity key keeps reading the lines run together, so bookings stored before are recognised.
        Assert.Equal("Example Bakery GmbH Fil 313 NuernbergDatum 12.11.2026 Zeit 13:32Kaufumsatz VISA Card", t.KeyPurpose);
    }

    [Fact]
    public void Mt940_purpose_fields_join_with_a_space_unless_a_full_field_continues_a_word()
    {
        Assert.Equal("Example Bakery Fil 313 Nuernberg Datum 12.11.2026 Zeit 13:3 Kaufumsatz VISA Card",
            Mt940Parser.JoinPurposeFields(["Example Bakery Fil 313", "Nuernberg", "Datum 12.11.2026 Zeit 13:3", "Kaufumsatz VISA Card"]));
        // A full 27-character field runs on into the next, whatever case the next starts with.
        Assert.Equal("SVWZ+Rechnung 4711 Kundennummer 0815", Mt940Parser.JoinPurposeFields(["SVWZ+Rechnung 4711 Kundennu", "mmer 0815"]));
        Assert.Equal("GRUNDSTEUER B 12,34 EUR WASSERGEB.", Mt940Parser.JoinPurposeFields(["GRUNDSTEUER B 12,34 EUR WAS", "SERGEB."]));
        Assert.Equal("Salary November", Mt940Parser.JoinPurposeFields(["Salary", "November"]));
    }

    [Fact]
    public void Mt940_free_text_lines_of_the_full_width_run_on()
    {
        var full = new string('A', 64) + "B";
        Assert.Equal(full + "CDE fgh", Mt940Parser.JoinLines([full, "CDE", "fgh"]));
        // Lines wrapped at a width several of them share run on as well.
        Assert.Equal("ABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOP short tail",
            Mt940Parser.JoinLines(["ABCDEFGHIJKLMNOPQRS", "TUVWXYZABCDEFGHIJKL", "MNOP short", "tail"]));
    }

    private static string Mt940(string info86, string entry = ":61:2611121112D12,34NDDTNONREF") =>
        ":20:STARTUMS\r\n:25:12345678/1234567\r\n:28C:0/1\r\n:60F:C261031EUR1000,00\r\n"
        + entry + "\r\n:86:" + info86 + "\r\n:62F:C261112EUR987,66\r\n-";

    [Fact]
    public void Mt940_pending_purpose_joins_full_fields_and_splits_off_the_sepa_tags()
    {
        var mt940 = Mt940("105?00SEPA-LASTSCHRIFT?20GRUNDSTEUER B 12,34 EUR WAS?21SERGEB. 5,60 EUR MREF+M-123?2245 CRED+DE00ZZZ00000012345?32EXAMPLE TOWN");
        var t = Assert.Single(Mt940Parser.Parse(mt940, "EUR", booked: false));
        Assert.False(t.Booked);
        Assert.Equal("GRUNDSTEUER B 12,34 EUR WASSERGEB. 5,60 EUR", t.Purpose);
        Assert.Equal("M-12345", t.Sepa?.MandateReference);
        Assert.Equal("DE00ZZZ00000012345", t.Sepa?.CreditorId);
        // The identity key reads the fields run together, tags included, as before.
        Assert.Equal("GRUNDSTEUER B 12,34 EUR WASSERGEB. 5,60 EUR MREF+M-12345 CRED+DE00ZZZ00000012345", t.KeyPurpose);
    }

    [Fact]
    public void Mt940_short_field_is_followed_by_a_space_and_free_text_before_tags_is_the_purpose()
    {
        var mt940 = Mt940("105?20GRUNDABGABEN 1234-10-001 KO?21RNACHERSTR. 5 NR. 9?22EREF+E-1 MREF+M-2 CRED+C-3?32EXAMPLE TOWN");
        var t = Assert.Single(Mt940Parser.Parse(mt940, "EUR", booked: false));
        Assert.Equal("GRUNDABGABEN 1234-10-001 KORNACHERSTR. 5 NR. 9", t.Purpose);
        Assert.Equal(new SepaDetails("E-1", "M-2", "C-3", "105"), t.Sepa);
        Assert.Equal("GRUNDABGABEN 1234-10-001 KORNACHERSTR. 5 NR. 9EREF+E-1 MREF+M-2 CRED+C-3", t.KeyPurpose);

        var free = Assert.Single(Mt940Parser.Parse(Mt940("WATER FEE 7,00 EUR\r\nMREF+M-9 CRED+C-8"), "EUR", booked: false));
        Assert.Equal("WATER FEE 7,00 EUR", free.Purpose);
        Assert.Equal("M-9", free.Sepa?.MandateReference);
        Assert.Equal("C-8", free.Sepa?.CreditorId);
    }

    [Fact]
    public void Camt_pending_purpose_splits_off_the_sepa_tags_and_keeps_its_key()
    {
        const string xml = "<Document xmlns=\"urn:iso:std:iso:20022:tech:xsd:camt.052.001.08\"><BkToCstmrAcctRpt><Rpt><Ntry><Amt Ccy=\"EUR\">7.00</Amt><CdtDbtInd>DBIT</CdtDbtInd><Sts><Cd>PDNG</Cd></Sts><BookgDt><Dt>2026-11-12</Dt></BookgDt><NtryDtls><TxDtls><RmtInf><Ustrd>WATER FEE 7,00 EUR</Ustrd><Ustrd>MREF+M-9 CRED+C-8</Ustrd></RmtInf></TxDtls></NtryDtls></Ntry></Rpt></BkToCstmrAcctRpt></Document>";
        var t = Assert.Single(CamtParser.Parse(xml, bookedDocument: false));
        Assert.Equal("WATER FEE 7,00 EUR", t.Purpose);
        Assert.Equal("M-9", t.Sepa?.MandateReference);
        Assert.Equal("C-8", t.Sepa?.CreditorId);
        Assert.Equal("WATER FEE 7,00 EUR MREF+M-9 CRED+C-8", t.KeyPurpose);
    }
}
