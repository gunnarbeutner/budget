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

    /// <summary>How often a further wallet page that could not be read is asked for again.</summary>
    public int WalletPageRetries { get; init; } = 4;

    /// <summary>The wait between two wallet pages; Amazon throttles a fast read of a long payment list.</summary>
    public TimeSpan WalletPageDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The first pause before asking for an unread wallet page again; it doubles with each attempt.</summary>
    public TimeSpan WalletRetryDelay { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>At most this many wallet pages per sync.</summary>
    public int MaxWalletPages { get; init; } = 300;

    public Uri OrderUrl(string orderId) => new(BaseUri, string.Format(CultureInfo.InvariantCulture, OrderDetailsPage, Uri.EscapeDataString(orderId)));
}

/// <summary>
/// Amazon (amazon.de) as an order source: it does not deliver bookings but enriches existing ones with what was
/// ordered. Sign-in: Amazon's page in the app's browser, kept for the sync. The app fills the stored e-mail address (or
/// phone number) and password and ticks "keep me signed in"; the one-time code page gets the code of a stored TOTP
/// secret (and "don't ask on this browser" ticked), otherwise the code is asked from the user. A captcha, an approval
/// on another device or any other page is shown to the user (see <see cref="AmazonSignIn"/>). With stored cookies the
/// wallet is tried first, so a session Amazon still accepts needs no sign-in. Background runs sign in only with a
/// stored TOTP secret; without one they use a still valid session.
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
    internal const string WalletResumeKey = "walletResume";
    internal const string SignInAgain = "Amazon wants you to sign in again.";

    /// <summary>Signed in: the wallet shows and no sign-in form or captcha is on the page.</summary>
    internal const string SignedInCheck =
        "() => { const busy = document.querySelector('#ap_email, #ap_password, input[name=cvf_captcha_input], #auth-mfa-otpcode, form[action*=validateCaptcha]');"
        + " return { ok: location.pathname.includes('/cpe/yourpayments') && !busy, reason: busy ? 'a sign-in form is shown' : '' }; }";

    private readonly ConnectorContext _context;
    private readonly AmazonOptions _options;
    private ConnectorSession _session = new();
    private ConnectorCredentials? _credentials;
    private IBrowserFetcher? _browser;

    public AmazonConnector(ConnectorContext context, AmazonOptions options)
    {
        _context = context;
        _options = options;
    }

    public static ConnectorRegistration CreateRegistration(AmazonOptions options) => new(
        TypeName,
        "Amazon (order details)",
        "Amazon.de orders: adds what you ordered to the matching bookings of your card or bank account, so they can be categorized per item. It does not create accounts or bookings. Sign in with your Amazon e-mail address (or phone number) and password: the app fills them in on Amazon's own page; a captcha or an approval on your phone is done there by you. Optionally store a TOTP secret so the one-time code is entered automatically and syncs run unattended: in Amazon open Login & security › 2-Step Verification, add an authenticator app and choose \"Can't scan the barcode?\" to see the key. Map each payment method to the account it is booked on.",
        ctx => new AmazonConnector(ctx, options),
        CredentialRequirement.UsernameAndSecret,
        Statements: false)
    {
        ExtraCredentials = [new CredentialField(AmazonSignIn.TotpKey, "TOTP secret (optional)", Normalize: NormalizeTotp)],
        // with a stored TOTP secret the sign-in needs nobody; without, only a still valid session can be reused
        Silent = SilentCapability.SessionOnly,
        SilentFor = credentials => TotpSecret.TryParse(credentials?.ExtraValue(AmazonSignIn.TotpKey), out _) ? SilentCapability.Full : SilentCapability.SessionOnly,
        SilentWebSignIn = true,
        Orders = new OrderSourceCapabilities(AmazonReceiptStore.Store, AmazonPages.IsPointsLabel, options.OrderUrl),
        ReceiptShop = Models.ReceiptShops.Amazon,
    };

    /// <summary>A usable secret is stored as entered (trimmed); <see cref="TotpSecret.Parse"/> reads it at sign-in.</summary>
    private static string NormalizeTotp(string value)
    {
        var trimmed = value.Trim();
        TotpSecret.Parse(trimmed);
        return trimmed;
    }

    public string ConnectorType => TypeName;

    private ILogger Log => _context.Logger;

    private DateOnly Today => DateOnly.FromDateTime(_context.Clock.GetUtcNow().UtcDateTime);

    internal WebLoginSpec WebLogin(ConnectorCredentials? credentials) => new(
        new Uri(_options.BaseUri, _options.WalletPage), [_options.BaseUri.Host], successUrlPrefix: new Uri(_options.BaseUri, _options.WalletPage))
    {
        Autofill = AmazonSignIn.Autofill(credentials),
        SuccessCheck = SignedInCheck,
        CodePrompt = AmazonSignIn.CodePrompt(credentials),
        WaitingScript = AmazonSignIn.WaitingScript,
        CredentialsError = AmazonSignIn.CredentialsError,
        KeepBrowser = true,
        ResumeUrl = new Uri(_options.BaseUri, _options.WalletPage),
        ObservedRequests = ["/ap/signin", "/ap/mfa", "/ap/cvf", "validateCaptcha"],
    };

    private ConnectorChallenge AskSignIn() => ConnectorChallenge.WebLogin("Sign in on the Amazon page.", WebLogin(_credentials), WebLoginReference);

    public Task<ConnectorStep<ConnectorSession?>> LoginAsync(
        ConnectorCredentials credentials, ConnectorSession? storedSession, ChallengeAnswer? answer, CancellationToken ct)
    {
        _credentials = credentials;
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
        DateOnly since, IReadOnlySet<string> knownOrderIds, IReadOnlyList<string> unreadOrderIds, ChallengeAnswer? answer, CancellationToken ct)
    {
        if (answer?.Browser is { IsOpen: true } fresh) _browser = fresh;
        if (_browser is not { IsOpen: true } browser) return NeedSignIn(answer != null);

        // ---- wallet ----
        _context.Progress?.Invoke("Reading Amazon payments");
        var walletUrl = new Uri(_options.BaseUri, _options.WalletPage);
        var response = await browser.GetAsync(walletUrl, "text/html", ct);
        if (AmazonPages.IsSignInPage(response.Url, response.Text)) return NeedSignIn(answer != null);
        if (!response.IsSuccess) throw new ConnectorException($"Amazon's payment list did not load (HTTP {response.Status}).");
        var page = AmazonPages.ParseWalletHtml(response.Text);
        if (page == null)
        {
            // Right after a sign-in Amazon sometimes serves the wallet without its transaction list; a second load has it.
            Log.LogWarning("Amazon: the payment list could not be read ({Shape}); loading it again.", AmazonPages.DescribePage(response.Url, response.Text));
            await Task.Delay(_options.WalletPageDelay, _context.Clock, ct);
            response = await browser.GetAsync(walletUrl, "text/html", ct);
            if (AmazonPages.IsSignInPage(response.Url, response.Text)) return NeedSignIn(answer != null);
            page = response.IsSuccess ? AmazonPages.ParseWalletHtml(response.Text) : null;
            if (page == null)
            {
                Log.LogWarning("Amazon: the payment list could not be read again ({Shape}).", AmazonPages.DescribePage(response.Url, response.Text));
                throw new ConnectorException("Amazon's payment list could not be read; the page may have changed.");
            }
        }
        var context = AmazonPages.WalletRequestContext(response.Text);
        var charges = new List<RemoteCharge>(page.Charges);
        var coveredFrom = page.HasMore ? NextDay(page.Oldest) ?? Today : since;
        var pages = 1;
        // A read of the whole list that stopped early left the key of its next page: once the recent pages reach the
        // days it covered, the read continues there instead of paging through those days again.
        var resume = LoadResume(since);
        var resumed = false;
        (DateOnly From, DateOnly To)? skipped = null;
        WalletResume? stopped = null;
        try
        {
            while (page.HasMore && page.Oldest is { } oldest && oldest >= since && pages < _options.MaxWalletPages)
            {
                AmazonWalletPage? next = null;
                if (resume != null && !resumed && oldest <= resume.ReadTo.AddDays(-3))
                {
                    resumed = true;
                    await Task.Delay(_options.WalletPageDelay, _context.Clock, ct);
                    next = await WalletPageAsync(browser, resume.Key, context, pages, retries: 1, ct);
                    if (next?.Newest is { } newest && newest <= oldest)
                    {
                        Log.LogInformation("Amazon: continuing the earlier read of the payment list before {From}.",
                            resume.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                        skipped = (newest, oldest);
                    }
                    else
                    {
                        Log.LogInformation("Amazon: the earlier read of the payment list cannot be continued; reading on.");
                        next = null;
                    }
                }
                if (next == null)
                {
                    await Task.Delay(_options.WalletPageDelay, _context.Clock, ct);
                    next = await WalletPageAsync(browser, page.NextPageKey!, context, pages, _options.WalletPageRetries, ct);
                }
                if (next == null)
                {
                    Log.LogWarning("Amazon: wallet page {Page} could not be read; payments before {Oldest} are left out this time.",
                        pages + 1, oldest.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    stopped = new WalletResume(page.NextPageKey!, oldest, Today);
                    break;
                }
                page = next;
                pages++;
                charges.AddRange(page.Charges);
                coveredFrom = page.HasMore ? NextDay(page.Oldest) ?? coveredFrom : since;
                _context.Progress?.Invoke($"Reading Amazon payments ({pages} pages)");
            }
            if (stopped == null && page.HasMore && page.Oldest is { } last && last >= since)
                stopped = new WalletResume(page.NextPageKey!, last, Today);
        }
        catch (OperationCanceledException) when (page.HasMore && page.Oldest is { } last)
        {
            stopped = new WalletResume(page.NextPageKey!, last, Today);
            throw;
        }
        finally
        {
            // A stop in the recent pages keeps the earlier position: those pages connect to it, the new one would not.
            if (stopped != null && (resume == null || resumed)) SaveResume(stopped);
            else if (stopped == null) _session.Values.Remove(WalletResumeKey);
        }
        if (coveredFrom < since) coveredFrom = since;
        if (skipped is { } gap && gap.To < coveredFrom) skipped = null;
        charges = charges.Where(c => c.Date >= since).ToList();
        Log.LogInformation("Amazon: {Pages} wallet pages, {Count} payments since {Since}.", pages, charges.Count, coveredFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        // ---- instruments ----
        var instruments = RememberedInstruments().ToDictionary(a => a.RemoteId, a => a.Name, StringComparer.Ordinal);
        foreach (var c in charges) instruments[c.InstrumentId] = c.Instrument;
        _session.Values[InstrumentsKey] = JsonSerializer.Serialize(instruments);

        // ---- order details ----
        var wanted = charges.Where(c => !c.Skip).SelectMany(c => c.OrderIds).Concat(unreadOrderIds).Distinct(StringComparer.Ordinal)
            .Where(id => !knownOrderIds.Contains(id)).ToList();
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
            charges, orders, instruments.Select(kv => Instrument(kv.Key, kv.Value)).OrderBy(a => a.Name, StringComparer.Ordinal).ToList(), coveredFrom)
        { Skipped = skipped });
    }

    /// <summary>
    /// A further wallet page, asked the way the page asks itself: a JSON POST with the page key, the page's token and
    /// context. Null when the answer cannot be read.
    /// </summary>
    /// <summary>The wallet page after <paramref name="pageKey"/>; asked for again after a growing pause while Amazon refuses it.</summary>
    private async Task<AmazonWalletPage?> WalletPageAsync(
        IBrowserFetcher browser, string pageKey, AmazonWalletRequest? context, int pages, int retries, CancellationToken ct)
    {
        // Amazon now and then answers a page with something else, and throttles a long read (HTTP 429); a later try usually works.
        var next = await NextWalletPageAsync(browser, pageKey, context, ct);
        for (var attempt = 1; next == null && attempt <= retries; attempt++)
        {
            var pause = _options.WalletRetryDelay * (1 << (attempt - 1));
            _context.Progress?.Invoke($"Reading Amazon payments ({pages} pages, Amazon asks to wait; trying again in {Math.Round(pause.TotalSeconds)} s)");
            await Task.Delay(pause, _context.Clock, ct);
            next = await NextWalletPageAsync(browser, pageKey, context, ct);
        }
        return next;
    }

    private sealed record WalletResume(string Key, DateOnly From, DateOnly ReadTo);

    private static DateOnly? NextDay(DateOnly? day) => day?.AddDays(1);

    /// <summary>The stopped read of the whole list, while it still reaches days the sync wants.</summary>
    private WalletResume? LoadResume(DateOnly since)
    {
        if (!_session.Values.TryGetValue(WalletResumeKey, out var json)) return null;
        try
        {
            return JsonSerializer.Deserialize<WalletResume>(json) is { Key.Length: > 0 } r && r.From >= since ? r : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void SaveResume(WalletResume resume) => _session.Values[WalletResumeKey] = JsonSerializer.Serialize(resume);

    private async Task<AmazonWalletPage?> NextWalletPageAsync(IBrowserFetcher browser, string pageKey, AmazonWalletRequest? context, CancellationToken ct)
    {
        if (context == null)
        {
            Log.LogWarning("Amazon: the payment list carries no request token.");
            return null;
        }
        var request = new BrowserFetchRequest("POST", new Uri(_options.BaseUri, _options.WalletDataPath))
        {
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "application/json",
                ["Accept"] = "application/json",
                ["x-amzn-upx-token"] = context.Token,
            },
            Body = AmazonPages.WalletPageBody(context, pageKey).ToJsonString(),
        };
        var response = await browser.SendAsync(request, ct);
        if (!response.IsSuccess)
        {
            Log.LogWarning("Amazon: a further payment page answered HTTP {Status}.", response.Status);
            return null;
        }
        try
        {
            if (JsonNode.Parse(response.Text) is { } json && AmazonPages.ParseWalletJson(json) is { } page) return page;
            var keys = JsonNode.Parse(response.Text) is JsonObject o ? string.Join(",", o.Select(p => p.Key)) : "(not an object)";
            Log.LogWarning("Amazon: a further payment page held no payment list (top-level keys: {Keys}).", keys);
        }
        catch (JsonException ex)
        {
            Log.LogWarning(ex, "Amazon: a further payment page was not JSON ({Length} characters).", response.Text.Length);
        }
        return null;
    }
}
