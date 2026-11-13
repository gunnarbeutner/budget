using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenBudget.Connectors.Amazon;

/// <summary>Where amazon.de lives and how politely it is read; tests point these at a fake.</summary>
public sealed class AmazonOptions
{
    public Uri BaseUri { get; init; } = new("https://www.amazon.de/");

    /// <summary>The wallet's transaction list (its first page is rendered by the server).</summary>
    public string WalletPage { get; init; } = "cpe/yourpayments/transactions";

    /// <summary>Further wallet pages (JSON POST with the page key and the page's request context).</summary>
    public string WalletDataPath { get; init; } = "payments-portal/data/iris/live/v1/data/manage/get-transactions";

    /// <summary>The order details page; {0} is the order number.</summary>
    public string OrderDetailsPage { get; init; } = "gp/your-account/order-details?orderID={0}";

    /// <summary>The wait between two requests.</summary>
    public TimeSpan RequestDelay { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>At most this many wallet pages per sync.</summary>
    public int MaxWalletPages { get; init; } = 60;

    /// <summary>At most this many order details per sync; the rest follow next time.</summary>
    public int MaxOrderDetails { get; init; } = 150;

    public Uri OrderUrl(string orderId) => new(BaseUri, string.Format(CultureInfo.InvariantCulture, OrderDetailsPage, Uri.EscapeDataString(orderId)));
}

/// <summary>
/// Amazon (amazon.de) as an order source: it does not deliver bookings but enriches existing ones with what was
/// ordered. The owner signs in on Amazon's page in the app's browser (captcha and one-time code there); the browser is
/// kept for the sync. With stored cookies the wallet is tried first, so a session Amazon still accepts needs no
/// sign-in; background runs only use such a session.
///
/// The wallet's transaction list (see <see cref="AmazonPages"/>) gives the charges and refunds with order number,
/// payment instrument and amount; each instrument becomes an "account" the owner maps to an existing account. The
/// details of orders not seen before are read from their order pages, one request at a time with a pause in between.
/// A sign-in or captcha page in the middle stops the sync (background: for the owner; otherwise it asks to sign in).
/// </summary>
public sealed class AmazonConnector : IBankConnector, IOrderSource
{
    public const string TypeName = "amazon";
    internal const string WebLoginReference = "amazon-web-login";
    internal const string InstrumentsKey = "instruments";
    internal const string SignInAgain = "Amazon wants you to sign in again.";

    /// <summary>Signed in: the wallet shows and no sign-in form or captcha is on the page.</summary>
    internal const string SignedInCheck =
        "() => { const busy = document.querySelector('#ap_email, #ap_password, input[name=cvf_captcha_input], #auth-mfa-otpcode, form[action*=validateCaptcha]');"
        + " return { ok: location.pathname.includes('/cpe/yourpayments') && !busy, reason: busy ? 'a sign-in form is shown' : '' }; }";

    private readonly ConnectorContext _context;
    private readonly AmazonOptions _options;
    private ConnectorSession _session = new();
    private IBrowserFetcher? _browser;

    public AmazonConnector(ConnectorContext context, AmazonOptions options)
    {
        _context = context;
        _options = options;
    }

    public static ConnectorRegistration CreateRegistration(AmazonOptions options) => new(
        TypeName,
        "Amazon (order details)",
        "Amazon.de orders: adds what you ordered to the matching bookings of your card or bank account, so they can be categorized per item. It does not create accounts or bookings. You sign in on Amazon's own page (captcha and code there); nothing is stored but the session. Map each payment method to the account it is booked on.",
        ctx => new AmazonConnector(ctx, options),
        CredentialRequirement.None,
        Statements: false)
    {
        Silent = SilentCapability.SessionOnly,
        OrderSource = true,
    };

    public string ConnectorType => TypeName;

    private ILogger Log => _context.Logger;

    private DateOnly Today => DateOnly.FromDateTime(_context.Clock.GetUtcNow().UtcDateTime);

    internal WebLoginSpec WebLogin() => new(
        new Uri(_options.BaseUri, _options.WalletPage), [_options.BaseUri.Host], successUrlPrefix: new Uri(_options.BaseUri, _options.WalletPage))
    {
        SuccessCheck = SignedInCheck,
        KeepBrowser = true,
        ResumeUrl = new Uri(_options.BaseUri, _options.WalletPage),
        ObservedRequests = ["/ap/signin", "/ap/mfa", "validateCaptcha"],
    };

