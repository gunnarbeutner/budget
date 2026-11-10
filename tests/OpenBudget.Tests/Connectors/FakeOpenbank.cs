#pragma warning disable CA2000 // test doubles: lifetimes are owned by the HTTP client
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OpenBudget.Connectors;

namespace OpenBudget.Tests.Connectors;

/// <summary>
/// A made-up Openbank API (api.kunden.openbankpay.com): the data shapes seen in the web client's responses, with
/// invented data (alice, Example Card, DE00... virtual IBAN). Sign-in happens in a browser (see <see cref="SignIn"/>):
/// the customer site's web client then holds the tokens in cookies; the API trades refresh tokens for new tokens.
/// </summary>
internal sealed class FakeOpenbank : HttpMessageHandler
{
    public const string Username = "alice";
    public const string Password = "example-password";
    public const string CardId = "000000000EXAMPLE0001_A";
    public const string AddonCardId = "000000000EXAMPLE0002_A";
    public const string Iban = "DE00000000000000000042";
    public const string TokenMarker = "tok-secretvalue";

    private readonly Lock _lock = new();
    private readonly HashSet<string> _accessTokens = [];
    private readonly HashSet<string> _refreshTokens = [];
    private int _counter;

    public const string SiteHost = "kunden.openbankpay.com";

    /// <summary>When set, the global position answers with this status.</summary>
    public HttpStatusCode? GlobalPositionFailure { get; set; }

    /// <summary>Whether offset requests are understood (else they answer 400).</summary>
    public bool SupportsOffset { get; set; } = true;

    public int PageLimit { get; set; } = 3;

    public List<string> Requests { get; } = [];

    public List<JsonObject> Transactions { get; set; } = DefaultTransactions();

    public int Count(string needle)
    {
        lock (_lock) return Requests.Count(r => r.Contains(needle, StringComparison.Ordinal));
    }

    /// <summary>Ends the access tokens (refresh tokens still work), as happens after a few minutes.</summary>
    public void ExpireAccessTokens()
    {
        lock (_lock) _accessTokens.Clear();
    }

    /// <summary>Ends every session: access and refresh tokens.</summary>
    public void ExpireSessions()
    {
        lock (_lock)
        {
            _accessTokens.Clear();
            _refreshTokens.Clear();
        }
    }

