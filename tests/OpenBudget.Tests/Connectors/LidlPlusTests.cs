#pragma warning disable CA2000 // test objects live for the test
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors;
using OpenBudget.Connectors.LidlPlus;
using OpenBudget.Connectors.Receipts;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;
using OpenBudget.Services;
using OpenBudget.Tests.Receipts;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>Lidl Plus (made-up receipts): ticket parsing, sign-in through the app callback, rotating refresh tokens, matching and splitting.</summary>
public sealed class LidlPlusTests
{
    private static readonly long Giro = RowIds.Namespace(BankWorld.SourceAlice, 900);

    // ---- parsing ----

    [Fact]
    public void A_ticket_list_and_details_become_a_receipt_with_discounts_deposits_and_weights()
    {
        var page = LidlTickets.ParseList(JsonNode.Parse("""
            {"tickets":[{"id":"T-1","date":"2026-11-03T16:35:12+00:00","totalAmount":4.9},{"id":"T-2","date":"2026-11-01T09:00:00","totalAmount":"12,34"}],"totalCount":7}
            """)!);
        Assert.Equal(7, page.TotalCount);
        Assert.Equal(["T-1", "T-2"], page.Tickets.Select(t => t.Id));
        Assert.Equal(new DateTime(2026, 11, 3, 17, 35, 12), page.Tickets[0].Date); // UTC → Berlin
        Assert.Equal(12.34m, page.Tickets[1].Total);

        var receipt = LidlTickets.ParseDetail(FakeLidlPlus.Groceries("T-1", "2026-11-03T16:35:12+00:00"))!;
        Assert.Equal("Lidl Teststraße 1, Musterstadt", receipt.Store);
        Assert.Equal(4.90m, receipt.Total);
        Assert.Equal(4.90m, receipt.PaymentAmount);
        Assert.Equal(
            ["Bananen", "Vollmilch 3,5%", "Mineralwasser", "Pfand 0,25", "Pfandrückgabe", "Spülmittel Zitrone"],
            receipt.Items.Select(i => i.Name));
        var bananas = receipt.Items[0];
        Assert.True(bananas.Weighted);
        Assert.Equal(0.812m, bananas.Quantity);
        Assert.Equal(1.49m, bananas.UnitPrice);
        Assert.Equal(1.21m, bananas.Total);
        var milk = receipt.Items[1];
        Assert.Equal((2m, 2.30m, 0.30m, 2.00m, "A"), (milk.Quantity, milk.Total, milk.Discount, milk.Net, milk.VatCode));
        Assert.Equal(LidlLineKind.Deposit, receipt.Items[3].Kind);
        Assert.Equal(0.25m, receipt.Items[3].Total);
        Assert.Equal(LidlLineKind.DepositReturn, receipt.Items[4].Kind);
        Assert.Equal(-0.25m, receipt.Items[4].Total);
        Assert.Equal(receipt.Total, receipt.Items.Sum(i => i.Net));
    }

    [Fact]
    public void In_the_shared_model_lines_carry_their_discounts_and_a_receipt_coupon_is_the_bonus()
    {
        var parsed = LidlTickets.ParseDetail(FakeLidlPlus.Groceries("T-1", "2026-11-03T16:35:12+00:00", coupon: 0.50m))!.ToParsed();
        Assert.Equal("T-1", parsed.InvoiceNumber);
        Assert.Equal(new DateOnly(2026, 11, 3), parsed.DeliveryDate);
        Assert.Equal(["812g", "2", "1", "1", "1", "1"], parsed.Items.Select(i => i.Quantity));
        Assert.Equal([121L, 200, 49, 25, -25, 120], parsed.Items.Select(i => i.TotalCents));
        Assert.Equal(
            [ReceiptItemKind.Product, ReceiptItemKind.Product, ReceiptItemKind.Product, ReceiptItemKind.Deposit, ReceiptItemKind.Deposit, ReceiptItemKind.Product],
            parsed.Items.Select(i => i.Kind));
        Assert.Equal((490L, -50L, 440L), (parsed.TotalCents, parsed.BonusCents, parsed.PaymentCents));

        // a part paid in cash is left to the booking-less side as well
        var mixed = LidlTickets.ParseDetail(FakeLidlPlus.Ticket("T-2", "2026-11-02T10:00:00", 20m, [FakeLidlPlus.Line("Kaffee", "1", "20,00", "20,00")], ("CASH", "5,00"), ("Lidl Pay", "15,00")))!.ToParsed();
        Assert.Equal((2000L, -500L, 1500L), (mixed.TotalCents, mixed.BonusCents, mixed.PaymentCents));

        Assert.Equal(40, LidlReceipt.InvoiceNumber(new string('7', 60)).Length);
    }

