#pragma warning disable CA2000 // test objects live for the test
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Amazon;
using OpenBudget.Connectors.Receipts;
using OpenBudget.Models;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>Amazon's pages (made-up orders and amounts), charge matching, split math and the connector over a scripted browser.</summary>
public sealed class AmazonTests
{
    private const string OrderA = "301-1111111-1111111";
    private const string OrderB = "302-2222222-2222222";
    private const string OrderC = "303-3333333-3333333";

    internal static JsonObject Entry(string amount, string date, string? order, string method, string? digits = null, string label = "Charged", string status = "Approved") => new()
    {
        ["formattedAmount"] = amount,
        ["formattedDate"] = date,
        ["statementDescriptor"] = "AMZN Mktp DE",
        ["statusInfo"] = new JsonObject { ["status"] = status, ["label"] = label, ["amountDetailsStatusType"] = "Charge" },
        ["orderData"] = order == null ? new JsonArray() : new JsonArray(new JsonObject
        {
            ["orderDisplayString"] = "Order #" + order,
            ["orderDetailsUrl"] = "https://shop.test/gp/css/summary?orderID=" + order,
        }),
        ["paymentMethodDisplayStringData"] = new JsonObject
        {
            ["paymentMethodName"] = method,
            ["paymentMethodNumber"] = digits == null ? null : new JsonObject { ["prefix"] = "••••", ["lastDigits"] = digits },
        },
    };

    /// <summary>The answer to a further-page request for the payments <paramref name="walletHtml"/> lists.</summary>
    internal static string NextPageAnswer(string walletHtml)
    {
        var state = AmazonPages.NextDataOf(walletHtml)!["props"]!["pageProps"]!["state"]!;
        return new JsonObject
        {
            ["type"] = "GetTransactions",
            ["displayResponse"] = new JsonObject
            {
                ["transactionsList"] = state["transactionResponseState"]!["visibleTransactionResponse"]!["transactionList"]!.DeepClone(),
                ["lastEvaluatedKey"] = state["lastEvaluatedPageKey"]?.DeepClone(),
            },
        }.ToJsonString();
    }

    internal static string WalletHtml(bool hasMore, params JsonObject[] entries)
    {
        var data = new JsonObject
        {
            ["props"] = new JsonObject
            {
                ["pageProps"] = new JsonObject
                {
                    ["token"] = "T-EXAMPLE",
                    ["request"] = new JsonObject { ["widgetName"] = "ViewTransactions" },
                    ["manageWalletRequest"] = new JsonObject
                    {
                        ["requestContext"] = new JsonObject
                        {
                            ["localeInfo"] = new JsonObject { ["locale"] = "en_GB" },
                            ["surfaceInfo"] = new JsonObject { ["surfaceType"] = "desktop", ["clientApplicationType"] = "browser", ["userAgent"] = "Example" },
                        },
                    },
                    ["state"] = new JsonObject
                    {
                        ["transactionResponseState"] = new JsonObject
                        {
                            ["visibleTransactionResponse"] = new JsonObject { ["transactionList"] = new JsonArray(entries) },
                        },
                        ["hasMore"] = hasMore,
                        ["lastEvaluatedPageKey"] = hasMore ? "page-2" : null,
                    },
                },
            },
        };
        return "<html><body><div id=\"__next\"></div><script id=\"__NEXT_DATA__\" type=\"application/json\">" + data.ToJsonString() + "</script></body></html>";
    }

    private static string Item(string asin, string title, int quantity, string price) =>
        $"""<div data-component="purchasedItems"><div data-component="itemImage"><a href="/-/en/dp/{asin}"><img src="x.jpg"></a></div>"""
        + $"""<div data-component="itemTitle"><a class="a-link-normal" href="/-/en/dp/{asin}?ref=x">{WebUtility.HtmlEncode(title)}</a></div>"""
        + """<div data-component="orderedMerchant">Sold by: Example Seller</div>"""
        + $"""<div data-component="quantity"><span>{quantity}</span></div><div data-component="unitPrice"><span>{price}</span></div></div>""";