    private ConnectorChallenge AskSignIn() => ConnectorChallenge.WebLogin("Sign in on the Amazon page.", WebLogin(), WebLoginReference);

    public Task<ConnectorStep<ConnectorSession?>> LoginAsync(
        ConnectorCredentials credentials, ConnectorSession? storedSession, ChallengeAnswer? answer, CancellationToken ct)
    {
        if (answer == null)
        {
            _session = storedSession ?? new ConnectorSession();
            return Task.FromResult(ConnectorStep.Ask<ConnectorSession?>(AskSignIn()));
        }
        if (answer.Challenge.Reference != WebLoginReference || answer.Browser is not { IsOpen: true } browser)
            return Task.FromResult(ConnectorStep.Fail<ConnectorSession?>("The Amazon sign-in did not complete; please sync again."));
        _browser = browser;
        Log.LogInformation("Signed in to Amazon through the web login.");
        return Task.FromResult(ConnectorStep.Done<ConnectorSession?>(_session));
    }

    public Task<ConnectorSession?> LogoutAsync(CancellationToken ct) => Task.FromResult<ConnectorSession?>(_session);

    /// <summary>The payment instruments seen in earlier syncs (the new ones come with <see cref="FetchOrdersAsync"/>).</summary>
    public Task<IReadOnlyList<RemoteAccount>> ListAccountsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RemoteAccount>>(RememberedInstruments());