    [Fact]
    public void Cash_is_left_out_of_the_payment_and_a_ticket_without_payments_is_charged_its_total()
    {
        var mixed = LidlTickets.ParseDetail(FakeLidlPlus.Ticket("T-3", "2026-11-02T10:00:00", 20m, [], ("CASH", "5,00"), ("Lidl Pay", "15,00")))!;
        Assert.Equal(15m, mixed.PaymentAmount);
        var cash = LidlTickets.ParseDetail(FakeLidlPlus.Ticket("T-4", "2026-11-02T10:00:00", 3m, [], ("Bar", "3,00")))!;
        Assert.Equal(0m, cash.PaymentAmount);
        var unknown = LidlTickets.ParseDetail(FakeLidlPlus.Ticket("T-5", "2026-11-02T10:00:00", 7.5m, []))!;
        Assert.Null(unknown.PaymentAmount);
        Assert.Equal(7.5m, unknown.ChargedAmount);
    }

    [Fact]
    public void A_printed_receipt_gives_the_lines_when_the_ticket_has_no_item_list()
    {
        const string html = """
            <div><span id="purchase_list_line_1" class="currency">EUR</span>
            <span id="purchase_list_line_2" class="article" data-art-id="0001" data-art-quantity="0,456" data-unit-price="2,99" data-tax-type="A" data-art-description="Tomaten">Tomaten        1,36 A</span>
            <span id="purchase_list_line_3" class="article" data-art-id="0001">  0,456 kg x  2,99 EUR/kg</span>
            <span id="purchase_list_line_4" class="article" data-art-id="0002" data-art-quantity="1" data-unit-price="3,49" data-tax-type="B" data-art-description="Spülmittel">Spülmittel     3,49 B</span>
            <span id="purchase_list_line_5" class="discount" data-promotion-id="P1">Preisvorteil  -0,50</span>
            <span id="purchase_list_line_6" class="article">Pfandrückgabe  -0,75 A</span></div>
            """;
        var ticket = FakeLidlPlus.Ticket("T-6", "2026-11-02T10:00:00", 3.60m, [], ("CreditCard", "3,60"));
        ticket["itemsLine"] = new JsonArray();
        ticket["htmlPrintedReceipt"] = html;
        var receipt = LidlTickets.ParseDetail(ticket)!;
        Assert.Equal(["Tomaten", "Spülmittel", "Pfandrückgabe"], receipt.Items.Select(i => i.Name));
        Assert.True(receipt.Items[0].Weighted);
        Assert.Equal(0.456m, receipt.Items[0].Quantity);
        Assert.Equal((3.49m, 0.50m, "B"), (receipt.Items[1].Total, receipt.Items[1].Discount, receipt.Items[1].VatCode));
        Assert.Equal((LidlLineKind.DepositReturn, -0.75m), (receipt.Items[2].Kind, receipt.Items[2].Total));
        Assert.Equal(3.60m, receipt.Items.Sum(i => i.Net));
    }

    // ---- callback ----