    public BankHttpClient CreateClient() => new(jar => new CookieRedirectHandler(jar, this));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content == null ? "" : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
        lock (_lock) return Task.FromResult(Handle(request, body));
    }

    private HttpResponseMessage Handle(HttpRequestMessage request, string body)
    {
        var uri = request.RequestUri!;
        Requests.Add($"{request.Method} {uri.AbsolutePath}{uri.Query}");
        if (uri.Host != "api.kunden.openbankpay.com") return new HttpResponseMessage(HttpStatusCode.NotFound);
        var input = body.Length > 0 ? JsonNode.Parse(body) as JsonObject : null;
        var path = uri.AbsolutePath;

        if (path == "/cb-api-auth/authentication/refresh-token") return Refresh(input);

        var auth = request.Headers.Authorization;
        if (auth?.Scheme != "Bearer" || auth.Parameter == null || !_accessTokens.Contains(auth.Parameter))
            return Json(HttpStatusCode.Unauthorized, new JsonObject { ["error"] = "invalid_token" });

        if (path == "/cb-api-global-position/v3/global-position")
            return GlobalPositionFailure is { } failure ? new HttpResponseMessage(failure) : Json(HttpStatusCode.OK, GlobalPosition());
        if (path == $"/cb-api-cards/v3/cards/{CardId}") return Json(HttpStatusCode.OK, new JsonObject { ["resource"] = CardDetail(CardId, "0001", Iban, 312.40) });
        if (path == $"/cb-api-cards/v3/cards/{AddonCardId}") return Json(HttpStatusCode.OK, new JsonObject { ["resource"] = CardDetail(AddonCardId, "0002", null, 0) });
        if (path == $"/cb-api-cards/v3/cards/{CardId}/transactions") return TransactionsPage(uri);
        if (path == $"/cb-api-cards/v3/cards/{AddonCardId}/transactions")
            return Json(HttpStatusCode.OK, new JsonObject { ["metadata"] = new JsonObject { ["hasPendingRecords"] = false }, ["elements"] = new JsonArray() });
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    // ---- auth ----

    /// <summary>
    /// What a completed sign-in in the browser leaves behind on the customer site: the web client's token cookies
    /// (URL-encoded, as it writes them). <paramref name="withTokens"/> false: a site that never stored a token.
    /// </summary>
    public IReadOnlyList<CookieState> SignIn(bool withTokens = true)
    {
        lock (_lock)
        {
            var tokens = NewTokens();
            var cookies = new List<CookieState>
            {
                Site("username", Username),
                Site("isCustomer", "true"),
            };
            if (withTokens)
            {
                cookies.Add(Site("accessToken", Uri.EscapeDataString((string)tokens["accessToken"]!)));
                cookies.Add(Site("refreshToken", Uri.EscapeDataString((string)tokens["refreshToken"]!)));
                cookies.Add(Site("sessionExpiresAt", Uri.EscapeDataString((string)tokens["sessionExpiresAt"]!)));
            }
            return cookies;
        }
    }

    private static CookieState Site(string name, string value) =>
        new(name, value, SiteHost, "/", DateTime.UtcNow.AddDays(1), true, false, "Lax");

    private HttpResponseMessage Refresh(JsonObject? input)
    {
        var token = (string?)input?["refreshToken"];
        if (token == null || !_refreshTokens.Remove(token)) return Json(HttpStatusCode.Unauthorized, new JsonObject { ["error"] = "invalid_grant" });
        return Json(HttpStatusCode.OK, NewTokens());
    }

    private JsonObject NewTokens()
    {
        var n = ++_counter;
        // Real tokens are base64-like and end in '=' (which the cookies carry URL-encoded).
        var access = $"{TokenMarker}-a{n}$x=";
        var refresh = $"{TokenMarker}-r{n}=";
        _accessTokens.Add(access);
        _refreshTokens.Add(refresh);
        return new JsonObject
        {
            ["accessToken"] = access,
            ["refreshToken"] = refresh,
            ["sessionExpiresAt"] = "2030-01-01T00:00:00Z",
            ["expiresIn"] = 300,
        };
    }

    // ---- data ----

    private static JsonObject GlobalPosition() => new()
    {
        ["resource"] = new JsonObject
        {
            ["customerInfo"] = new JsonObject { ["userDetails"] = new JsonObject { ["userName"] = "Alice Example" } },
            ["cardsInfo"] = new JsonObject
            {
                ["cards"] = new JsonArray
                {
                    CardSummary(CardId, ""),
                    CardSummary(AddonCardId, "Alice's second card"),
                },
            },
        },
    };

    private static JsonObject CardSummary(string id, string alias) => new()
    {
        ["holderName"] = "ALICE EXAMPLE",
        ["id"] = id,
        ["productTypeCode"] = "DE.OB.CREDIT.CARD",
        ["productTypeName"] = "Example Card",
        ["currencyCode"] = "EUR",
        ["alias"] = alias,
        ["status"] = "ACTIVE",
    };

    private static JsonObject CardDetail(string id, string last4, string? iban, double currentBalance)
    {
        var o = CardSummary(id, "");
        o["last4DigitsPan"] = last4;
        o["creditDetails"] = new JsonObject
        {
            ["availableAmount"] = 1000 - currentBalance,
            ["limit"] = 1000.0,
            ["currentBalance"] = currentBalance,
        };
        if (iban != null) o["virtualIban"] = "DE00 0000 0000 0000 0000 42";
        return o;
    }

    private HttpResponseMessage TransactionsPage(Uri uri)
    {
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var limit = Math.Min(int.Parse(query["limit"] ?? "99", CultureInfo.InvariantCulture), PageLimit);
        var offset = 0;
        if (query["offset"] is { } o)
        {
            if (!SupportsOffset) return Json(HttpStatusCode.BadRequest, new JsonObject { ["error"] = "unknown parameter" });
            offset = int.Parse(o, CultureInfo.InvariantCulture);
        }
        var page = new JsonArray();
        foreach (var t in Transactions.Skip(offset).Take(limit)) page.Add(t.DeepClone());
        return Json(HttpStatusCode.OK, new JsonObject
        {
            ["metadata"] = new JsonObject { ["hasPendingRecords"] = Transactions.Any(t => (string?)t["transactionStatusCode"] == "AUTHORIZED") },
            ["elements"] = page,
        });
    }

    public static JsonObject Tx(string id, string date, double amount, string status, string description,
        string? merchant = null, string? settlementDate = null, string denialCode = "0", JsonObject? detailedAmount = null)
    {
        var o = new JsonObject
        {
            ["id"] = id,
            ["date"] = date,
            ["time"] = "12:00:00",
            ["dateTime"] = date + "T11:00:00Z",
            ["categoryCode"] = "OB.C.OTHERS",
            ["description"] = description,
            ["transactionTypeCode"] = amount < 0 ? "D" : "C",
            ["transactionStatusCode"] = status,
            ["merchantDetails"] = merchant == null
                ? new JsonObject { ["address"] = new JsonObject() }
                : new JsonObject { ["id"] = "m-" + id, ["name"] = merchant, ["address"] = new JsonObject { ["countryCode"] = "DE" } },
            ["categoryName"] = "Otros",
            ["denialCode"] = denialCode,
            ["currencyCode"] = "EUR",
            ["amounts"] = new JsonArray { new JsonObject { ["totalAmount"] = amount, ["currency"] = "EUR" } },
        };
        if (settlementDate != null) o["settlementDate"] = settlementDate;
        if (detailedAmount != null) o["detailedAmount"] = detailedAmount;
        return o;
    }

    /// <summary>Newest first, as Openbank lists them.</summary>
    private static List<JsonObject> DefaultTransactions() =>
    [
        Tx("T-07", "2026-11-09", -12.99, "AUTHORIZED", "EXAMPLE STREAMING", "Example Streaming"),
        Tx("T-06", "2026-11-08", -40.00, "AUTHORIZED", "DECLINED SHOP", "Declined Shop", denialCode: "51"),
        Tx("T-05", "2026-11-06", -23.50, "LIQUIDATED", "EXAMPLE BOOKS", "Example Books", "2026-11-07"),
        Tx("T-04", "2026-11-04", -9.99, "LIQUIDATED", "EXAMPLE CLOUD", "Example Cloud", "2026-11-05",
            detailedAmount: new JsonObject { ["exchangeRate"] = 1.1, ["originalCurrency"] = "USD", ["originalAmountExchange"] = 10.99, ["totalAmount"] = -9.99 }),
        Tx("T-03", "2026-10-30", 250.00, "LIQUIDATED", "Kartenabrechnung", settlementDate: "2026-10-30"),
        Tx("T-02", "2026-10-20", 3.17, "LIQUIDATED", "Eingelöste Example Punkte"),
        Tx("T-01", "2026-10-02", -55.00, "LIQUIDATED", "EXAMPLE GROCER", "Example Grocer", "2026-10-03"),
    ];

    private static HttpResponseMessage Json(HttpStatusCode status, JsonObject body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}
