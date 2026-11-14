using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBudget.Models;

namespace OpenBudget.Connectors.LidlPlus;

/// <summary>Where Lidl Plus lives and how the app identifies itself; tests point the addresses at a fake.</summary>
public sealed class LidlPlusOptions
{
    public Uri AuthBase { get; init; } = new("https://accounts.lidl.com/");

    public Uri TicketsBase { get; init; } = new("https://tickets.lidlplus.com/api/");

    public string Country { get; init; } = "DE";

    public string Language { get; init; } = "de";

    /// <summary>The Lidl Plus app's OAuth client (public; its secret is the fixed word "secret").</summary>
    public string ClientId { get; init; } = "LidlPlusNativeClient";

    /// <summary>The app's own callback; Lidl accepts no other redirect for this client.</summary>
#pragma warning disable CA1056 // an app scheme, compared as text with the addresses the browser reports
    public string RedirectUri { get; init; } = "com.lidlplus.app://callback";
#pragma warning restore CA1056

    public string AppVersion { get; init; } = "17.9.3";

    /// <summary>The wait between two receipt requests.</summary>
    public TimeSpan RequestDelay { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>At most this many list pages per sync.</summary>
    public int MaxPages { get; init; } = 60;

    /// <summary>At most this many receipt details per sync; the rest follow next time.</summary>
    public int MaxDetails { get; init; } = 250;
}

/// <summary>
/// Lidl Plus as a receipt source: the digital receipts of the signed-in Lidl Plus account, with every line, matched to the
/// bookings they were paid with on the owner's accounts and split by their lines like the owner's other supermarket
/// receipts (see <see cref="Receipts.ReceiptStore"/>). It has no accounts or bookings of its own.
///
/// Sign-in: Lidl's OAuth sign-in (authorization code with PKCE) on accounts.lidl.com, shown in the app's browser. The
/// owner types their e-mail address or phone number, password and the code Lidl sends; nothing is filled in for them.
/// Lidl then sends the page to the Lidl Plus app's callback address, which the browser cannot open: the web login
/// captures it (<see cref="WebLoginSpec.CaptureRedirect"/>), and the code in it is exchanged for tokens with the PKCE
/// verifier, after checking the state. Later syncs only use the refresh token, which Lidl replaces with every use:
/// the new one is written to the login's encrypted session as soon as Lidl hands it out. When Lidl no longer accepts it, a
/// background sync stops for the owner and a manual sync shows the sign-in again. Tokens and receipt contents are never logged.
/// </summary>
public sealed class LidlPlusConnector : IBankConnector, IReceiptSource, IBackgroundRunInfo
{
    public const string TypeName = "lidlplus";
    internal const string WebLoginReference = "lidlplus-web-login";
    internal const string RefreshTokenKey = "refresh_token";
    internal const string SignInAgain = "Lidl Plus wants you to sign in again.";

    private readonly ConnectorContext _context;
    private readonly LidlPlusOptions _options;
    private ConnectorSession _session = new();
    private string? _accessToken;
    private string? _verifier;
    private string? _state;

    public LidlPlusConnector(ConnectorContext context, LidlPlusOptions options)
    {
        _context = context;
        _options = options;
    }

    public static ConnectorRegistration CreateRegistration(LidlPlusOptions options) => new(
        TypeName,
        "Lidl Plus (receipts)",
        "Your digital Lidl Plus receipts: each receipt's items are added to the booking it was paid with (card or Lidl Pay) on your accounts. It does not create accounts or bookings. Sign in once on Lidl's page with your Lidl Plus e-mail address or phone number, password and the code Lidl sends you; after that the receipts are read without you.",
        ctx => new LidlPlusConnector(ctx, options),
        CredentialRequirement.None,
        Statements: false)
    {
        // after the first sign-in the refresh token needs nobody
        Silent = SilentCapability.Full,
        ReceiptShop = ReceiptShops.Lidl,
    };

    public string ConnectorType => TypeName;

    public string Shop => ReceiptShops.Lidl;