    [Fact]
    public void The_callback_gives_the_code_only_with_the_state_that_was_sent()
    {
        var callback = new Uri("com.lidlplus.app://callback?code=abc%2Bdef&state=s-123&scope=openid");
        Assert.Equal("abc+def", LidlPlusConnector.CodeFromCallback(callback, "s-123", out _));
        Assert.Null(LidlPlusConnector.CodeFromCallback(callback, "s-999", out var problem));
        Assert.Equal("state mismatch", problem);
        Assert.Null(LidlPlusConnector.CodeFromCallback(new Uri("com.lidlplus.app://callback?code=abc"), "s-123", out _));
        Assert.Null(LidlPlusConnector.CodeFromCallback(new Uri("com.lidlplus.app://callback?error=access_denied&state=s-123"), "s-123", out _));
        Assert.Null(LidlPlusConnector.CodeFromCallback(new Uri("com.lidlplus.app://callback?state=s-123"), "s-123", out _));
    }

    [Fact]
    public void The_browser_finds_the_callback_in_a_redirect_answer_or_request()
    {
        using var redirect = JsonDocument.Parse("""
            {"requestId":"1","statusCode":302,"headers":{"location":"com.lidlplus.app://callback?code=c1&state=s1","cache-control":"no-store"}}
            """);
        using var request = JsonDocument.Parse("""
            {"request":{"url":"COM.LIDLPLUS.APP://callback?code=c2&state=s2","method":"GET"},"documentURL":"https://accounts.lidl.test/x"}
            """);
        Assert.Equal("com.lidlplus.app://callback?code=c1&state=s1",
            Assert.Single(OpenBudget.Browser.PuppeteerBrowserSession.RedirectTargets(redirect.RootElement, "com.lidlplus.app://callback")).OriginalString);
        Assert.Single(OpenBudget.Browser.PuppeteerBrowserSession.RedirectTargets(request.RootElement, "com.lidlplus.app://callback"));
    }

    // ---- the connector in a sync ----

    private sealed class Rig : IAsyncDisposable
    {
        public required BankWorld World { get; init; }
        public required FakeLidlPlus Lidl { get; init; }
        public required FakeBrowserHost Browser { get; init; }
        public int LoginId { get; set; }

        public ValueTask DisposeAsync() => World.DisposeAsync();
    }

    /// <summary>
    /// Alice's Lidl Plus login, her receipt categories (non-food goes to the drugstore) and a giro account with the card
    /// bookings of two receipts, categorized by a rule as groceries (and a booking of another shop with the same amount). The browser's "user" signs in by sending the page to the app callback with <paramref name="state"/>
    /// (null: the state the sign-in page was opened with).
    /// </summary>
    private static async Task<Rig> RigAsync(Func<string, string>? state = null)
    {
        var lidl = new FakeLidlPlus();
        lidl.Details.Add(FakeLidlPlus.Groceries("T-100", "2026-11-03T16:35:12+00:00", coupon: 0.50m));
        lidl.Details.Add(FakeLidlPlus.Ticket("T-099", "2026-11-02T08:00:00", 3.00m, [FakeLidlPlus.Line("Kaffee", "1", "3,00", "3,00")], ("Bar", "3,00")));
        lidl.Details.Add(FakeLidlPlus.Ticket("T-098", "2026-11-01T08:00:00", 12.50m, [FakeLidlPlus.Line("Blumen", "1", "12,50", "12,50")], ("Lidl Pay", "12,50")));
        lidl.BrokenV2.Add("T-098");
        var browser = new FakeBrowserHost { FieldWait = TimeSpan.Zero };
        browser.User = session =>
        {
            var query = HttpUtility.ParseQueryString(session.StartedAt!.Query);
            lidl.Challenge = query["code_challenge"];
            Assert.Equal("S256", query["code_challenge_method"]);
            Assert.Equal("openid profile offline_access lpprofile lpapis", query["scope"]);
            var sent = query["state"]!;
            session.SendTo(new Uri($"com.lidlplus.app://callback?code={lidl.Code}&state={Uri.EscapeDataString(state?.Invoke(sent) ?? sent)}"));
            return Task.CompletedTask;
        };
        var world = await BankWorld.CreateAsync(extras: new BankWorldExtras
        {
            Registrations = [LidlPlusConnector.CreateRegistration(FakeLidlPlus.Options)],
            HttpFactory = lidl.CreateClient,
            BrowserHost = browser,
        });
        await ReceiptWorld.SeedAsync(world.T, users: false);
        await ReceiptWorld.SetNonFoodAsync(world.T, ReceiptWorld.Drugstore);
        await using (var db = await world.T.System.CreateDbContextAsync())
        {
            db.Accounts.Add(new Account { Id = Giro, OwnerUserId = BankWorld.Alice, Name = "Giro" });
            db.Transactions.AddRange(
                Booking(1, -440, new DateTime(2026, 11, 5), "S. Payment Solutions GmbH", "VISA Lidl sagt Danke"),
                Booking(2, -1250, new DateTime(2026, 11, 2), "Florist Example", null),
                Booking(3, -1250, new DateTime(2026, 11, 3), "S. Digits Payment GmbH", "Lidl Pay"));
            await db.SaveChangesAsync();
        }
        var login = await world.For(BankWorld.Alice).CreateLoginAsync(LidlPlusConnector.TypeName, "Lidl", "", "");
        return new Rig { World = world, Lidl = lidl, Browser = browser, LoginId = login.Id };
    }

