#pragma warning disable CA2000 // test objects live for the test
using System.Globalization;
using System.Text;
using System.Text.Json;
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
        var progress = new List<string>();
        var connector = Connector(progress: progress.Add);
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
        Assert.Equal("20260812220000", browser.StartUtc); // account midnight in summer time, sent as UTC
        Assert.Equal("20261111112959", browser.EndUtc); // Pacific freshness timestamp, not the end of a future day
        Assert.Equal("DLOGTEMPLATE", browser.Template);
        Assert.Contains(progress, message => message.StartsWith("Waiting for PayPal", StringComparison.Ordinal));
        Assert.Contains(progress, message => message.StartsWith("Downloading PayPal", StringComparison.Ordinal));

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

    [Theory]
    [InlineData("Personal", "DLOGCONSUMERTEMPLATE")]
    [InlineData("Premier", "DLOGTEMPLATE")]
    public async Task The_account_group_chooses_the_sites_CSV_template(string group, string template)
    {
        var browser = new ScriptedPayPal(EnglishCsv) { UserGroup = group };
        var connector = await SignedInAsync(browser);
        await connector.ListAccountsAsync(CancellationToken.None);
        Assert.Equal(template, browser.Template);
    }

    [Fact]
    public async Task A_matching_completed_report_is_reused_on_the_next_sync()
    {
        var browser = new ScriptedPayPal(EnglishCsv) { UnrelatedReport = true };
        await (await SignedInAsync(browser)).ListAccountsAsync(CancellationToken.None);
        await (await SignedInAsync(browser)).ListAccountsAsync(CancellationToken.None);
        Assert.Equal(1, browser.Created);
        Assert.Equal(2, browser.Downloaded);
    }

    [Fact]
    public async Task A_pending_report_is_reused_when_a_sync_retries_after_timing_out()
    {
        var browser = new ScriptedPayPal(EnglishCsv) { ReadyAfter = 2 };
        var first = await SignedInAsync(browser, new PayPalOptions { BaseUri = new Uri("https://paypal.test/"), PollAttempts = 1, PollDelay = TimeSpan.Zero });
        await Assert.ThrowsAsync<ConnectorException>(() => first.ListAccountsAsync(CancellationToken.None));
        await (await SignedInAsync(browser)).ListAccountsAsync(CancellationToken.None);
        Assert.Equal(1, browser.Created);
    }

    [Fact]
    public async Task A_split_report_is_downloaded_one_CSV_at_a_time()
    {
        var browser = new ScriptedPayPal(EnglishCsv) { FileCount = 2 };
        await (await SignedInAsync(browser)).ListAccountsAsync(CancellationToken.None);
        Assert.Equal(2, browser.Downloaded);
    }

    [Fact]
    public async Task History_exports_are_all_requested_before_waiting_for_the_first_one()
    {
        var browser = new ScriptedPayPal(EnglishCsv);
        var connector = await SignedInAsync(browser);
        var accounts = await connector.ListAccountsAsync(CancellationToken.None);
        browser.ReleaseAfterCreated = 4;
        var refreshed = await connector.RefreshAsync(accounts[0], new DateOnly(2023, 11, 11), null, CancellationToken.None);
        Assert.Equal(StepStatus.Done, refreshed.Status);
        Assert.Equal(4, browser.Created);
    }

    private static string Row(string id, string amount, string description) =>
        $"""<div aria-controls="transactionDetails-{id}" aria-expanded="false"><div><div><div data-testid="trans_overview_root_container">"""
        + $"""<span data-testid="counterparty_name">Example Shop</span><span data-testid="transaction_amount">{amount}</span>"""
        + $"""<span data-testid="transaction_description">{description}</span><span data-testid="second_row_description_right"></span></div></div></div></div>""";

    internal static readonly string ActivityHtml =
        """<html><body><h3 data-testid="activity_list_header_view">This month</h3>"""
        + Row("1AB23456CD7890123", "\u221212,34 €", "9 Nov . Payment")
        + Row("98765432EF1234567", "5,00 €", "7 Nov . Money received")
        + "</body></html>";

    private static readonly string PendingHtml =
        ActivityHtml.Replace("</body>", Row("5PQ12345RS6789012", "\u22121,00 €", "10 Nov . <b>Pending</b>") + "</body>", StringComparison.Ordinal);

    [Fact]
    public void The_activity_page_lists_ids_and_pending_rows()
    {
        var list = PayPalActivityPage.Parse(PendingHtml)!;
        Assert.Equal(["1AB23456CD7890123", "98765432EF1234567", "5PQ12345RS6789012"], list.Ids);
        Assert.Equal(["5PQ12345RS6789012"], list.PendingIds);
        Assert.Empty(PayPalActivityPage.Parse("<html><body>No activity</body></html>")!.Ids);
    }

    [Fact]
    public void An_activity_page_without_the_expected_markers_is_unreadable()
    {
        Assert.Null(PayPalActivityPage.Parse(ActivityHtml.Replace("aria-controls=\"transactionDetails-1AB23456CD7890123\"", "", StringComparison.Ordinal)));
        Assert.Null(PayPalActivityPage.Parse(ActivityHtml.Replace("transaction_amount", "amount", StringComparison.Ordinal)));
        Assert.Null(PayPalActivityPage.Parse(ActivityHtml.Replace("transaction_description", "description", StringComparison.Ordinal)));
    }

    private static SyncHistory History(int pending = 0, params string[] known) =>
        new(new DateTime(2026, 11, 8, 6, 0, 0, DateTimeKind.Utc), known.ToHashSet(StringComparer.Ordinal), pending);

    [Fact]
    public void The_decision_skips_only_when_every_listed_id_is_stored_and_nothing_is_pending()
    {
        var page = PayPalActivityPage.Parse(ActivityHtml);
        var both = History(0, "1AB23456CD7890123", "98765432EF1234567", "OLDER");
        Assert.Equal(PayPalConnector.QuickOutcome.NothingNew, PayPalConnector.Decide(page, both, 50));
        Assert.Equal(PayPalConnector.QuickOutcome.Changed, PayPalConnector.Decide(page, History(0, "1AB23456CD7890123"), 50));
        Assert.Equal(PayPalConnector.QuickOutcome.Changed, PayPalConnector.Decide(page, History(1, "1AB23456CD7890123", "98765432EF1234567"), 50));
        Assert.Equal(PayPalConnector.QuickOutcome.Changed, PayPalConnector.Decide(PayPalActivityPage.Parse(PendingHtml), both, 50));
        Assert.Equal(PayPalConnector.QuickOutcome.Unreadable, PayPalConnector.Decide(null, both, 50));
        Assert.Equal(PayPalConnector.QuickOutcome.Unreadable, PayPalConnector.Decide(page, both, 2)); // possibly cut off
    }

    [Fact]
    public async Task Nothing_new_on_the_activity_page_ends_the_sync_without_a_download()
    {
        var browser = new ScriptedPayPal(EnglishCsv) { ActivityPage = ActivityHtml };
        var connector = await SignedInAsync(browser);
        connector.UseSyncHistory(History(0, "1AB23456CD7890123", "98765432EF1234567"));
        var account = Assert.Single(await connector.ListAccountsAsync(CancellationToken.None));
        var refresh = await connector.RefreshAsync(account, new DateOnly(2026, 11, 1), null, CancellationToken.None);
        Assert.Null(refresh.Value!.Balance); // kept as it was
        Assert.Empty(refresh.Value.Transactions);
        Assert.Equal(0, browser.Created);
        Assert.Contains("start_date=2026-11-05&end_date=2026-11-11", browser.ActivityQuery, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_new_id_or_an_unreadable_page_downloads_only_the_days_since_the_last_sync(bool readable)
    {
        var browser = new ScriptedPayPal(EnglishCsv) { ActivityPage = readable ? ActivityHtml : "<html>changed layout</html><div data-testid=\"trans_overview_root_container\">" };
        var connector = await SignedInAsync(browser);
        connector.UseSyncHistory(History(0, "1AB23456CD7890123"));
        var account = Assert.Single(await connector.ListAccountsAsync(CancellationToken.None));
        var refresh = await connector.RefreshAsync(account, new DateOnly(2026, 10, 1), null, CancellationToken.None);
        Assert.Equal(StepStatus.Done, refresh.Status);
        Assert.Equal(1, browser.Created);
        Assert.Equal("20261104230000", browser.StartUtc); // 2026-11-05, account midnight
    }

    [Fact]
    public async Task A_last_sync_older_than_the_limit_skips_the_quick_check()
    {
        var browser = new ScriptedPayPal(EnglishCsv) { ActivityPage = ActivityHtml };
        var connector = await SignedInAsync(browser);
        connector.UseSyncHistory(new SyncHistory(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), new HashSet<string>(StringComparer.Ordinal), 0));
        await connector.ListAccountsAsync(CancellationToken.None);
        Assert.Null(browser.ActivityQuery);
        Assert.Equal("20260812220000", browser.StartUtc);
    }

    private static async Task<PayPalConnector> SignedInAsync(ScriptedPayPal browser, PayPalOptions? options = null)
    {
        var connector = Connector(options);
        var credentials = new ConnectorCredentials("me@example.org", "pw");
        var ask = await connector.LoginAsync(credentials, null, null, CancellationToken.None);
        await connector.LoginAsync(credentials, null, new ChallengeAnswer(ask.Challenge!, null) { Browser = browser }, CancellationToken.None);
        return connector;
    }

    private static PayPalConnector Connector(PayPalOptions? options = null, Action<string>? progress = null) => new(
        new ConnectorContext(new BankHttpClient(), new FixedClock(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero)), NullLogger.Instance) { Progress = progress },
        options ?? new PayPalOptions { BaseUri = new Uri("https://paypal.test/"), PollDelay = TimeSpan.Zero });

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>The observed web reporting protocol, with only made-up report identifiers and activity.</summary>
    private sealed class ScriptedPayPal(string csv) : IBrowserFetcher
    {
        public int ReadyAfter { get; init; }
        public int CreateStatus { get; init; } = 200;
        public string UserGroup { get; init; } = "Premier";
        public bool UnrelatedReport { get; init; }
        public int FileCount { get; init; } = 1;
        public int Created { get; private set; }
        public int Downloaded { get; private set; }
        public bool CsrfSeen { get; private set; }
        public string? Template { get; private set; }
        public string? StartUtc { get; private set; }
        public string? EndUtc { get; private set; }
        public int ReleaseAfterCreated { get; set; } = 1;
        public string? ActivityPage { get; init; }
        public string? ActivityQuery { get; private set; }
        private readonly Dictionary<(DateTime Start, DateTime End), int> _reports = [];
        public bool IsOpen => true;
        public Task NavigateAsync(Uri url, CancellationToken ct) => Task.CompletedTask;
        public Task<(bool Ok, string Reason)> RunAsync(string function, string argument, CancellationToken ct) => throw new NotSupportedException();
        public Task<Uri?> ResolveRedirectAsync(Uri url, CancellationToken ct) => Task.FromResult<Uri?>(null);

        public async Task<BrowserFetchResponse> SendAsync(BrowserFetchRequest request, CancellationToken ct)
        {
            var path = request.Url.AbsolutePath;
            if (path == "/reports/dlog" && request.Method == "GET")
                return await Answer(200, "text/html", """<script id="server-data" type="application/json">{"_csrf":"csrf-1","locality":{"timezone":"Europe/Berlin"}}</script>""");
            if (path == "/myaccount/activities" && request.Method == "GET")
            {
                ActivityQuery = request.Url.Query;
                return await Answer(ActivityPage == null ? 404 : 200, "text/html", ActivityPage ?? "");
            }
            Assert.Equal("POST", request.Method);
            Assert.Equal("csrf-1", request.Headers["X-CSRF-Token"]);
            Assert.Equal("XMLHttpRequest", request.Headers["X-Requested-With"]);
            CsrfSeen = true;
            using var doc = JsonDocument.Parse(request.Body!);
            var body = doc.RootElement;
            var operation = body.GetProperty("apiType").GetString();
            if (path == "/reports/apis/dlog/ql")
            {
                Assert.Equal("activityDownload", operation);
                return await JsonAnswer(new[] { new { userGroup = UserGroup, data = new { dlogRefreshInfo = new { last_updated = "20261111032959" } } } });
            }
            Assert.Equal("/reports/apis/common/ql", path);
            Assert.Equal("DLOG", body.GetProperty("reportType").GetString());
            if (operation == "reportCreate")
            {
                Created++;
                var form = body.GetProperty("formdata");
                Assert.Equal("CSV", form.GetProperty("file_format").GetString());
                Assert.Equal("WEB", form.GetProperty("delivery_channel").GetString());
                Assert.Equal("BALANCE_IMPACTING", form.GetProperty("filters").GetString());
                Template = form.GetProperty("name").GetString();
                StartUtc = form.GetProperty("start_date").GetString();
                EndUtc = form.GetProperty("end_date").GetString();
                var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
                var start = TimeZoneInfo.ConvertTimeFromUtc(DateTime.ParseExact(StartUtc!, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal), zone);
                var end = TimeZoneInfo.ConvertTimeFromUtc(DateTime.ParseExact(EndUtc!, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal), zone);
                _reports[(start, end)] = 0;
                return await Answer(CreateStatus, "application/json", CreateStatus == 200 ? """[{"document_id":"R1"}]""" : """{"error":true}""");
            }
            if (operation == "reports")
            {
                var reports = new List<object>();
                foreach (var range in _reports.Keys.ToList())
                {
                    var (start, end) = range;
                    var stampStart = start.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    var stampEnd = end.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    if (UnrelatedReport)
                    {
                        reports.Add(new { documentFileFormat = "CSV", documentStartDate = stampStart, documentEndDate = stampEnd, documentMetadata = new { filters = "ALL_TRANSACTIONS" }, status = "AVAILABLE", downloadUrls = new[] { "wrong.csv" } });
                        reports.Add(new { documentFileFormat = "CSV", documentStartDate = start.AddDays(1).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), documentEndDate = stampEnd, documentMetadata = new { filters = "BALANCE_IMPACTING" }, status = "AVAILABLE", downloadUrls = new[] { "wrong.csv" } });
                        reports.Add(new { documentFileFormat = "PDF", documentStartDate = stampStart, documentEndDate = stampEnd, documentMetadata = new { filters = "BALANCE_IMPACTING" }, status = "AVAILABLE", downloadUrls = new[] { "wrong.csv" } });
                    }
                    reports.Add(new { documentFileFormat = "CSV", documentStartDate = stampStart, documentEndDate = stampEnd, documentMetadata = new { filters = "BALANCE_IMPACTING" }, status = ++_reports[range] > ReadyAfter && Created >= ReleaseAfterCreated ? "AVAILABLE" : "IN_PROGRESS", downloadUrls = Enumerable.Range(1, FileCount).Select(i => $"R{i}.CSV").ToArray() });
                }
                return await JsonAnswer(new[] { new { reportDetails = reports } });
            }
            Assert.Equal("download", operation);
            var file = Assert.Single(body.GetProperty("reportNames").EnumerateArray()).GetString();
            Assert.NotEqual("wrong.csv", file);
            Downloaded++;
            return await Answer(200, "application/octet-stream", csv);
        }

        private static Task<BrowserFetchResponse> JsonAnswer(object body) => Answer(200, "application/json", JsonSerializer.Serialize(body));
        private static Task<BrowserFetchResponse> Answer(int status, string type, string body) =>
            Task.FromResult(new BrowserFetchResponse(status, null, type, Encoding.UTF8.GetBytes(body)));
    }
}
