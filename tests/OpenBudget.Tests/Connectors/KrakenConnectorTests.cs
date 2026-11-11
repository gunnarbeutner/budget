#pragma warning disable CA2000 // test objects live for the test
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Kraken;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>
/// The Kraken connector against a made-up exchange API that checks every request's signature (no network). Asset
/// codes and amounts are invented.
/// </summary>
public sealed class KrakenConnectorTests
{
    private const string ApiKey = "test-api-key";
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("a made-up private key for tests");
    private static readonly string SecretBase64 = Convert.ToBase64String(Secret);

    /// <summary>A fake API: private calls must carry the key and a valid signature over a strictly increasing nonce.</summary>
    private sealed class FakeApi : HttpMessageHandler
    {
        public JsonObject Balances { get; set; } = new();

        public JsonObject Pairs { get; set; } = new();

        public JsonObject Ticker { get; set; } = new();

        /// <summary>Errors the next private calls answer with, one per call.</summary>
        public Queue<string> PrivateErrors { get; } = new();

        public List<string> Requests { get; } = [];

        public long LastNonce { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path + request.RequestUri.Query);
            if (path.StartsWith("/0/public/", StringComparison.Ordinal))
            {
                return path switch
                {
                    "/0/public/AssetPairs" => Ok(Pairs),
                    "/0/public/Ticker" => Ok(Ticker),
                    _ => Error("EQuery:Unknown method"),
                };
            }

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var nonceText = body.Split('&').First(p => p.StartsWith("nonce=", StringComparison.Ordinal))["nonce=".Length..];
            var key = request.Headers.GetValues("API-Key").Single();
            var sign = request.Headers.GetValues("API-Sign").Single();
            if (key != ApiKey) return Error("EAPI:Invalid key");
            if (sign != KrakenApi.Sign(Secret, path, nonceText, body)) return Error("EAPI:Invalid signature");
            var nonce = long.Parse(nonceText, System.Globalization.CultureInfo.InvariantCulture);
            if (nonce <= LastNonce) return Error("EAPI:Invalid nonce");
            LastNonce = nonce;
            if (PrivateErrors.TryDequeue(out var queued)) return Error(queued);
            return path switch
            {
                "/0/private/Balance" => Ok(new JsonObject(Balances.Select(b => KeyValuePair.Create(b.Key, b.Value?["balance"]?.DeepClone())))),
                "/0/private/BalanceEx" => Ok(Balances),
                _ => Error("EGeneral:Unknown method"),
            };
        }

        private static HttpResponseMessage Ok(JsonObject result) => Json(new JsonObject { ["error"] = new JsonArray(), ["result"] = result.DeepClone() });

        private static HttpResponseMessage Error(string error) => Json(new JsonObject { ["error"] = new JsonArray(error) });