    private static Transaction Booking(int row, long cents, DateTime day, string name, string? purpose) => new()
    {
        Id = RowIds.Namespace(BankWorld.SourceAlice, 900 + row),
        OwnerUserId = BankWorld.Alice,
        AccountId = Giro,
        Amount = cents,
        Timestamp = new DateTimeOffset(DateTime.SpecifyKind(day, DateTimeKind.Utc)).ToUnixTimeSeconds(),
        Name = name,
        Purpose = purpose,
        CategoryId = ReceiptWorld.Groceries,
        CategorySetBy = TransactionCategorySource.Rule,
    };

    private static async Task<string?> StoredRefreshTokenAsync(Rig rig)
    {
        await using var db = await rig.World.T.System.CreateDbContextAsync();
        var login = await db.BankLogins.AsNoTracking().SingleAsync(l => l.Id == rig.LoginId);
        if (!rig.World.Secrets.TryDecrypt(login.EncryptedSession, BankSyncRunner.SessionPurpose, out var json)) return null;
        return JsonSerializer.Deserialize<ConnectorSession>(json)!.Values.GetValueOrDefault(LidlPlusConnector.RefreshTokenKey);
    }

    private static async Task<List<ReceiptInvoice>> ReceiptsAsync(Rig rig)
    {
        await using var db = await rig.World.T.System.CreateDbContextAsync();
        return await db.ReceiptInvoices.AsNoTracking().Include(r => r.Items).OrderBy(r => r.InvoiceNumber).ToListAsync();
    }

