#pragma warning disable CA2000 // test objects live for the test
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBudget.Connectors;
using OpenBudget.Connectors.PayPal;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>PayPal's activity CSV (made-up rows) and the connector over a scripted signed-in browser.</summary>
public sealed class PayPalConnectorTests
{
    internal const string EnglishCsv =
        "﻿\"Date\",\"Time\",\"TimeZone\",\"Name\",\"Type\",\"Status\",\"Currency\",\"Gross\",\"Fee\",\"Net\",\"From Email Address\",\"To Email Address\",\"Transaction ID\",\"Item Title\",\"Reference Txn ID\",\"Balance\",\"Subject\",\"Note\",\"Balance Impact\"\r\n"
        + "\"10/02/2026\",\"09:00:00\",\"CET\",\"Example Shop\",\"Express Checkout Payment\",\"Completed\",\"EUR\",\"-1,234.50\",\"0.00\",\"-1,234.50\",\"me@example.org\",\"shop@example.com\",\"TX1\",\"Garden chair\",\"\",\"-1,234.50\",\"\",\"\",\"Debit\"\r\n"
        + "\"10/02/2026\",\"09:00:01\",\"CET\",\"\",\"Bank Deposit to PP Account \",\"Completed\",\"EUR\",\"1,234.50\",\"0.00\",\"1,234.50\",\"\",\"me@example.org\",\"TX2\",\"\",\"TX1\",\"0.00\",\"\",\"\",\"Credit\"\r\n"
        + "\"10/05/2026\",\"12:00:00\",\"CET\",\"Bob Example\",\"Mobile Payment\",\"Completed\",\"EUR\",\"50.00\",\"-1.10\",\"48.90\",\"bob@example.net\",\"me@example.org\",\"TX3\",\"\",\"\",\"48.90\",\"Dinner, \"\"shared\"\"\",\"\",\"Credit\"\r\n"
        + "\"10/06/2026\",\"08:00:00\",\"CET\",\"Example Shop\",\"General Authorization\",\"Pending\",\"EUR\",\"-20.00\",\"0.00\",\"-20.00\",\"\",\"\",\"TX4\",\"\",\"\",\"48.90\",\"\",\"\",\"Memo\"\r\n"
        + "\"10/07/2026\",\"08:00:00\",\"CET\",\"US Store\",\"Express Checkout Payment\",\"Completed\",\"USD\",\"-10.00\",\"0.00\",\"-10.00\",\"\",\"\",\"TX5\",\"E-book\",\"\",\"-10.00\",\"\",\"\",\"Debit\"\r\n"
        + "\"10/07/2026\",\"08:00:00\",\"CET\",\"\",\"General Currency Conversion\",\"Completed\",\"USD\",\"10.00\",\"0.00\",\"10.00\",\"\",\"\",\"TX6\",\"\",\"TX5\",\"0.00\",\"\",\"\",\"Credit\"\r\n"
        + "\"10/07/2026\",\"08:00:00\",\"CET\",\"\",\"General Currency Conversion\",\"Completed\",\"EUR\",\"-9.20\",\"0.00\",\"-9.20\",\"\",\"\",\"TX7\",\"\",\"TX5\",\"39.70\",\"\",\"\",\"Debit\"\r\n"
        + "\"10/08/2026\",\"10:00:00\",\"CET\",\"Carol Example\",\"Payment Received\",\"Pending\",\"EUR\",\"5.00\",\"0.00\",\"5.00\",\"\",\"\",\"TX8\",\"\",\"\",\"44.70\",\"\",\"\",\"Credit\"\r\n"
        + "\"10/08/2026\",\"10:00:00\",\"CET\",\"Carol Example\",\"Payment Received\",\"Pending\",\"EUR\",\"5.00\",\"0.00\",\"5.00\",\"\",\"\",\"TX8\",\"\",\"\",\"44.70\",\"\",\"\",\"Credit\"\r\n";

    internal const string GermanCsv =
        "Datum;Uhrzeit;Zeitzone;Name;Typ;Status;Währung;Brutto;Gebühr;Netto;Transaktionscode;Artikelbezeichnung;Guthaben;Auswirkung auf Guthaben\n"
        + "03.10.2026;14:00:00;MESZ;Beispiel GmbH;Zahlung im Einzugsverfahren;Abgeschlossen;EUR;-1.234,56;0,00;-1.234,56;DE1;Lampe;10,00;Soll\n"
        + "04.10.2026;15:00:00;MESZ;Dora Beispiel;Zahlung erhalten;Ausstehend;EUR;12,50;-0,60;11,90;DE2;;21,90;Haben\n";