        private static HttpResponseMessage Json(JsonObject o) =>
            new(HttpStatusCode.OK) { Content = new StringContent(o.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    private static JsonObject Holding(string amount) => new() { ["balance"] = amount, ["hold_trade"] = "0" };

    private static JsonObject Pair(string @base, string quote) => new() { ["base"] = @base, ["quote"] = quote };

    private static JsonObject Last(string price) => new() { ["c"] = new JsonArray(price, "1.0") };

    private static FakeApi DefaultApi() => new()
    {
        Balances = new JsonObject
        {
            ["ZEUR"] = Holding("12.3400"),
            ["XABC"] = Holding("2.5"),
            ["XABC.S"] = Holding("1.0"),
            ["NOPR"] = Holding("4"),
            ["ZUSD"] = Holding("10"),
            ["ZERO"] = Holding("0.0000"),
        },
        Pairs = new JsonObject
        {
            ["XABCZEUR"] = Pair("XABC", "ZEUR"),
            ["ZEURZUSD"] = Pair("ZEUR", "ZUSD"),
            ["XABCZUSD"] = Pair("XABC", "ZUSD"),
        },
        Ticker = new JsonObject
        {
            ["XABCZEUR"] = Last("8.00"),
            ["ZEURZUSD"] = Last("1.25"),
        },
    };

    private static KrakenConnector Connector(FakeApi api, CapturingLoggerFactory? logs = null, int retries = 3) =>
        new(new ConnectorContext(new BankHttpClient(api), TimeProvider.System, (logs ?? new CapturingLoggerFactory()).CreateLogger("kraken")),
            new KrakenOptions { BaseUri = new Uri("https://api.example.test/"), RateLimitDelay = TimeSpan.Zero, RateLimitRetries = retries });

    private static ConnectorCredentials Credentials(string key = ApiKey, string? secret = null) => new(key, secret ?? SecretBase64);

    [Fact]
    public void Signature_matches_the_documented_example()
    {
        // The example from Kraken's REST authentication documentation.
        var secret = Convert.FromBase64String("kQH5HW/8p1uGOVjbgWA7FunAmGO8lsSUXNsu3eow76sz84Q18fWxnyRzBHCd3pd5nE9qa99HAZtuZuj6F1huXg==");
        var sign = KrakenApi.Sign(secret, "/0/private/AddOrder", "1616492376594",
            "nonce=1616492376594&ordertype=limit&pair=XBTUSD&price=37500&type=buy&volume=1.25");
        Assert.Equal("4/dpxb3iT4tp/ZCVEwSnEsLxx0bqyhLpdfOpc6fn7OR8+UClSV5n9E6aSS8MPtnRfp32bAb0nmbRn6H8ndwLUQ==", sign);
    }

    [Theory]
    [InlineData("ZEUR", "EUR", "Euro")]
    [InlineData("EUR.HOLD", "EUR", "Euro (on hold)")]
    [InlineData("XABC", "XABC", "XABC")]
    [InlineData("ABC.S", "ABC", "ABC (staked)")]
    [InlineData("ABC.M", "ABC", "ABC (rewards)")]
    [InlineData("ABC.F", "ABC", "ABC (earn)")]
    [InlineData("ABC.Q", "ABC", "ABC (Q)")]
    [InlineData("ZUSD", "USD", "USD")]
    public void Asset_codes_map_to_display_names(string code, string expectedCode, string expectedLabel)
    {
        var asset = KrakenConnector.MapAsset(code);
        Assert.Equal(expectedCode, asset.Code);
        Assert.Equal(expectedLabel, asset.Label);
    }

    [Fact]
    public async Task Balances_become_priced_positions_of_one_depot()
    {
        var api = DefaultApi();
        var logs = new CapturingLoggerFactory();
        var connector = Connector(api, logs);

        var login = await connector.LoginAsync(Credentials(), null, null, CancellationToken.None);
        Assert.Equal(StepStatus.Done, login.Status);
        var account = Assert.Single(await connector.ListAccountsAsync(CancellationToken.None));
        Assert.Equal(RemoteAccountType.Depot, account.Type);
        Assert.Equal("Kraken", account.Name);

        var refresh = await connector.RefreshAsync(account, null, null, CancellationToken.None);
        Assert.Equal(StepStatus.Done, refresh.Status);
        var positions = refresh.Value!.Positions.ToDictionary(p => p.Name, StringComparer.Ordinal);
        Assert.Equal(5, positions.Count); // the zero balance is left out

        Assert.Equal(1m, positions["Euro"].Price);
        Assert.Equal(12.34m, positions["Euro"].Value);
        Assert.Equal(8m, positions["XABC"].Price);
        Assert.Equal(20m, positions["XABC"].Value);
        Assert.Equal(8m, positions["XABC (staked)"].Price);
        Assert.Equal(8m, positions["XABC (staked)"].Value);
        Assert.Equal(0.8m, positions["USD"].Price); // EUR/USD 1.25 inverted
        Assert.Equal(8m, positions["USD"].Value);
        Assert.Null(positions["NOPR"].Price);
        Assert.Null(positions["NOPR"].Value);
        Assert.All(positions.Values, p => Assert.Equal("EUR", p.PriceCurrency));
        Assert.Equal(48.34m, refresh.Value.Balance);
        Assert.Empty(refresh.Value.Transactions);

        Assert.Contains("NOPR has no EUR price", logs.All, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, logs.All, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretBase64, logs.All, StringComparison.Ordinal);
        Assert.DoesNotContain("12.34", logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_last_nonce_is_kept_for_the_next_sync()
    {
        var api = DefaultApi();
        var first = Connector(api);
        await first.LoginAsync(Credentials(), null, null, CancellationToken.None);
        var session = await first.LogoutAsync(CancellationToken.None);
        Assert.Equal(api.LastNonce.ToString(System.Globalization.CultureInfo.InvariantCulture), session!.Values[KrakenConnector.LastNonceKey]);

        // A stored nonce ahead of the clock still yields a larger one.
        session.Values[KrakenConnector.LastNonceKey] = (api.LastNonce + 1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        api.LastNonce += 1_000_000;
        var second = Connector(api);
        Assert.Equal(StepStatus.Done, (await second.LoginAsync(Credentials(), session, null, CancellationToken.None)).Status);
    }

    [Theory]
    [InlineData("EAPI:Invalid key", "API key")]
    [InlineData("EAPI:Invalid signature", "private key")]
    [InlineData("EGeneral:Permission denied", "Query Funds")]
    public async Task Refused_keys_are_reported_as_rejected_credentials(string error, string hint)
    {
        var api = DefaultApi();
        api.PrivateErrors.Enqueue(error);
        var step = await Connector(api).LoginAsync(Credentials(), null, null, CancellationToken.None);
        Assert.Equal(StepStatus.Failed, step.Status);
        Assert.True(step.CredentialsRejected);
        Assert.Contains(hint, step.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wrong_key_or_secret_is_rejected()
    {
        var wrongKey = await Connector(DefaultApi()).LoginAsync(Credentials(key: "other"), null, null, CancellationToken.None);
        Assert.True(wrongKey.CredentialsRejected);
        var wrongSecret = await Connector(DefaultApi()).LoginAsync(Credentials(secret: Convert.ToBase64String(new byte[] { 1, 2, 3 })), null, null, CancellationToken.None);
        Assert.True(wrongSecret.CredentialsRejected);
        Assert.Contains("signature", wrongSecret.FailureReason, StringComparison.Ordinal);
        var notBase64 = await Connector(DefaultApi()).LoginAsync(Credentials(secret: "not base64 !"), null, null, CancellationToken.None);
        Assert.True(notBase64.CredentialsRejected);
    }

    [Fact]
    public async Task Rate_limits_are_retried_then_reported()
    {
        var api = DefaultApi();
        api.PrivateErrors.Enqueue("EAPI:Rate limit exceeded");
        api.PrivateErrors.Enqueue("EAPI:Rate limit exceeded");
        Assert.Equal(StepStatus.Done, (await Connector(api).LoginAsync(Credentials(), null, null, CancellationToken.None)).Status);

        var limited = DefaultApi();
        for (var i = 0; i < 5; i++) limited.PrivateErrors.Enqueue("EAPI:Rate limit exceeded");
        var ex = await Assert.ThrowsAsync<ConnectorException>(() => Connector(limited, retries: 2).LoginAsync(Credentials(), null, null, CancellationToken.None));
        Assert.Contains("Rate limit", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_nonce_is_retried_once()
    {
        var api = DefaultApi();
        api.PrivateErrors.Enqueue("EAPI:Invalid nonce");
        Assert.Equal(StepStatus.Done, (await Connector(api).LoginAsync(Credentials(), null, null, CancellationToken.None)).Status);

        var twice = DefaultApi();
        twice.PrivateErrors.Enqueue("EAPI:Invalid nonce");
        twice.PrivateErrors.Enqueue("EAPI:Invalid nonce");
        await Assert.ThrowsAsync<ConnectorException>(() => Connector(twice).LoginAsync(Credentials(), null, null, CancellationToken.None));
    }

    [Fact]
    public async Task Only_euros_need_no_price_lookup()
    {
        var api = DefaultApi();
        api.Balances = new JsonObject { ["ZEUR"] = Holding("1.5") };
        var connector = Connector(api);
        await connector.LoginAsync(Credentials(), null, null, CancellationToken.None);
        var refresh = await connector.RefreshAsync((await connector.ListAccountsAsync(CancellationToken.None))[0], null, null, CancellationToken.None);
        Assert.Equal(1.5m, refresh.Value!.Balance);
        Assert.DoesNotContain(api.Requests, r => r.StartsWith("/0/public/", StringComparison.Ordinal));
    }
}