    [Fact]
    public async Task Signing_in_captures_the_callback_reads_the_receipts_and_splits_their_bookings()
    {
        await using var rig = await RigAsync();

        var status = await rig.World.SyncAsync(BankWorld.Alice, rig.LoginId);

        Assert.True(status.Succeeded, status.Error);
        Assert.Single(rig.Browser.Sessions);
        Assert.True(rig.Browser.Sessions[0].Disposed);
        Assert.Equal(["authorization_code"], rig.Lidl.Grants);
        Assert.Equal(rig.Lidl.CurrentRefresh, await StoredRefreshTokenAsync(rig));
        Assert.Contains(rig.Lidl.TicketRequests, r => r.StartsWith("/api/v3/DE/tickets/T-098", StringComparison.Ordinal));

        var receipts = await ReceiptsAsync(rig);
        Assert.Equal(["T-098", "T-099", "T-100"], receipts.Select(r => r.InvoiceNumber));
        Assert.All(receipts, r => Assert.Equal((ReceiptShops.Lidl, "Lidl Teststraße 1, Musterstadt"), (r.Shop, r.Store)));
        var groceries = receipts.Single(r => r.InvoiceNumber == "T-100");
        Assert.Equal(6, groceries.Items.Count);
        var booking = RowIds.Namespace(BankWorld.SourceAlice, 901);
        Assert.Equal(booking, groceries.TransactionId);
        // the florist has the same amount a day earlier, but is not Lidl
        Assert.Equal(RowIds.Namespace(BankWorld.SourceAlice, 903), receipts.Single(r => r.InvoiceNumber == "T-098").TransactionId);
        Assert.Null(receipts.Single(r => r.InvoiceNumber == "T-099").TransactionId); // cash

        // split like a REWE receipt: the washing-up liquid goes to the drugstore, the coupon is spread, the parts add up
        var rows = await rig.World.TransactionsAsync(Giro);
        var parent = rows.Single(t => t.Id == booking);
        Assert.Equal(SplitState.Parent, parent.Splitted);
        Assert.StartsWith("Bananen; Vollmilch 3,5% ×2", parent.OrderItems, StringComparison.Ordinal);
        var parts = rows.Where(t => t.SplitParentId == booking && t.Splitted == SplitState.Child).ToList();
        Assert.Equal(-440, parts.Sum(p => p.Amount));
        Assert.Equal([ReceiptWorld.Groceries, ReceiptWorld.Drugstore], parts.OrderBy(p => p.Amount).Select(p => p.CategoryId));
        Assert.Equal("Spülmittel Zitrone", parts.Single(p => p.CategoryId == ReceiptWorld.Drugstore).OrderItems);
        Assert.Null(rows.Single(t => t.Name == "Florist Example").OrderItems);

        // the details view and the receipts list show the Lidl receipt with its store
        var service = ReceiptServiceFor(rig);
        var view = (await service.GetForBookingAsync(booking))!;
        Assert.Equal("Lidl Teststraße 1, Musterstadt", view.Title);
        Assert.Equal(6, view.Groups.Sum(g => g.Lines.Count));
        Assert.Contains(await service.ListAsync(), r => r.Title == "Lidl Teststraße 1, Musterstadt" && r.TransactionId == booking);
        var summary = await service.GetShopSummaryAsync(ReceiptShops.Lidl);
        Assert.Equal((3, 2, 0), (summary.Count, summary.Matched, summary.OpenCount));

        // matching again changes nothing
        await using (var db = await rig.World.T.System.CreateDbContextAsync())
            await new ReceiptStore().MatchAsync(db, BankWorld.Alice, CancellationToken.None);
        Assert.Equal(parts.Select(p => (p.Id, p.Amount)), (await rig.World.TransactionsAsync(Giro))
            .Where(t => t.SplitParentId == booking && t.Splitted == SplitState.Child).Select(p => (p.Id, p.Amount)));
    }

    private static ReceiptService ReceiptServiceFor(Rig rig)
    {
        var view = rig.World.View(BankWorld.Alice);
        return new ReceiptService(view.Factory, rig.World.T.System, view.Access, null!, new ReceiptStore(), null!, rig.World.T.Notifier, new ChangeOrigin(), rig.World.Clock);
    }