    [Fact]
    public void Amounts_with_either_decimal_separator_are_read()
    {
        Assert.Equal(-1234.50m, PayPalCsv.ParseAmount("-1,234.50"));
        Assert.Equal(-1234.56m, PayPalCsv.ParseAmount("-1.234,56"));
        Assert.Equal(12.5m, PayPalCsv.ParseAmount("12,5"));
        Assert.Equal(0.99m, PayPalCsv.ParseAmount("0.99"));
        Assert.Equal(1000m, PayPalCsv.ParseAmount("1.000"));
        Assert.Equal(1234567.89m, PayPalCsv.ParseAmount("1.234.567,89"));
        Assert.Null(PayPalCsv.ParseAmount(""));
    }

    [Fact]
    public void English_rows_map_to_gross_and_fee_lines_without_memos_conversion_pairs_or_duplicates()
    {
        var rows = PayPalCsv.Parse(EnglishCsv);
        Assert.Equal(9, rows.Count);
        Assert.Equal(new DateOnly(2026, 10, 2), rows[0].Date); // US order: no day above 12 in the file
        Assert.Equal("Dinner, \"shared\"", rows[2].Subject);

        var eur = PayPalCsv.Map(rows, "EUR", null);
        Assert.Equal(["TX1", "TX2", "TX3", "TX3:fee", "TX7", "TX8"], eur.Select(t => t.BankTransactionId));
        Assert.Equal(-1234.50m, eur[0].Amount);
        Assert.Equal("Example Shop", eur[0].Name);
        Assert.Equal("Garden chair", eur[0].Purpose);
        Assert.True(eur[0].Booked);
        Assert.Equal(1234.50m, eur[1].Amount); // the bank deposit funding the payment is kept
        Assert.Equal(48.90m, eur[2].Amount + eur[3].Amount); // gross and fee net to the net amount
        Assert.Equal("PayPal", eur[3].Name);
        Assert.Equal(-9.20m, eur[4].Amount);
        Assert.Equal("US Store", eur[4].Name); // the conversion carries the foreign payment
        Assert.Equal("E-book · -10.00 USD", eur[4].Purpose);
        Assert.False(eur[5].Booked); // pending
        Assert.Equal(44.70m, eur.Sum(t => t.Amount)); // reconciles with the balance
        Assert.Empty(PayPalCsv.Map(rows, "USD", null)); // a payment and its conversion cancel out
        Assert.Equal(44.70m, PayPalCsv.LatestBalance(rows, "EUR"));
        Assert.Equal(["TX7", "TX8"], PayPalCsv.Map(rows, "EUR", new DateOnly(2026, 10, 7)).Select(t => t.BankTransactionId));
    }

    [Fact]
    public void German_semicolon_rows_are_read()
    {
        var rows = PayPalCsv.Parse(GermanCsv);
        var eur = PayPalCsv.Map(rows, "EUR", null);
        Assert.Equal(new DateOnly(2026, 10, 3), rows[0].Date);
        Assert.Equal([-1234.56m, 12.50m, -0.60m], eur.Select(t => t.Amount));
        Assert.False(eur[1].Booked);
        Assert.False(eur[2].Booked); // the fee of a pending payment is pending too
        Assert.Equal(21.90m, PayPalCsv.LatestBalance(rows, "EUR"));
    }