    internal static string OrderHtml(string date, string grandTotal, params (int Shipment, string Html)[] items)
    {
        var sb = new StringBuilder("<html><body>");
        sb.Append(CultureInfo.InvariantCulture, $"""<div data-component="orderDate"><span>Order placed {date}</span></div>""");
        sb.Append("""<div data-component="chargeSummary"><div>Item(s) Subtotal: €1.00</div><div>Postage &amp; Packing: €0.00</div>""");
        sb.Append(CultureInfo.InvariantCulture, $"""<div>Grand Total:</div><div>{grandTotal}</div></div><div data-component="shipments">""");
        foreach (var shipment in items.GroupBy(i => i.Shipment))
        {
            sb.Append("""<div data-component="shipmentStatus">Delivered</div>""");
            foreach (var i in shipment) sb.Append(i.Html);
        }
        return sb.Append("</div></body></html>").ToString();
    }

    // ---- pages ----

    [Fact]
    public void The_wallet_lists_charges_refunds_points_and_bank_payments()
    {
        var page = AmazonPages.ParseWalletHtml(WalletHtml(true,
            Entry("-€10.60", "12 Nov 2026", OrderA, "Amazon Visa", "1111"),
            Entry("-€2.40", "12 Nov 2026", OrderA, "Example Points"),
            Entry("+€5.00", "26 Sept 2026", OrderB, "Amazon Visa", "1111", label: "Refunded"),
            Entry("-8,99 €", "3. Okt. 2026", "D01-4444444-4444444", "EXAMPLE BANK AG", "8340"),
            Entry("-€3.00", "1 Oct 2026", OrderC, "Amazon Visa", "1111", status: "Declined"),
            Entry("-€1.00", "1 Oct 2026", null, "Amazon Visa", "1111")))!;

        Assert.True(page.HasMore);
        Assert.Equal("page-2", page.NextPageKey);
        Assert.Equal(new DateOnly(2026, 9, 26), page.Oldest);
        Assert.Equal(4, page.Charges.Count); // the declined and the order-less entries are left out
        var card = page.Charges[0];
        Assert.Equal((OrderA, new DateOnly(2026, 11, 12), -10.60m, "amazon-visa-1111", "Amazon Visa •••• 1111", false),
            (card.OrderId, card.Date, card.Amount, card.InstrumentId, card.Instrument, card.Skip));
        Assert.True(page.Charges[1].Skip); // points
        Assert.Equal(5.00m, page.Charges[2].Amount);
        Assert.Equal((-8.99m, new DateOnly(2026, 10, 3), "example-bank-ag-8340"), (page.Charges[3].Amount, page.Charges[3].Date, page.Charges[3].InstrumentId));
    }