    [Fact]
    public async Task The_next_sync_uses_the_rotated_refresh_token_and_keeps_the_new_one()
    {
        await using var rig = await RigAsync();
        Assert.True((await rig.World.SyncAsync(BankWorld.Alice, rig.LoginId)).Succeeded);
        var first = await StoredRefreshTokenAsync(rig);

        rig.Lidl.Details.Insert(0, FakeLidlPlus.Ticket("T-101", "2026-11-08T12:00:00", 2.00m, [FakeLidlPlus.Line("Äpfel", "1", "2,00", "2,00")], ("CreditCard", "2,00")));
        var status = await rig.World.SyncAsync(BankWorld.Alice, rig.LoginId);

        Assert.True(status.Succeeded, status.Error);
        Assert.Single(rig.Browser.Sessions); // no second sign-in
        Assert.Equal(["authorization_code", "refresh_token"], rig.Lidl.Grants);
        var second = await StoredRefreshTokenAsync(rig);
        Assert.NotEqual(first, second);
        Assert.Equal(rig.Lidl.CurrentRefresh, second);
        Assert.Equal(4, (await ReceiptsAsync(rig)).Count);
        // details are read once: only the new receipt's
        Assert.Single(rig.Lidl.TicketRequests, r => r.Contains("/tickets/T-100", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_sync_that_fails_after_the_refresh_still_keeps_the_new_token()
    {
        await using var rig = await RigAsync();
        Assert.True((await rig.World.SyncAsync(BankWorld.Alice, rig.LoginId)).Succeeded);
        var old = await StoredRefreshTokenAsync(rig);
        rig.Lidl.TicketsDown = true;
        string? storedWhenFailing = null;
        rig.Lidl.OnTicketsDown = () => storedWhenFailing ??= StoredRefreshTokenAsync(rig).GetAwaiter().GetResult();

        var status = await rig.World.SyncAsync(BankWorld.Alice, rig.LoginId);

        Assert.False(status.Succeeded);
        Assert.Equal(["authorization_code", "refresh_token"], rig.Lidl.Grants);
        var stored = await StoredRefreshTokenAsync(rig);
        Assert.NotEqual(old, stored);
        Assert.Equal(rig.Lidl.CurrentRefresh, stored);
        // stored before the ticket API was asked, not only at the end of the sync
        Assert.Equal(stored, storedWhenFailing);

        // and the next sync gets on with it
        rig.Lidl.TicketsDown = false;
        Assert.True((await rig.World.SyncAsync(BankWorld.Alice, rig.LoginId)).Succeeded);
        Assert.Single(rig.Browser.Sessions);
    }

    [Fact]
    public async Task A_rejected_refresh_token_in_the_background_waits_for_the_owner_without_a_sign_in()
    {
        await using var rig = await RigAsync();
        Assert.True((await rig.World.SyncAsync(BankWorld.Alice, rig.LoginId)).Succeeded);
        rig.Lidl.CurrentRefresh = "revoked-elsewhere";

        Assert.Equal(StartResult.Started, rig.World.Coordinator.LaunchBackground(BankWorld.Alice,
            new SyncRequest(rig.LoginId, Transactions: true, Statements: false, Background: true), null));
        await rig.World.Coordinator.WhenIdleAsync(rig.LoginId);

        var status = rig.World.Coordinator.GetStatus(BankWorld.Alice, rig.LoginId)!;
        Assert.True(status.NeedsUser);
        Assert.Null(status.Challenge);
        Assert.Single(rig.Browser.Sessions);
        await using var db = await rig.World.T.System.CreateDbContextAsync();
        var login = await db.BankLogins.AsNoTracking().SingleAsync(l => l.Id == rig.LoginId);
        Assert.Equal(LidlPlusConnector.SignInAgain, login.NeedsManualSync);

        // the owner's own sync signs in again
        rig.Lidl.Code = "code-0002";
        Assert.True((await rig.World.SyncAsync(BankWorld.Alice, rig.LoginId)).Succeeded);
        Assert.Equal(2, rig.Browser.Sessions.Count);
        Assert.Equal(rig.Lidl.CurrentRefresh, await StoredRefreshTokenAsync(rig));
    }

    [Fact]
    public async Task A_background_sync_without_a_refresh_token_never_opens_the_sign_in()
    {
        await using var rig = await RigAsync();
        rig.World.Coordinator.LaunchBackground(BankWorld.Alice, new SyncRequest(rig.LoginId, Transactions: true, Statements: false, Background: true), null);
        await rig.World.Coordinator.WhenIdleAsync(rig.LoginId);

        Assert.True(rig.World.Coordinator.GetStatus(BankWorld.Alice, rig.LoginId)!.NeedsUser);
        Assert.Empty(rig.Browser.Sessions);
        Assert.Empty(rig.Lidl.Grants);
    }

    [Fact]
    public async Task A_callback_with_another_state_is_rejected_and_no_code_is_exchanged()
    {
        await using var rig = await RigAsync(state: sent => sent + "-forged");

        var status = await rig.World.SyncAsync(BankWorld.Alice, rig.LoginId);

        Assert.False(status.Succeeded);
        Assert.Contains("could not be verified", status.Error, StringComparison.Ordinal);
        Assert.Empty(rig.Lidl.Grants);
        Assert.Null(await StoredRefreshTokenAsync(rig));
        Assert.Empty(await ReceiptsAsync(rig));
    }

    // ---- matching ----

    private static readonly DateOnly Day = new(2026, 11, 3);

    [Theory]
    [InlineData(-490, 0, "LIDL DIENSTLEISTUNG", null, true)]
    [InlineData(-490, 5, "S. Payment Solutions GmbH", "Lidl sagt Danke", true)]
    [InlineData(-490, 6, "S. Payment Solutions GmbH", null, false)]
    [InlineData(-490, -1, "S. Payment Solutions GmbH", null, false)]
    [InlineData(-491, 1, "S. Payment Solutions GmbH", null, false)]
    [InlineData(490, 1, "S. Payment Solutions GmbH", null, false)]
    [InlineData(-490, 1, "Bakery", "card payment", false)]
    [InlineData(-490, 1, "REWE Markt GmbH", null, false)]
    [InlineData(-490, 2, "VISA Lidl sagt Danke", null, true)]
    [InlineData(-490, 2, "Kartenzahlung", "S. Digits Payment GmbH", true)]
    public void A_booking_fits_a_Lidl_receipt_by_amount_days_after_and_payee(long cents, int daysAfter, string name, string? purpose, bool fits)
    {
        var receipt = new ReceiptToMatch(1, 490, Day, Day, ReceiptShops.Lidl);
        var booking = new ReceiptBooking(10, Day.AddDays(daysAfter), cents, name, purpose);
        Assert.Equal(fits, ReceiptMatcher.Candidates(receipt, [booking]).Count == 1);
    }

    [Fact]
    public void A_Lidl_booking_is_not_taken_for_a_REWE_receipt()
    {
        var rewe = new ReceiptToMatch(1, 490, Day, Day);
        Assert.Empty(ReceiptMatcher.Candidates(rewe, [new ReceiptBooking(10, Day, -490, "S. Payment Solutions GmbH", "Lidl sagt Danke")]));
    }

    [Fact]
    public async Task A_receipt_waits_for_its_late_booking_and_only_the_owners_own()
    {
        await using var rig = await RigAsync();
        var store = new ReceiptStore();
        var receipt = LidlTickets.ParseDetail(FakeLidlPlus.Groceries("T-200", "2026-11-07T10:00:00"))!;
        await using var db = await rig.World.T.System.CreateDbContextAsync();
        // Bob's booking of the same amount is not Alice's
        db.Accounts.Add(new Account { Id = RowIds.Namespace(3, 900), OwnerUserId = BankWorld.Bob, Name = "Bob's" });
        db.Transactions.Add(new Transaction
        {
            Id = RowIds.Namespace(3, 901),
            OwnerUserId = BankWorld.Bob,
            AccountId = RowIds.Namespace(3, 900),
            Amount = -490,
            Timestamp = new DateTimeOffset(new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds(),
            Name = "Lidl",
        });
        await db.SaveChangesAsync();

        Assert.True(await ReceiptStore.AddAsync(db, BankWorld.Alice, ReceiptShops.Lidl, receipt.ToParsed(), null, DateTime.UtcNow, CancellationToken.None, receipt.Store));
        Assert.False(await ReceiptStore.AddAsync(db, BankWorld.Alice, ReceiptShops.Lidl, receipt.ToParsed(), null, DateTime.UtcNow, CancellationToken.None, receipt.Store));
        await store.MatchAsync(db, BankWorld.Alice, CancellationToken.None);
        Assert.Null((await db.ReceiptInvoices.AsNoTracking().SingleAsync(r => r.InvoiceNumber == "T-200")).TransactionId);

        // the card booking arrives two days later
        db.Transactions.Add(Booking(10, -490, new DateTime(2026, 11, 9), "S. Payment Solutions GmbH", "Lidl sagt Danke"));
        await db.SaveChangesAsync();
        await store.MatchAsync(db, BankWorld.Alice, CancellationToken.None);
        Assert.Equal(RowIds.Namespace(BankWorld.SourceAlice, 910), (await db.ReceiptInvoices.AsNoTracking().SingleAsync(r => r.InvoiceNumber == "T-200")).TransactionId);
    }
}