    [Fact]
    public void Day_first_slash_dates_are_recognised_from_the_file()
    {
        var rows = PayPalCsv.Parse("Date,Currency,Gross,Fee,Net,Transaction ID\n01/02/2026,EUR,1.00,0.00,1.00,A\n25/02/2026,EUR,2.00,0.00,2.00,B\n");
        Assert.Equal([new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 25)], rows.Select(r => r.Date));
    }

    [Fact]
    public async Task A_sync_signs_in_requests_the_report_waits_for_it_and_lists_one_account_per_currency()
    {
        var browser = new ScriptedPayPal(EnglishCsv) { ReadyAfter = 2 };
        var connector = Connector();
        var creds = new ConnectorCredentials("me@example.org", "pw");

        var ask = await connector.LoginAsync(creds, null, null, CancellationToken.None);
        Assert.Equal(StepStatus.Challenge, ask.Status);
        var spec = ask.Challenge!.WebLoginSpec!;
        Assert.True(spec.KeepBrowser);
        Assert.Equal(2, spec.Autofill!.Steps.Count); // e-mail, then password
        Assert.Equal("/myaccount/summary", spec.ResumeUrl!.AbsolutePath);
        Assert.Contains("#bannerDeclineButton", spec.Dismiss);

        var done = await connector.LoginAsync(creds, null, new ChallengeAnswer(ask.Challenge, null) { Browser = browser }, CancellationToken.None);
        Assert.Equal(StepStatus.Done, done.Status);

        var accounts = await connector.ListAccountsAsync(CancellationToken.None);
        var account = Assert.Single(accounts);
        Assert.Equal("paypal-EUR", account.RemoteId);
        Assert.Equal(RemoteAccountType.Other, account.Type);

        var refresh = await connector.RefreshAsync(account, new DateOnly(2026, 10, 1), null, CancellationToken.None);
        Assert.Equal(StepStatus.Done, refresh.Status);
        Assert.Equal(44.70m, refresh.Value!.Balance);
        Assert.Equal(6, refresh.Value.Transactions.Count);
        Assert.Equal(1, browser.Created); // the listing's report already covers the refresh
        Assert.True(browser.CsrfSeen);

        var session = await connector.LogoutAsync(CancellationToken.None);
        Assert.Equal("EUR", session!.Values[PayPalConnector.CurrenciesKey]);
    }

    [Fact]
    public async Task An_unexpected_report_answer_fails_with_a_message()
    {
        var browser = new ScriptedPayPal(EnglishCsv) { CreateStatus = 403 };
        var connector = Connector();
        var creds = new ConnectorCredentials("me@example.org", "pw");
        var ask = await connector.LoginAsync(creds, null, null, CancellationToken.None);
        await connector.LoginAsync(creds, null, new ChallengeAnswer(ask.Challenge!, null) { Browser = browser }, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<ConnectorException>(() => connector.ListAccountsAsync(CancellationToken.None));
        Assert.Contains("activity download", ex.Message, StringComparison.Ordinal);
    }

    private static PayPalConnector Connector() => new(
        new ConnectorContext(new BankHttpClient(), new FixedClock(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero)), NullLogger.Instance),
        new PayPalOptions { BaseUri = new Uri("https://paypal.test/"), PollDelay = TimeSpan.Zero });

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>A signed-in PayPal page: the report request answers an id, the list says ready after a few polls.</summary>
    private sealed class ScriptedPayPal(string csv) : IBrowserFetcher
    {
        public int ReadyAfter { get; init; }

        public int CreateStatus { get; init; } = 200;

        public int Created { get; private set; }

        public bool CsrfSeen { get; private set; }

        private int _polls;

        public bool IsOpen => true;

        public Task NavigateAsync(Uri url, CancellationToken ct) => Task.CompletedTask;

        public Task<(bool Ok, string Reason)> RunAsync(string function, string argument, CancellationToken ct) => Task.FromResult((true, "csrf-1"));

        public Task<Uri?> ResolveRedirectAsync(Uri url, CancellationToken ct) => Task.FromResult<Uri?>(null);

        public Task<BrowserFetchResponse> SendAsync(BrowserFetchRequest request, CancellationToken ct)
        {
            var path = request.Url.AbsolutePath;
            if (path == "/reports/dlog/generate" && request.Method == "POST")
            {
                Created++;
                CsrfSeen = request.Headers.TryGetValue("X-CSRF-Token", out var t) && t == "csrf-1";
                return Answer(CreateStatus, "application/json", CreateStatus == 200 ? "{\"data\":{\"reportId\":\"R1\"}}" : "{\"error\":\"x\"}");
            }
            if (path == "/reports/dlog/list")
                return Answer(200, "application/json", $"{{\"reports\":[{{\"id\":\"R1\",\"status\":\"{(++_polls > ReadyAfter ? "COMPLETED" : "PENDING")}\"}}]}}");
            if (path == "/reports/dlog/download/R1") return Answer(200, "text/csv", csv);
            return Answer(404, "text/plain", "");
        }

        private static Task<BrowserFetchResponse> Answer(int status, string type, string body) =>
            Task.FromResult(new BrowserFetchResponse(status, null, type, Encoding.UTF8.GetBytes(body)));
    }
}