    private List<RemoteAccount> RememberedInstruments()
    {
        if (!_session.Values.TryGetValue(InstrumentsKey, out var json)) return [];
        try
        {
            return (JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [])
                .Select(kv => Instrument(kv.Key, kv.Value)).OrderBy(a => a.Name, StringComparer.Ordinal).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static RemoteAccount Instrument(string id, string label) =>
        new(id, label, AmazonPages.IsPointsLabel(label) ? RemoteAccountType.Other : RemoteAccountType.CreditCard, null, "EUR");

    public Task<ConnectorStep<AccountRefresh>> RefreshAsync(RemoteAccount account, DateOnly? since, ChallengeAnswer? answer, CancellationToken ct) =>
        Task.FromResult(ConnectorStep.Done(new AccountRefresh(null, [], [])));

    public Task<ConnectorStep<IReadOnlyList<RemoteStatement>>> FetchStatementsAsync(
        IReadOnlyList<RemoteAccount> accounts, IReadOnlySet<string> knownIdentifiers, ChallengeAnswer? answer, CancellationToken ct) =>
        Task.FromResult(ConnectorStep.Done<IReadOnlyList<RemoteStatement>>([]));

    private ConnectorStep<OrderSourceResult> NeedSignIn(bool answered)
    {
        if (_context.Silent) return ConnectorStep.NeedsUser<OrderSourceResult>(SignInAgain);
        if (answered) return ConnectorStep.Fail<OrderSourceResult>("The Amazon sign-in did not complete; please sync again.");
        return ConnectorStep.Ask<OrderSourceResult>(AskSignIn());
    }

    public async Task<ConnectorStep<OrderSourceResult>> FetchOrdersAsync(
        DateOnly since, IReadOnlySet<string> knownOrderIds, ChallengeAnswer? answer, CancellationToken ct)
    {
        if (answer?.Browser is { IsOpen: true } fresh) _browser = fresh;
        if (_browser is not { IsOpen: true } browser) return NeedSignIn(answer != null);

        // ---- wallet ----
        _context.Progress?.Invoke("Reading Amazon payments");
        var walletUrl = new Uri(_options.BaseUri, _options.WalletPage);
        var response = await browser.GetAsync(walletUrl, "text/html", ct);
        if (AmazonPages.IsSignInPage(response.Url, response.Text)) return NeedSignIn(answer != null);
        if (!response.IsSuccess) throw new ConnectorException($"Amazon's payment list did not load (HTTP {response.Status}).");
        var page = AmazonPages.ParseWalletHtml(response.Text)
            ?? throw new ConnectorException("Amazon's payment list could not be read; the page may have changed.");
        var context = AmazonPages.WalletRequestContext(response.Text);
        var charges = new List<RemoteCharge>(page.Charges);
        var coveredFrom = page.HasMore ? page.Oldest ?? Today : since;
        var pages = 1;
        while (page.HasMore && page.Oldest is { } oldest && oldest >= since && pages < _options.MaxWalletPages)
        {
            await Task.Delay(_options.RequestDelay, _context.Clock, ct);
            var next = await NextWalletPageAsync(browser, page.NextPageKey!, context, ct);
            if (next == null)
            {
                Log.LogWarning("Amazon: wallet page {Page} could not be read; payments before {Oldest} are left out this time.",
                    pages + 1, oldest.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            }
            page = next;
            pages++;
            charges.AddRange(page.Charges);
            coveredFrom = page.HasMore ? page.Oldest ?? coveredFrom : since;
            _context.Progress?.Invoke($"Reading Amazon payments ({pages} pages)");
        }
        if (coveredFrom < since) coveredFrom = since;
        charges = charges.Where(c => c.Date >= since).ToList();
        Log.LogInformation("Amazon: {Pages} wallet pages, {Count} payments since {Since}.", pages, charges.Count, coveredFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        // ---- instruments ----
        var instruments = RememberedInstruments().ToDictionary(a => a.RemoteId, a => a.Name, StringComparer.Ordinal);
        foreach (var c in charges) instruments[c.InstrumentId] = c.Instrument;
        _session.Values[InstrumentsKey] = JsonSerializer.Serialize(instruments);

        // ---- order details ----
        var wanted = charges.Where(c => !c.Skip).Select(c => c.OrderId).Distinct(StringComparer.Ordinal)
            .Where(id => !knownOrderIds.Contains(id)).Take(_options.MaxOrderDetails).ToList();
        var orders = new List<RemoteOrder>();
        foreach (var orderId in wanted)
        {
            _context.Progress?.Invoke($"Reading Amazon orders ({orders.Count + 1} of {wanted.Count})");
            await Task.Delay(_options.RequestDelay, _context.Clock, ct);
            var details = await browser.GetAsync(_options.OrderUrl(orderId), "text/html", ct);
            if (AmazonPages.IsSignInPage(details.Url, details.Text))
            {
                // What was read so far is kept; the remaining orders follow at the next sync.
                Log.LogWarning("Amazon: a sign-in page appeared after {Count} order pages; stopping.", orders.Count);
                if (_context.Silent) return ConnectorStep.NeedsUser<OrderSourceResult>(SignInAgain);
                break;
            }
            if (!details.IsSuccess)
            {
                Log.LogWarning("Amazon: an order page answered HTTP {Status}.", details.Status);
                continue;
            }
            orders.Add(AmazonPages.ParseOrderDetails(orderId, details.Text));
        }
        Log.LogInformation("Amazon: read {Count} order pages ({Items} items).", orders.Count, orders.Sum(o => o.Items.Count));
        return ConnectorStep.Done(new OrderSourceResult(
            charges, orders, instruments.Select(kv => Instrument(kv.Key, kv.Value)).OrderBy(a => a.Name, StringComparer.Ordinal).ToList(), coveredFrom));
    }

    /// <summary>
    /// A further wallet page, asked the way the page asks itself: a JSON POST with the page key and the page's request
    /// context. Null when the answer cannot be read.
    /// </summary>
    private async Task<AmazonWalletPage?> NextWalletPageAsync(IBrowserFetcher browser, string pageKey, JsonNode? context, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["manageWalletRequest"] = context?.DeepClone(),
            ["lastEvaluatedPageKey"] = pageKey,
        };
        var request = new BrowserFetchRequest("POST", new Uri(_options.BaseUri, _options.WalletDataPath))
        {
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "application/json",
                ["Accept"] = "application/json",
            },
            Body = body.ToJsonString(),
        };
        var response = await browser.SendAsync(request, ct);
        if (!response.IsSuccess) return null;
        try { return JsonNode.Parse(response.Text) is { } json ? AmazonPages.ParseWalletJson(json) : null; }
        catch (JsonException) { return null; }
    }
}
