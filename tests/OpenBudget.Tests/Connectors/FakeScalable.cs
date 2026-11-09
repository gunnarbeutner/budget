#pragma warning disable CA2000 // test doubles: lifetimes are owned by the HTTP client
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OpenBudget.Connectors;

namespace OpenBudget.Tests.Connectors;

/// <summary>Follows redirects and keeps cookies in the client's jar, like the real SocketsHttpHandler does.</summary>
internal sealed class CookieRedirectHandler : HttpMessageHandler
{
    private readonly CookieContainer _jar;
    private readonly HttpMessageInvoker _inner;

    public CookieRedirectHandler(CookieContainer jar, HttpMessageHandler inner)
    {
        _jar = jar;
        _inner = new HttpMessageInvoker(inner, disposeHandler: false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var current = request;
        for (var hop = 0; hop < 20; hop++)
        {
            var cookies = _jar.GetCookieHeader(current.RequestUri!);
            if (cookies.Length > 0) current.Headers.TryAddWithoutValidation("Cookie", cookies);
            var response = await _inner.SendAsync(current, cancellationToken);
            response.RequestMessage = current;
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var header in setCookies) _jar.SetCookies(current.RequestUri!, header);
            }
            if ((int)response.StatusCode is < 300 or >= 400 || response.Headers.Location == null) return response;

            var next = new Uri(current.RequestUri!, response.Headers.Location);
            var keepMethod = response.StatusCode is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
            response.Dispose();
            current = new HttpRequestMessage(keepMethod ? current.Method : HttpMethod.Get, next);
        }
        throw new HttpRequestException("too many redirects");
    }
}

/// <summary>Captures log output so tests can assert what is (and is not) logged.</summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_entries) return [.. _entries];
        }
    }

    public string All => string.Join('\n', Entries.Select(e => $"{e.Level}: {e.Message}"));

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public ILogger CreateLogger(string categoryName) => new Capture(this);

    public void Dispose()
    {
    }

    private sealed class Capture : ILogger
    {
        private readonly CapturingLoggerFactory _owner;

        public Capture(CapturingLoggerFactory owner) => _owner = owner;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_owner._entries) _owner._entries.Add((logLevel, formatter(state, exception) + (exception == null ? "" : " | " + exception.Message)));
        }
    }
}

/// <summary>A made-up Scalable: the observed login flow and data shapes, with invented data (alice, Example ETF, DE00... IBAN).</summary>
internal sealed partial class FakeScalable : HttpMessageHandler
{
    public const string PortfolioId = "PF-1";
    public const string OtherPortfolioId = "PF-9";
    public const string PersonId = "person-1";
    public const string UserId = "user-1";
    public const string Iban = "DE00000000000000000009";
    public const string SessionCookie = "sc_session";
    private const string AppHost = "de.scalable.capital";

    private readonly Lock _lock = new();
    private readonly HashSet<string> _signedIn = [];
    private int _sessionCounter;

    public List<string> Requests { get; } = [];

    public List<string> Cursors { get; } = [];

    public Dictionary<string, int> Downloads { get; } = new(StringComparer.Ordinal);

    public List<JsonObject> Transactions { get; set; } = DefaultTransactions();

    public int PageSize { get; set; } = 2;

    public List<JsonObject> Documents { get; set; } =
    [
        Doc("D1", "2026-11-01T00:00:00Z", "Securities statement October", PortfolioId),
        Doc("D2", "2026-10-15T00:00:00Z", "Tax certificate", null),
        Doc("D3", "2026-10-01T00:00:00Z", "Other portfolio letter", OtherPortfolioId),
    ];

    /// <summary>Forgets every session, as if Scalable had ended them.</summary>
    public void ExpireSessions()
    {
        lock (_lock)
        {
            _signedIn.Clear();
        }
    }