    [Theory]
    [InlineData("12 Nov 2026", 2026, 11, 12)]
    [InlineData("26 Sept 2026", 2026, 9, 26)]
    [InlineData("3. März 2026", 2026, 3, 3)]
    [InlineData("November 3, 2026", 2026, 11, 3)]
    [InlineData("03.11.2026", 2026, 11, 3)]
    public void Dates_in_either_language_are_read(string text, int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), AmazonPages.ParseDate(text));

    [Fact]
    public void Items_under_a_return_status_are_returned()
    {
        var html = OrderHtml("1 Mar 2026", "€30.00", (0, Item("B000000001", "Desk lamp", 1, "€20.00")), (1, Item("B000000002", "Cable", 1, "€10.00")))
            .Replace("""<div data-component="shipmentStatus">Delivered</div>""", "", StringComparison.Ordinal);
        var marker = """<div data-component="itemImage"><a href="/-/en/dp/B000000002">""";
        html = html.Replace("""<div data-component="purchasedItems">""" + marker,
            """<div data-component="shipmentStatus"><h4 class="a-color-base od-status-message"><span>Return complete</span></h4></div><div data-component="purchasedItems">""" + marker, StringComparison.Ordinal);
        html = html.Replace("""<div data-component="shipments">""", """<div data-component="shipments"><div data-component="shipmentStatus"><h4 class="od-status-message"><span>Delivered</span></h4></div>""", StringComparison.Ordinal);

        var order = AmazonPages.ParseOrderDetails(OrderA, html);

        Assert.Equal([("Desk lamp", false), ("Cable", true)], order.Items.Select(i => (i.Title, i.Returned)));
    }

    [Fact]
    public void A_refund_takes_the_returned_items_else_the_items_adding_up_to_it()
    {
        SplitItem Line(long id, long cents, bool returned = false) => new(id, "Item " + id, 1, cents, 0, returned);
        long[] charges = [-6000];

        Assert.Equal([2L], AmazonShipments.ItemsForCharge([Line(1, 1000), Line(2, 2000), Line(3, 3000)], 2000, charges, 0, 6000).Items.Select(i => i.Id));
        // 30.00 is item 3 or items 1 and 2: the return status decides.
        Assert.Equal([1L, 2L], AmazonShipments.ItemsForCharge([Line(1, 1000, true), Line(2, 2000, true), Line(3, 3000)], 3000, charges, 0, 6000).Items.Select(i => i.Id));
        Assert.Equal(3, AmazonShipments.ItemsForCharge([Line(1, 1000), Line(2, 2000), Line(3, 3000)], 3000, charges, 0, 6000).Items.Count);
    }

    [Fact]
    public void Order_details_give_items_with_quantity_price_and_shipment()
    {
        var html = OrderHtml("3 November 2026", "€38.47",
            (0, Item("B000000001", "USB-C charger", 1, "€12.99")),
            (0, Item("B000000002", "Laundry detergent & softener", 2, "€9.99")),
            (1, Item("B000000003", "Notebook", 1, "€5.50")));
        var order = AmazonPages.ParseOrderDetails(OrderA, html);
        Assert.Equal(new DateOnly(2026, 11, 3), order.Date);
        Assert.Equal(38.47m, order.Total);
        Assert.Equal(3, order.Items.Count);
        Assert.Equal(("USB-C charger", 1, 12.99m, "B000000001", 0), (order.Items[0].Title, order.Items[0].Quantity, order.Items[0].Price, order.Items[0].Asin, order.Items[0].Shipment));
        Assert.Equal(("Laundry detergent & softener", 2, 9.99m), (order.Items[1].Title, order.Items[1].Quantity, order.Items[1].Price));
        Assert.Equal(1, order.Items[2].Shipment);
    }

    /// <summary>An item as amazon.de shows it today: an empty quantity element, the count only as a badge on the image (above one).</summary>
    private static string BadgeItem(string asin, string title, int quantity, string price) =>
        $"""<div data-component="purchasedItems"><div data-component="itemImage"><a href="/-/en/dp/{asin}"><img src="x.jpg"></a>"""
        + (quantity > 1 ? $"""<div class="od-item-view-qty"><span>{quantity}</span></div>""" : "") + "</div>"
        + $"""<div data-component="itemTitle"><a class="a-link-normal" href="/-/en/dp/{asin}?ref=x">{WebUtility.HtmlEncode(title)}</a></div>"""
        + """<div data-component="orderedMerchant">Sold by: Example Seller</div>"""
        + $"""<div class="" data-component="quantity">\n    \n  </div><div data-component="unitPrice"><span>{price}</span></div></div>""";

    [Fact]
    public void The_quantity_comes_from_the_image_badge_and_never_from_the_price()
    {
        var html = OrderHtml("28 October 2026", "€13.52",
            (0, BadgeItem("B000000011", "Example coin cells 4 pack", 1, "€5.52")),
            (1, BadgeItem("B000000012", "Example coin cells 6 pack", 2, "€4.00")));
        var order = AmazonPages.ParseOrderDetails(OrderA, html);
        Assert.Equal([(1, 5.52m), (2, 4.00m)], order.Items.Select(i => (i.Quantity, i.Price!.Value)));
    }

    [Fact]
    public void A_payment_for_several_orders_lists_them_all_in_order()
    {
        var keyed = Entry("-€63.99", "12 Nov 2026", null, "Visa", "1111");
        keyed["orderData"] = new JsonObject
        {
            ["1"] = new JsonObject { ["orderDisplayString"] = "Order #" + OrderB },
            ["0"] = new JsonObject { ["orderDisplayString"] = "Order #" + OrderA },
        };
        var listed = Entry("-€20.00", "11 Nov 2026", null, "Visa", "1111");
        listed["orderData"] = new JsonArray(new JsonObject { ["orderDisplayString"] = "Order #" + OrderC }, new JsonObject { ["orderDetailsUrl"] = "https://shop.test/x?orderID=" + OrderA });
        var page = AmazonPages.ParseWalletJson(new JsonObject { ["transactionList"] = new JsonArray(keyed, listed, Entry("-€5.00", "10 Nov 2026", OrderB, "Visa", "1111")) })!;

        Assert.Equal([(OrderA, new[] { OrderB }), (OrderC, new[] { OrderA }), (OrderB, Array.Empty<string>())],
            page.Charges.Select(c => (c.OrderId, c.OtherOrderIds.ToArray())));
        Assert.Equal([OrderA, OrderB], page.Charges[0].OrderIds);
    }

    [Fact]
    public void A_digital_order_page_without_items_still_reads()
    {
        var order = AmazonPages.ParseOrderDetails("D01-4444444-4444444", "<html><body>Your membership</body></html>");
        Assert.Empty(order.Items);
    }

    [Fact]
    public void Sign_in_and_captcha_pages_are_recognised()
    {
        Assert.True(AmazonPages.IsSignInPage(new Uri("https://shop.test/ap/signin?x=1"), ""));
        Assert.True(AmazonPages.IsSignInPage(new Uri("https://shop.test/errors/validateCaptcha"), ""));
        Assert.True(AmazonPages.IsSignInPage(new Uri("https://shop.test/cpe/yourpayments/transactions"), "<input id=\"ap_password\">"));
        Assert.False(AmazonPages.IsSignInPage(new Uri("https://shop.test/cpe/yourpayments/transactions"), WalletHtml(false)));
    }

    // ---- matching ----

    private static readonly DateOnly Day = new(2026, 11, 10);

    /// <summary>An Amazon charge on account <paramref name="account"/> (signed cents as the wallet shows them).</summary>
    private static ReceiptToMatch Charge(long id, long account, DateOnly day, long cents) => new(id, -cents, null, day, ReceiptShops.Amazon, account);

    private static ReceiptBooking Booking(long id, long account, DateOnly day, long cents, bool shopLike, bool Pending = false) =>
        new(id, day, cents, shopLike ? "AMAZON EU S.A R.L." : "Card payment", null, account, Pending);

    [Fact]
    public void A_charge_matches_the_same_amount_from_seven_days_before_to_nine_days_after()
    {
        var charges = new[] { Charge(1, 7, Day, -1060) };
        Assert.Equal(10, ReceiptMatcher.Match(charges, [Booking(10, 7, Day.AddDays(-7), -1060, false)]).Matches[1]);
        Assert.Equal(10, ReceiptMatcher.Match(charges, [Booking(10, 7, Day.AddDays(9), -1060, false)]).Matches[1]);
        Assert.Empty(ReceiptMatcher.Match(charges, [Booking(10, 7, Day.AddDays(10), -1060, false)]).Matches);
        // a pending booking carries the bank's expected booking day, often a week or more later
        Assert.Equal(10, ReceiptMatcher.Match(charges, [Booking(10, 7, Day.AddDays(9), -1060, false, Pending: true)]).Matches[1]);
        Assert.Empty(ReceiptMatcher.Match(charges, [Booking(10, 7, Day.AddDays(15), -1060, false, Pending: true)]).Matches);
        Assert.Empty(ReceiptMatcher.Match(charges, [Booking(10, 7, Day.AddDays(-8), -1060, false)]).Matches);
        Assert.Empty(ReceiptMatcher.Match(charges, [Booking(10, 7, Day, -1061, false)]).Matches);
        Assert.Empty(ReceiptMatcher.Match(charges, [Booking(10, 8, Day, -1060, false)]).Matches); // another account
        Assert.Empty(ReceiptMatcher.Match(charges, [Booking(10, 7, Day, 1060, false)]).Matches); // the sign counts
    }

    [Fact]
    public void Shop_bookings_win_and_equal_candidates_stay_ambiguous()
    {
        var charges = new[] { Charge(1, 7, Day, -1060) };
        var preferred = ReceiptMatcher.Match(charges, [Booking(10, 7, Day, -1060, false), Booking(11, 7, Day, -1060, true)]);
        Assert.Equal(11, preferred.Matches[1]);

        var unclear = ReceiptMatcher.Match(charges, [Booking(10, 7, Day, -1060, true), Booking(11, 7, Day.AddDays(1), -1060, true)]);
        Assert.Empty(unclear.Matches);
        Assert.Contains(1L, unclear.Ambiguous);
    }

    [Fact]
    public void Two_charges_on_one_booking_leave_both_open_but_shipments_with_their_own_bookings_match()
    {
        var both = ReceiptMatcher.Match([Charge(1, 7, Day, -500), Charge(2, 7, Day.AddDays(1), -500)],
            [Booking(10, 7, Day.AddDays(1), -500, true)]);
        Assert.Empty(both.Matches);
        Assert.Equal([1L, 2L], both.Ambiguous.Order());

        // Two shipments of one order: different amounts, each its own booking; a refund matches money coming back.
        var shipments = ReceiptMatcher.Match(
            [Charge(1, 7, Day, -1247), Charge(2, 7, Day.AddDays(3), -64900), Charge(3, 7, Day.AddDays(9), 1247)],
            [Booking(10, 7, Day.AddDays(1), -1247, true), Booking(11, 7, Day.AddDays(4), -64900, true), Booking(12, 7, Day.AddDays(10), 1247, true)]);
        Assert.Equal(new Dictionary<long, long> { [1] = 10, [2] = 11, [3] = 12 }, shipments.Matches);
    }

    [Fact]
    public void Repeated_charges_of_one_amount_pair_with_the_booking_nearest_in_date()
    {
        // A monthly charge of one amount: each charge's window holds both bookings, but one is clearly nearer.
        var outcome = ReceiptMatcher.Match([Charge(1, 7, Day, -899), Charge(2, 7, Day.AddDays(14), -899)],
            [Booking(10, 7, Day.AddDays(1), -899, true), Booking(11, 7, Day.AddDays(15), -899, true)]);
        Assert.Equal(10, outcome.Matches[1]);
        Assert.Equal(11, outcome.Matches[2]);
    }

    [Fact]
    public void Twin_charges_of_one_order_take_their_bookings_in_order_and_a_spare_twin_stays_open()
    {
        static ReceiptToMatch Twin(long id, string order) => new(id, 500, null, Day, ReceiptShops.Amazon, 7, order);
        var two = ReceiptMatcher.Match([Twin(1, "A"), Twin(2, "A")], [Booking(10, 7, Day.AddDays(1), -500, true), Booking(11, 7, Day.AddDays(1), -500, true)]);
        Assert.Equal(new Dictionary<long, long> { [1] = 10, [2] = 11 }, two.Matches);

        var one = ReceiptMatcher.Match([Twin(1, "A"), Twin(2, "A")], [Booking(10, 7, Day.AddDays(1), -500, true)]);
        Assert.Equal(10, Assert.Single(one.Matches).Value);
        Assert.Empty(one.Ambiguous);

        // Equal charges of different orders are not twins.
        var apart = ReceiptMatcher.Match([Twin(1, "A"), Twin(2, "B")], [Booking(10, 7, Day.AddDays(1), -500, true)]);
        Assert.Empty(apart.Matches);
    }

    [Fact]
    public void A_settled_charge_frees_its_booking_for_another()
    {
        // Charge 1 can only be booking 10; then charge 2 has only booking 11 left.
        var outcome = ReceiptMatcher.Match([Charge(1, 7, Day.AddDays(-12), -500), Charge(2, 7, Day, -500)],
            [Booking(10, 7, Day.AddDays(-10), -500, true), Booking(11, 7, Day.AddDays(2), -500, true)]);
        Assert.Equal(10, outcome.Matches[1]);
        Assert.Equal(11, outcome.Matches[2]);
    }

    // ---- split math ----

    [Fact]
    public void Shares_follow_the_prices_and_the_rounding_rest_goes_to_the_largest()
    {
        Assert.Equal([-332, -336, -332], ReceiptCategorizer.Proportion(-1000, [100, 101, 100]));
        Assert.Equal([-1247, -64900], ReceiptCategorizer.Proportion(-66147, [1247, 64900]));
        // Shipping and points spread alike: 20 € of items charged as 18 € after points.
        Assert.Equal([-900, -900], ReceiptCategorizer.Proportion(-1800, [1000, 1000]));
        Assert.Equal([-500, -500], ReceiptCategorizer.Proportion(-1000, [null, null])); // no prices: equal shares
        Assert.Equal([-333, -334, -334], ReceiptCategorizer.Proportion(-1001, [1, 1, 1])); // the rounding rest lands on one share
        Assert.Equal([700, 300], ReceiptCategorizer.Proportion(1000, [70, 30])); // refunds keep their sign
    }

    private static readonly SplitItem Snacks = new(1, "Salted snack mix", 1, 1247, Shipment: 0);
    private static readonly SplitItem Fridge = new(2, "Fridge freezer combination", 1, 64900, Shipment: 1);

    [Fact]
    public void Separate_shipments_give_each_charge_its_own_items()
    {
        var items = new[] { Snacks, Fridge };
        long[] charges = [-1247, -64900];
        var first = AmazonShipments.ItemsForCharge(items, -1247, charges, 0, 66147);
        var second = AmazonShipments.ItemsForCharge(items, -64900, charges, 0, 66147);
        Assert.Equal([1L], first.Items.Select(i => i.Id));
        Assert.Equal([2L], second.Items.Select(i => i.Id));
        Assert.False(first.Unclear || second.Unclear);
    }

    [Fact]
    public void One_charge_for_the_whole_order_covers_every_item()
    {
        var paid = AmazonShipments.ItemsForCharge([Snacks, Fridge with { Shipment = 0 }], -66147, [-66147], 0, 66147);
        Assert.Equal(2, paid.Items.Count);
        Assert.False(paid.Unclear);
    }

    [Fact]
    public void Points_lower_the_charge_of_a_shipment()
    {
        // The fridge shipment was paid 640.00 by card and 9.00 with points.
        var paid = AmazonShipments.ItemsForCharge([Snacks, Fridge], -64000, [-1247, -64000], 900, 66147);
        Assert.Equal([2L], paid.Items.Select(i => i.Id));
    }

    [Fact]
    public void A_charge_that_fits_no_shipment_is_unclear_and_equal_charges_are_instalments()
    {
        var unclear = AmazonShipments.ItemsForCharge([Snacks, Fridge], -30000, [-30000, -37147], 0, 66147);
        Assert.True(unclear.Unclear);
        Assert.Equal(2, unclear.Items.Count);

        var instalment = AmazonShipments.ItemsForCharge([Snacks, Fridge with { Shipment = 0 }], -22049, [-22049, -22049, -22049], 0, 66147);
        Assert.True(instalment.Instalment);
        Assert.Equal(2, instalment.Items.Count);
    }

    // ---- connector ----

    private static AmazonConnector Connector(bool silent = false) => new(
        new ConnectorContext(new BankHttpClient(), TimeProvider.System, NullLogger.Instance) { Silent = silent },
        new AmazonOptions { BaseUri = new Uri("https://shop.test/"), RequestDelay = TimeSpan.Zero, WalletPageDelay = TimeSpan.Zero, WalletRetryDelay = TimeSpan.Zero });

    private static async Task<AmazonConnector> SignedInAsync(IBrowserFetcher browser, bool silent = false)
    {
        var connector = Connector(silent);
        var creds = new ConnectorCredentials("", "");
        var ask = await connector.LoginAsync(creds, null, null, CancellationToken.None);
        Assert.Equal(StepStatus.Challenge, ask.Status);
        Assert.True(ask.Challenge!.WebLoginSpec!.KeepBrowser);
        var done = await connector.LoginAsync(creds, null, new ChallengeAnswer(ask.Challenge, null) { Browser = browser }, CancellationToken.None);
        Assert.Equal(StepStatus.Done, done.Status);
        return connector;
    }

    [Fact]
    public async Task A_further_page_answered_with_something_else_is_asked_for_again()
    {
        var shop = new ScriptedShop { OddAnswers = 1 };
        shop.Pages["/cpe/yourpayments/transactions"] = WalletHtml(true, Entry("-€10.60", "12 Nov 2026", OrderA, "Amazon Visa", "1111"));
        shop.NextPage = WalletHtml(false, Entry("-€649.00", "2 Oct 2026", OrderB, "Visa", "2222"));
        shop.Pages["/gp/your-account/order-details"] = OrderHtml("1 Oct 2026", "€649.00", (0, Item("B000000009", "Fridge freezer combination", 1, "€649.00")));
        var connector = await SignedInAsync(shop);

        var step = await connector.FetchOrdersAsync(new DateOnly(2025, 11, 13), new HashSet<string>(StringComparer.Ordinal), [], null, CancellationToken.None);

        Assert.Equal(2, step.Value!.Charges.Count);
        Assert.Equal(0, shop.OddAnswers);
    }

    private static string KeyedAnswer(string? nextKey, params JsonObject[] entries) => new JsonObject
    {
        ["displayResponse"] = new JsonObject { ["transactionsList"] = new JsonArray(entries), ["lastEvaluatedKey"] = nextKey },
    }.ToJsonString();

    [Fact]
    public async Task A_read_of_the_whole_list_that_stopped_continues_where_it_stopped()
    {
        var shop = new ScriptedShop();
        shop.Pages["/cpe/yourpayments/transactions"] = WalletHtml(true, Entry("-€10.60", "12 Nov 2026", OrderA, "Amazon Visa", "1111"));
        shop.ByKey = new(StringComparer.Ordinal) { ["page-2"] = KeyedAnswer("page-3", Entry("-€649.00", "2 Oct 2026", OrderB, "Visa", "2222")) };
        var connector = await SignedInAsync(shop);
        var since = new DateOnly(2024, 1, 1);
        var none = new HashSet<string>([OrderA, OrderB, OrderC], StringComparer.Ordinal);

        // Amazon refuses page 3: the read keeps what it got and covers the days from the day after the last one read.
        var first = (await connector.FetchOrdersAsync(since, none, [], null, CancellationToken.None)).Value!;
        Assert.Equal((2, new DateOnly(2026, 10, 3)), (first.Charges.Count, first.CoveredFrom));

        // The next read jumps from the recent pages to page 3; the days in between it does not read again.
        shop.ByKey["page-3"] = KeyedAnswer(null, Entry("-€1.00", "2 Sept 2025", OrderC, "Visa", "2222"));
        shop.Keys.Clear();
        var second = (await connector.FetchOrdersAsync(since, none, [], null, CancellationToken.None)).Value!;
        Assert.Equal(["page-3"], shop.Keys);
        Assert.Equal((since, (new DateOnly(2025, 9, 2), new DateOnly(2026, 11, 12))), (second.CoveredFrom, second.Skipped));
        Assert.Equal([OrderA, OrderC], second.Charges.Select(c => c.OrderId));

        // That read reached the end: the next one pages through from the start again.
        shop.Keys.Clear();
        var third = (await connector.FetchOrdersAsync(since, none, [], null, CancellationToken.None)).Value!;
        Assert.Equal(["page-2", "page-3"], shop.Keys);
        Assert.Null(third.Skipped);
    }

    [Fact]
    public async Task A_position_Amazon_no_longer_accepts_falls_back_to_paging_through()
    {
        var shop = new ScriptedShop();
        shop.Pages["/cpe/yourpayments/transactions"] = WalletHtml(true, Entry("-€10.60", "12 Nov 2026", OrderA, "Amazon Visa", "1111"));
        shop.ByKey = new(StringComparer.Ordinal) { ["page-2"] = KeyedAnswer("page-3", Entry("-€649.00", "2 Oct 2026", OrderB, "Visa", "2222")) };
        var connector = await SignedInAsync(shop);
        var none = new HashSet<string>([OrderA, OrderB, OrderC], StringComparer.Ordinal);
        await connector.FetchOrdersAsync(new DateOnly(2024, 1, 1), none, [], null, CancellationToken.None);

        shop.ByKey = new(StringComparer.Ordinal) { ["page-2"] = KeyedAnswer(null, Entry("-€649.00", "2 Oct 2026", OrderB, "Visa", "2222")) };
        shop.Keys.Clear();
        var result = (await connector.FetchOrdersAsync(new DateOnly(2024, 1, 1), none, [], null, CancellationToken.None)).Value!;

        Assert.Equal(["page-3", "page-3", "page-2"], shop.Keys); // asked once more, then read on from the recent pages
        Assert.Null(result.Skipped);
        Assert.Equal(2, result.Charges.Count);
    }

    [Fact]
    public async Task A_sync_pages_through_the_wallet_and_reads_new_orders_once()
    {
        var shop = new ScriptedShop();
        shop.Pages["/cpe/yourpayments/transactions"] = WalletHtml(true,
            Entry("-€10.60", "12 Nov 2026", OrderA, "Amazon Visa", "1111"),
            Entry("-€2.40", "12 Nov 2026", OrderA, "Example Points"));
        shop.NextPage = WalletHtml(false, Entry("-€649.00", "2 Oct 2026", OrderB, "Visa", "2222"), Entry("-€1.00", "2 Oct 2024", OrderC, "Visa", "2222"));
        shop.Pages["/gp/your-account/order-details"] = OrderHtml("1 Oct 2026", "€649.00", (0, Item("B000000009", "Fridge freezer combination", 1, "€649.00")));
        var connector = await SignedInAsync(shop);

        var step = await connector.FetchOrdersAsync(new DateOnly(2025, 11, 13), new HashSet<string>([OrderA], StringComparer.Ordinal), [OrderC], null, CancellationToken.None);

        Assert.Equal(StepStatus.Done, step.Status);
        var result = step.Value!;
        Assert.Equal(3, result.Charges.Count); // the 2024 charge is before the start
        Assert.Equal(["/gp/your-account/order-details"], shop.Requested.Where(r => r.Contains("order-details", StringComparison.Ordinal)).Select(r => r.Split('?')[0]).Distinct(StringComparer.Ordinal));
        Assert.Contains(shop.Requested, r => r.EndsWith("orderID=" + OrderB, StringComparison.Ordinal));
        Assert.DoesNotContain(shop.Requested, r => r.EndsWith("orderID=" + OrderA, StringComparison.Ordinal)); // known already
        Assert.Contains(shop.Requested, r => r.EndsWith("orderID=" + OrderC, StringComparison.Ordinal)); // stored without details
        Assert.Equal([OrderB, OrderC], result.Orders.Select(o => o.OrderId));
        Assert.Equal("Fridge freezer combination", result.Orders[0].Items[0].Title);
        Assert.Equal(["Amazon Visa •••• 1111", "Example Points", "Visa •••• 2222"], result.Instruments.Select(i => i.Name));
        var body = JsonNode.Parse(shop.PostBody)!;
        Assert.Equal("page-2", (string?)body["exclusiveStartKey"]);
        Assert.Equal("GetTransactions", (string?)body["type"]);
        Assert.Equal("en_GB", (string?)body["locale"]);
        Assert.Equal("ViewTransactions", (string?)body["widgetName"]);
        Assert.Equal("desktop", (string?)body["surfaceInfo"]?["surfaceType"]);
        Assert.Equal("T-EXAMPLE", shop.PostToken);

        // The instruments are remembered for the next listing.
        var session = await connector.LogoutAsync(CancellationToken.None);
        var next = Connector();
        await next.LoginAsync(new ConnectorCredentials("", ""), session, null, CancellationToken.None);
        Assert.Equal(3, (await next.ListAccountsAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task A_sign_in_page_stops_a_background_sync_for_the_owner_and_asks_otherwise()
    {
        var shop = new ScriptedShop { SignIn = true };
        var background = await (await SignedInAsync(shop, silent: true)).FetchOrdersAsync(new DateOnly(2026, 1, 1), new HashSet<string>(StringComparer.Ordinal), [], null, CancellationToken.None);
        Assert.Equal(StepStatus.NeedsUser, background.Status);

        var manual = await (await SignedInAsync(shop)).FetchOrdersAsync(new DateOnly(2026, 1, 1), new HashSet<string>(StringComparer.Ordinal), [], null, CancellationToken.None);
        Assert.Equal(StepStatus.Challenge, manual.Status);
        Assert.Equal(ChallengeKind.WebLogin, manual.Challenge!.Kind);
    }

    private sealed class ScriptedShop : IBrowserFetcher
    {
        public Dictionary<string, string> Pages { get; } = new(StringComparer.Ordinal);
        public string? NextPage { get; set; }
        public bool SignIn { get; init; }
        public List<string> Requested { get; } = [];
        public List<string> Keys { get; } = [];
        public string PostBody { get; private set; } = "";
        public string? PostToken { get; private set; }

        /// <summary>Further pages by the key they follow (a missing key answers HTTP 429), used instead of <see cref="NextPage"/>.</summary>
        public Dictionary<string, string>? ByKey { get; set; }

        /// <summary>How many further-page requests are answered with JSON that holds no payment list.</summary>
        public int OddAnswers { get; set; }

        /// <summary>A further page as Amazon answers it: <c>displayResponse.transactionsList</c> and <c>lastEvaluatedKey</c>.</summary>
        public string? NextPageJson => NextPage == null ? null : NextPageAnswer(NextPage);
        public bool IsOpen => true;

        public Task<Uri?> ResolveRedirectAsync(Uri url, CancellationToken ct) => Task.FromResult<Uri?>(null);

        public Task<BrowserFetchResponse> SendAsync(BrowserFetchRequest request, CancellationToken ct)
        {
            Requested.Add(request.Url.PathAndQuery);
            if (SignIn) return Answer(200, new Uri("https://shop.test/ap/signin"), "<form><input id=\"ap_email\"></form>");
            if (request.Method == "POST")
            {
                PostBody = request.Body ?? "";
                PostToken = request.Headers?.GetValueOrDefault("x-amzn-upx-token");
                if (OddAnswers > 0)
                {
                    OddAnswers--;
                    return Answer(200, request.Url, "{\"type\":\"Throttled\"}");
                }
                if (ByKey != null)
                {
                    var key = (string?)JsonNode.Parse(PostBody)?["exclusiveStartKey"] ?? "";
                    Keys.Add(key);
                    return ByKey.TryGetValue(key, out var json) ? Answer(200, request.Url, json) : Answer(429, request.Url, "");
                }
                return Answer(NextPage == null ? 500 : 200, request.Url, NextPageJson ?? "");
            }
            return Pages.TryGetValue(request.Url.AbsolutePath, out var html) ? Answer(200, request.Url, html) : Answer(404, request.Url, "");
        }

        private static Task<BrowserFetchResponse> Answer(int status, Uri url, string body) =>
            Task.FromResult(new BrowserFetchResponse(status, url, "text/html", Encoding.UTF8.GetBytes(body)));
    }
}