    private ILogger Log => _context.Logger;

    // ---- sign-in ----

    internal WebLoginSpec WebLogin()
    {
        _verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        _state = Base64Url(RandomNumberGenerator.GetBytes(24));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(_verifier)));
        var query = string.Join("&", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client_id"] = _options.ClientId,
            ["response_type"] = "code",
            ["scope"] = "openid profile offline_access lpprofile lpapis",
            ["redirect_uri"] = _options.RedirectUri,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = _state,
            ["Country"] = _options.Country.ToUpperInvariant(),
            ["language"] = $"{_options.Language}-{_options.Country.ToUpperInvariant()}",
        }.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)));
        var start = new Uri(_options.AuthBase, "connect/authorize?" + query);
        // the sign-in only ends at the app's callback; no page of Lidl's counts as done
        return new WebLoginSpec(start, [_options.AuthBase.Host], successWhen: (_, _) => false)
        {
            CaptureRedirect = _options.RedirectUri,
            ObservedRequests = ["/connect/token", "/Account/"],
        };
    }

    private ConnectorChallenge AskSignIn() => ConnectorChallenge.WebLogin("Sign in on the Lidl Plus page.", WebLogin(), WebLoginReference);

    public async Task<ConnectorStep<ConnectorSession?>> LoginAsync(
        ConnectorCredentials credentials, ConnectorSession? storedSession, ChallengeAnswer? answer, CancellationToken ct)
    {
        if (answer == null)
        {
            _session = storedSession ?? new ConnectorSession();
            if (_session.Values.TryGetValue(RefreshTokenKey, out var refresh) && refresh.Length > 0)
            {
                if (await RefreshAsync(refresh, ct)) return ConnectorStep.Done<ConnectorSession?>(_session);
                _session.Values.Remove(RefreshTokenKey);
            }
            if (_context.Silent) return ConnectorStep.NeedsUser<ConnectorSession?>(SignInAgain);
            return ConnectorStep.Ask<ConnectorSession?>(AskSignIn());
        }
        if (answer.Challenge.Reference != WebLoginReference || answer.CallbackUrl is not { } callback)
            return ConnectorStep.Fail<ConnectorSession?>("The Lidl Plus sign-in did not complete; please sync again.");
        var code = CodeFromCallback(callback, _state, out var problem);
        if (code == null)
        {
            Log.LogWarning("Lidl Plus: the sign-in callback was not accepted ({Problem}).", problem);
            return ConnectorStep.Fail<ConnectorSession?>("The Lidl Plus sign-in could not be verified; please sync again.");
        }
        var tokens = await TokenAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _options.RedirectUri,
            ["code_verifier"] = _verifier ?? "",
        }, ct);
        if (tokens == null) return ConnectorStep.Fail<ConnectorSession?>("Lidl did not accept the sign-in; please sync again.");
        Log.LogInformation("Signed in to Lidl Plus.");
        return ConnectorStep.Done<ConnectorSession?>(_session);
    }

    /// <summary>
    /// The authorization code of a callback address, when its state is the one this sign-in sent; otherwise null and why
    /// (without any value from the address).
    /// </summary>
    internal static string? CodeFromCallback(Uri callback, string? expectedState, out string? problem)
    {
        var text = callback.OriginalString;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in new[] { text.IndexOf('?', StringComparison.Ordinal), text.IndexOf('#', StringComparison.Ordinal) }.Where(i => i >= 0))
        {
            var section = text[(part + 1)..];
            var end = section.IndexOfAny(['#', '?']);
            if (end >= 0) section = section[..end];
            foreach (var pair in section.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=', StringComparison.Ordinal);
                if (eq <= 0) continue;
                values.TryAdd(Uri.UnescapeDataString(pair[..eq]), Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')));
            }
        }
        if (values.ContainsKey("error"))
        {
            problem = "Lidl returned an error";
            return null;
        }
        if (string.IsNullOrEmpty(expectedState) || !values.TryGetValue("state", out var state)
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(expectedState)))
        {
            problem = "state mismatch";
            return null;
        }
        if (!values.TryGetValue("code", out var code) || code.Length == 0)
        {
            problem = "no code";
            return null;
        }
        problem = null;
        return code;
    }

    /// <summary>Gets a new access token with the stored refresh token; false when Lidl rejects it (signed out).</summary>
    private async Task<bool> RefreshAsync(string refreshToken, CancellationToken ct) =>
        await TokenAsync(new Dictionary<string, string>(StringComparer.Ordinal) { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken }, ct) != null;

    /// <summary>
    /// Calls the token endpoint. On success the access token is kept for this sync and the (rotated) refresh token in the
    /// session. Null when Lidl rejects the grant (HTTP 400/401); any other failure throws, so the stored token stays.
    /// </summary>
    private async Task<JsonNode?> TokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.AuthBase, "connect/token"))
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(_options.ClientId + ":secret")));
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await _context.Http.SendWithoutSessionAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            Log.LogInformation("Lidl Plus: the token endpoint refused the {Grant} grant (HTTP {Status}).", form["grant_type"], (int)response.StatusCode);
            return null;
        }
        if (!response.IsSuccessStatusCode) throw new ConnectorException($"Lidl Plus could not be reached (HTTP {(int)response.StatusCode}).");
        JsonNode? json;
        try { json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)); }
        catch (JsonException) { json = null; }
        var access = json?["access_token"]?.GetValue<string>();
        if (string.IsNullOrEmpty(access)) throw new ConnectorException("Lidl Plus answered the sign-in in an unexpected way.");
        _accessToken = access;
        if (json?["refresh_token"]?.GetValue<string>() is { Length: > 0 } rotated)
        {
            _session.Values[RefreshTokenKey] = rotated;
            // the old token no longer works: the new one is stored before anything else can fail
            if (_context.SaveSession is { } save) await save(_session, ct);
        }
        return json;
    }

    public string? FinTsCodes => null;

    public bool ScaCompleted => false;

    /// <summary>A run that stopped for the owner may already have used (and so replaced) the refresh token: the new one is kept.</summary>
    public ConnectorSession? SessionAfterStop => _session.Values.ContainsKey(RefreshTokenKey) ? _session : null;

    public Task<ConnectorSession?> LogoutAsync(CancellationToken ct) => Task.FromResult<ConnectorSession?>(_session);

    public Task<IReadOnlyList<RemoteAccount>> ListAccountsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RemoteAccount>>([]);

    public Task<ConnectorStep<AccountRefresh>> RefreshAsync(RemoteAccount account, DateOnly? since, ChallengeAnswer? answer, CancellationToken ct) =>
        Task.FromResult(ConnectorStep.Done(new AccountRefresh(null, [], [])));

    public Task<ConnectorStep<IReadOnlyList<RemoteStatement>>> FetchStatementsAsync(
        IReadOnlyList<RemoteAccount> accounts, IReadOnlySet<string> knownIdentifiers, ChallengeAnswer? answer, CancellationToken ct) =>
        Task.FromResult(ConnectorStep.Done<IReadOnlyList<RemoteStatement>>([]));

    // ---- receipts ----

    public async Task<ConnectorStep<IReadOnlyList<SourceReceipt>>> FetchReceiptsAsync(
        DateOnly since, IReadOnlySet<string> knownReceiptNumbers, ChallengeAnswer? answer, CancellationToken ct)
    {
        if (_accessToken == null)
            return _context.Silent ? ConnectorStep.NeedsUser<IReadOnlyList<SourceReceipt>>(SignInAgain) : ConnectorStep.Fail<IReadOnlyList<SourceReceipt>>(SignInAgain);
        var country = Uri.EscapeDataString(_options.Country.ToUpperInvariant());
        var wanted = new List<LidlTicketSummary>();
        var seen = 0;
        for (var page = 1; page <= _options.MaxPages; page++)
        {
            _context.Progress?.Invoke(page == 1 ? "Reading Lidl Plus receipts" : $"Reading Lidl Plus receipts ({page} pages)");
            if (page > 1) await Task.Delay(_options.RequestDelay, _context.Clock, ct);
            var json = await GetJsonAsync(new Uri(_options.TicketsBase, $"v2/{country}/tickets?pageNumber={page.ToString(CultureInfo.InvariantCulture)}&onlyFavorite=false"), ct)
                ?? throw new ConnectorException("The Lidl Plus receipt list could not be read.");
            var list = LidlTickets.ParseList(json);
            seen += list.Tickets.Count;
            wanted.AddRange(list.Tickets.Where(t => DateOnly.FromDateTime(t.Date) >= since && !knownReceiptNumbers.Contains(LidlReceipt.InvoiceNumber(t.Id))));
            if (list.Tickets.Count == 0 || (list.TotalCount is { } total && seen >= total)) break;
            // newest first: once a page reaches before the wanted days, the later pages are older still
            if (DateOnly.FromDateTime(list.Tickets.Min(t => t.Date)) < since) break;
        }
        var receipts = new List<SourceReceipt>();
        foreach (var ticket in wanted.Take(_options.MaxDetails))
        {
            _context.Progress?.Invoke($"Reading Lidl Plus receipts ({receipts.Count + 1} of {Math.Min(wanted.Count, _options.MaxDetails)})");
            await Task.Delay(_options.RequestDelay, _context.Clock, ct);
            var id = Uri.EscapeDataString(ticket.Id);
            // the v2 details fail for some accounts; v3 has the same receipt
            var json = await GetJsonAsync(new Uri(_options.TicketsBase, $"v2/{country}/tickets/{id}"), ct)
                ?? await GetJsonAsync(new Uri(_options.TicketsBase, $"v3/{country}/tickets/{id}"), ct);
            if (json == null || LidlTickets.ParseDetail(json, ticket) is not { } receipt)
            {
                Log.LogWarning("Lidl Plus: a receipt could not be read; it is tried again next time.");
                continue;
            }
            receipts.Add(new SourceReceipt(receipt.Store, receipt.ToParsed()));
        }
        Log.LogInformation("Lidl Plus: {Listed} receipts listed, {Read} new ones read.", seen, receipts.Count);
        return ConnectorStep.Done<IReadOnlyList<SourceReceipt>>(receipts);
    }

    /// <summary>A GET of the ticket API as the app sends it; null when it does not answer with JSON. An expired access token is renewed once.</summary>
    private async Task<JsonNode?> GetJsonAsync(Uri url, CancellationToken ct)
    {
        var (status, json) = await SendGetAsync(url, ct);
        if (status == HttpStatusCode.Unauthorized && _session.Values.TryGetValue(RefreshTokenKey, out var refresh) && await RefreshAsync(refresh, ct))
            (status, json) = await SendGetAsync(url, ct);
        if (status == HttpStatusCode.Unauthorized)
            throw _context.Silent ? new SilentAbortException(SignInAgain) : new ConnectorException(SignInAgain);
        if (status is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
        {
            Log.LogInformation("Lidl Plus: {Path} answered HTTP {Status}.", string.Join('/', url.AbsolutePath.Split('/').Take(5)), (int)status);
            return null;
        }
        return json;
    }

    private async Task<(HttpStatusCode Status, JsonNode? Json)> SendGetAsync(Uri url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        request.Headers.TryAddWithoutValidation("App-Version", _options.AppVersion);
        request.Headers.TryAddWithoutValidation("Operating-System", "iOs");
        request.Headers.TryAddWithoutValidation("App", "com.lidl.eci.lidl.plus");
        request.Headers.TryAddWithoutValidation("Accept-Language", _options.Language);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await _context.Http.SendWithoutSessionAsync(request, ct);
        if (!response.IsSuccessStatusCode) return (response.StatusCode, null);
        try { return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))); }
        catch (JsonException) { return (HttpStatusCode.UnprocessableEntity, null); }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