    /// <summary>What the user's sign-in in a real browser leaves behind: a new session and its cookie.</summary>
    public CookieState SignIn()
    {
        lock (_lock)
        {
            var id = $"sess-{++_sessionCounter}-secretvalue";
            _signedIn.Add(id);
            return new CookieState(SessionCookie, id, AppHost, "/", DateTime.UtcNow.AddDays(30), true, true, "Lax");
        }
    }

    public int Count(string needle) => Requests.Count(r => r.Contains(needle, StringComparison.Ordinal));

    public BankHttpClient CreateClient() => new(jar => new CookieRedirectHandler(jar, this));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Content must be read outside the lock-free async part: it is an in-memory string.
        var body = request.Content == null ? "" : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
        lock (_lock) return Task.FromResult(Handle(request, body));
    }

    // ---- HTTP routing ----

    private HttpResponseMessage Handle(HttpRequestMessage request, string body)
    {
        var uri = request.RequestUri!;
        Requests.Add($"{request.Method} {uri.Host}{uri.AbsolutePath}");
        var cookie = request.Headers.TryGetValues("Cookie", out var c) ? string.Join(';', c) : "";
        var match = Regex.Match(cookie, SessionCookie + "=(?<v>[^;]+)", RegexOptions.None, TimeSpan.FromSeconds(2));
        var session = match.Success ? match.Groups["v"].Value : null;

        return uri.AbsolutePath switch
        {
            "/cockpit/" => Cockpit(session),
            "/cockpit/graphql" or "/broker/api/data" => Graphql(body, session),
            "/cockpit/api/download" => Download(uri, session),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    // ---- cockpit page (carries the user id in its Next.js data) ----

    private HttpResponseMessage Cockpit(string? session)
    {
        Requests.Add("cockpit page");
        if (session == null || !_signedIn.Contains(session))
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://secure.scalable.capital/u/login");
            return redirect;
        }
        var nextData = JsonSerializer.Serialize(new
        {
            props = new { pageProps = new { middlewareProps = new { m1 = new { session = new { user = new { userId = PersonId } } } } } },
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"<html><body><script id=\"__NEXT_DATA__\" type=\"application/json\">{nextData}</script></body></html>", Encoding.UTF8, "text/html"),
        };
    }

    // ---- GraphQL ----

    private HttpResponseMessage Graphql(string body, string? session)
    {
        var json = JsonNode.Parse(body)!.AsObject();
        var op = json["operationName"]!.GetValue<string>();
        var query = json["query"]!.GetValue<string>();
        var vars = json["variables"]!.AsObject();
        Requests.Add($"graphql {op}");

        if (session == null || !_signedIn.Contains(session))
            return Gql(new { errors = new[] { new { message = "Not authenticated", extensions = new { code = "UNAUTHENTICATED" } } }, data = (object?)null });

        return DataOperation(op, query, vars);
    }

    private HttpResponseMessage DataOperation(string op, string query, JsonObject vars)
    {
        if (op == "personOverview") return Gql(new { data = new { personOverview = new { id = PersonId } } });
        if (vars["personId"]?.GetValue<string>() != PersonId && vars["userId"]?.GetValue<string>() != PersonId)
            return Gql(new { errors = new[] { new { message = "Unknown person" } } });

        switch (op)
        {
            case "fetchCashAccountDetails":
                return Gql(new { data = new { account = new { id = PersonId, brokerPortfolios = new[] { new { id = PortfolioId, custodianBank = "X", cashAccount = new { iban = Iban, bic = "TESTDE00" } } } } } });
            case "getBrokerPortfolios":
                return Gql(new { data = new { account = new { id = PersonId, brokerPortfolios = new[] { new { id = PortfolioId, personalizations = new[] { new { name = "Alice Portfolio", id = "n1" } } } } } } });
            case "getCashBalance":
                return Gql(new { data = new { account = new { brokerPortfolio = new { payments = new { buyingPower = new { cashBalance = 1234.56 } } } } } });
            case "getPortfolioGroupsInventory":
                return Gql(Inventory());
            case "getInventory":
                return Gql(ExtraInventory(query));
            case "moreTransactions":
                return TransactionPage(vars, null);
            case "GetMailbox":
                return Gql(Mailbox());
            default:
                // The second booking feed has the same shape one level deeper; its wrapper key is read from the query text.
                if (op.StartsWith("more", StringComparison.Ordinal))
                    return TransactionPage(vars, WrapperPattern().Match(query).Groups["k"].Value);
                return Gql(new { errors = new[] { new { message = $"Unknown operation {op}" } } });
        }
    }

    [GeneratedRegex(@"(?<k>\w+) \{ id moreTransactions", RegexOptions.None, 2000)]
    private static partial Regex WrapperPattern();

    private static object Inventory() => new
    {
        data = new
        {
            account = new
            {
                brokerPortfolio = new
                {
                    inventory = new
                    {
                        portfolioGroups = new[]
                        {
                            new { id = "g1", items = new[] { new { id = "gi", items = new[] { Security("XX0000000001", "A0EXAM", "Example ETF", 10.5, 100.25, 90) } } } },
                        },
                        ungroupedInventoryItems = new[]
                        {
                            new { id = "u1", items = new[] { Security("XX0000000002", "A0EXBD", "Example Bond Fund", 4, 50, 52), Security("XX0000000009", "A0SOLD", "Sold Out Fund", 0, 10, 10) } },
                        },
                    },
                },
            },
        },
    };

    private static object Security(string isin, string wkn, string name, double filled, double mid, double fifo) => new
    {
        id = isin,
        isin,
        wkn,
        name,
        type = "FUND",
        inventory = new { id = "i-" + isin, position = new { filled, fifoPrice = fifo } },
        quoteTick = new { id = "q-" + isin, midPrice = mid, currency = "EUR" },
    };

    /// <summary>One ETP holding, nested under the wrapper keys named in the query (so no product-specific key is spelled here).</summary>
    private static JsonObject ExtraInventory(string query)
    {
        var m = ExtraPattern().Match(query);
        var entry = new JsonObject
        {
            ["id"] = "e1",
            ["etpSecurity"] = JsonSerializer.SerializeToNode(Security("XX0000000003", "A0ETP1", "Example Tracker ETP", 2, 25, 20)),
        };
        var wrapper = new JsonObject { ["id"] = "w1", [m.Groups["b"].Value] = new JsonArray(new JsonObject { ["id"] = "x1", ["inventory"] = entry }) };
        var portfolio = new JsonObject { [m.Groups["a"].Value] = wrapper };
        return new JsonObject { ["data"] = new JsonObject { ["account"] = new JsonObject { ["brokerPortfolio"] = portfolio } } };
    }

    [GeneratedRegex(@"brokerPortfolio\(id: \$portfolioId\) \{ id (?<a>\w+) \{ id (?<b>\w+) ", RegexOptions.None, 2000)]
    private static partial Regex ExtraPattern();

    // ---- bookings ----

    public static List<JsonObject> DefaultTransactions() =>
    [
        Tx("T1", "2026-11-05T10:00:00Z", "SETTLED", "Deposit from Alice", cash: "DEPOSIT", amount: 500m),
        Tx("T2", "2026-11-04T09:00:00Z", "SETTLED", "Example ETF", security: "SINGLE", amount: 50m, side: "BUY", isin: "XX0000000001", quantity: 2m),
        Tx("T3", "2026-11-03T09:00:00Z", "PENDING", "Savings plan Example ETF", security: "SAVINGS_PLAN", amount: 25m, side: "BUY", isin: "XX0000000001", quantity: 0.25m),
        Tx("T4", "2026-11-02T09:00:00Z", "SETTLED", "Cancelled buy", security: "SINGLE", amount: 10m, side: "BUY", isin: "XX0000000002", quantity: 1m, cancellation: true),
        Tx("T5", "2026-10-20T09:00:00Z", "WEIRD_STATE", "Odd one", cash: "FEE", amount: -1.5m),
        Tx("T6", "2026-09-01T09:00:00Z", "SETTLED", "Old deposit", cash: "DEPOSIT", amount: 100m),
        Tx("T7", "2026-08-01T09:00:00Z", "SETTLED", "Very old deposit", cash: "DEPOSIT", amount: 100m),
    ];

    public static JsonObject Tx(string id, string at, string status, string description, string? cash = null, string? security = null,
        decimal amount = 0, string? side = null, string? isin = null, decimal? quantity = null, bool cancellation = false)
    {
        var tx = new JsonObject
        {
            ["id"] = id,
            ["currency"] = "EUR",
            ["type"] = cash != null ? "CASH" : "SECURITY",
            ["status"] = status,
            ["isCancellation"] = cancellation,
            ["lastEventDateTime"] = at,
            ["description"] = description,
            ["amount"] = (double)amount,
        };
        if (cash != null) tx["cashTransactionType"] = cash;
        if (security != null) tx["securityTransactionType"] = security;
        if (side != null) tx["side"] = side;
        if (isin != null) tx["isin"] = isin;
        if (quantity != null) tx["quantity"] = (double)quantity;
        return tx;
    }

    private HttpResponseMessage TransactionPage(JsonObject vars, string? wrapper)
    {
        var cursor = vars["input"]!["cursor"]?.GetValue<string>();
        if (wrapper == null) Cursors.Add(cursor ?? "(first)");
        var feed = wrapper == null ? Transactions : [];
        var start = cursor == null ? 0 : int.Parse(cursor[1..], CultureInfo.InvariantCulture) * PageSize;
        var page = new JsonArray(feed.Skip(start).Take(PageSize).Select(t => t.DeepClone()).ToArray());
        var next = start + PageSize < feed.Count ? "c" + (((start / PageSize) + 1).ToString(CultureInfo.InvariantCulture)) : null;
        var summaries = new JsonObject { ["cursor"] = next, ["total"] = feed.Count, ["transactions"] = page };
        var portfolio = wrapper == null
            ? new JsonObject { ["id"] = PortfolioId, ["moreTransactions"] = summaries }
            : new JsonObject { ["id"] = PortfolioId, [wrapper] = new JsonObject { ["id"] = "w", ["moreTransactions"] = summaries } };
        return Gql(new JsonObject { ["data"] = new JsonObject { ["account"] = new JsonObject { ["brokerPortfolio"] = portfolio } } });
    }

    // ---- mailbox ----

    public static JsonObject Doc(string id, string date, string subject, string? portfolio) => new()
    {
        ["id"] = id,
        ["date"] = date,
        ["senderName"] = "Example Sender",
        ["read"] = false,
        ["subject"] = subject,
        ["fileName"] = id + ".pdf",
        ["fileType"] = "PDF",
        ["fileDownloadPath"] = $"./api/download?id={id}",
        ["portfolioId"] = portfolio,
    };

    private JsonObject Mailbox() => new()
    {
        ["data"] = new JsonObject { ["documentMailbox"] = new JsonObject { ["documents"] = new JsonArray(Documents.Select(d => d.DeepClone()).ToArray()) } },
    };

    private HttpResponseMessage Download(Uri uri, string? session)
    {
        if (session == null || !_signedIn.Contains(session)) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var id = uri.Query.Split("id=")[1];
        Downloads[id] = Downloads.GetValueOrDefault(id) + 1;
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.ASCII.GetBytes($"%PDF-1.4\n%document {id}\n%%EOF\n")) };
        response.Content.Headers.ContentType = new("application/pdf");
        return response;
    }

    private static HttpResponseMessage Text(string text, string contentType, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(text, Encoding.UTF8, contentType) };

    private static HttpResponseMessage Gql(object body) =>
        Text(body is JsonNode n ? n.ToJsonString() : JsonSerializer.Serialize(body), "application/json");
}
