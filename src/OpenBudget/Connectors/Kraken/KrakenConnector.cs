using System.Globalization;
using System.Text.Json;

namespace OpenBudget.Connectors.Kraken;

/// <summary>
/// Kraken over its REST interface with a read-only API key (username = API key, secret = private key in base64;
/// permissions "Query Funds" and "Query Ledger Entries").
///
/// One depot account "Kraken" (remote id "depot") in EUR whose positions are the assets with a nonzero balance
/// (<c>/0/private/BalanceEx</c>). Each asset is valued in EUR with the last trade price of its EUR pair
/// (<c>/0/public/AssetPairs</c>, <c>/0/public/Ticker</c>); euros are a "Euro" position at 1.00, other currencies use
/// their pair with EUR in either direction. An asset without such a pair has no price and counts 0. Variants with a
/// suffix (".S", ".M", ".F", ...) are separate positions named after the base asset, e.g. "ABC (staked)". The account
/// balance is the sum of the position values. Ledger entries are not fetched: depot accounts hold positions only.
/// </summary>
public sealed class KrakenConnector : IBankConnector
{
    public const string TypeName = "kraken";
    internal const string DepotRemoteId = "depot";
    internal const string LastNonceKey = "lastNonce";
    private const string Eur = "EUR";

    private readonly ConnectorContext _context;
    private readonly KrakenApi _api;
    private ConnectorSession _session = new();

    public KrakenConnector(ConnectorContext context, KrakenOptions options)
    {
        _context = context;
        _api = new KrakenApi(context.Http, context.Clock, context.Logger, options);
    }

    public static ConnectorRegistration CreateRegistration(KrakenOptions options) => new(
        TypeName,
        "Kraken",
        "Kraken balances as one depot account, valued in EUR. Use a read-only API key: on kraken.com go to Settings › API › Create API key and allow only “Query Funds” and “Query Ledger Entries”. Enter the API key as username and the private key as password.",
        ctx => new KrakenConnector(ctx, options),
        CredentialRequirement.UsernameAndSecret,
        Statements: false);

    public string ConnectorType => TypeName;

    private ILogger Log => _context.Logger;

    public async Task<ConnectorStep<ConnectorSession?>> LoginAsync(
        ConnectorCredentials credentials, ConnectorSession? storedSession, ChallengeAnswer? answer, CancellationToken ct)
    {
        _session = storedSession ?? new ConnectorSession();
        if (_session.Values.TryGetValue(LastNonceKey, out var text) && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var last))
            _api.LastNonce = last;
        if (!_api.UseKey(credentials.Username, credentials.Secret))
            return ConnectorStep.Fail<ConnectorSession?>("The private key is not valid base64. Copy it from Kraken's API key page as shown.", credentialsRejected: true);
        try
        {
            // A cheap call that needs "Query Funds": proves key, signature and permission.
            await _api.PrivateAsync("Balance", ct);
        }
        catch (KrakenCredentialsException ex)
        {
            return ConnectorStep.Fail<ConnectorSession?>(ex.Message, credentialsRejected: true);
        }
        finally
        {
            RememberNonce();
        }
        Log.LogInformation("Signed in to Kraken with an API key.");
        return ConnectorStep.Done<ConnectorSession?>(_session);
    }

    public Task<IReadOnlyList<RemoteAccount>> ListAccountsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RemoteAccount>>([new RemoteAccount(DepotRemoteId, "Kraken", RemoteAccountType.Depot, null, Eur)]);

    public async Task<ConnectorStep<AccountRefresh>> RefreshAsync(RemoteAccount account, DateOnly? since, ChallengeAnswer? answer, CancellationToken ct)
    {
        if (account.RemoteId != DepotRemoteId) throw new ConnectorException($"Unknown Kraken account {account.RemoteId}.");
        JsonElement balances;
        try
        {
            balances = await _api.PrivateAsync("BalanceEx", ct);
        }
        catch (KrakenCredentialsException ex)
        {
            return ConnectorStep.Fail<AccountRefresh>(ex.Message, credentialsRejected: true);
        }
        finally
        {
            RememberNonce();
        }

        var holdings = ReadBalances(balances);
        var prices = await FetchEurPricesAsync(holdings.Select(h => h.Asset.Code).Where(c => c != Eur).Distinct(StringComparer.Ordinal).ToList(), ct);
        var positions = new List<RemotePosition>();
        foreach (var (asset, quantity) in holdings)
        {
            decimal? price = asset.Code == Eur ? 1m : prices.TryGetValue(asset.Code, out var p) ? p : null;
            if (price == null) Log.LogInformation("Kraken asset {Asset} has no EUR price; it counts 0.", asset.Code);
            positions.Add(new RemotePosition(
                asset.Label, null, null, asset.Code, quantity, price, Eur,
                price is { } x ? Math.Round(quantity * x, 2, MidpointRounding.AwayFromZero) : null));
        }
        Log.LogInformation("Kraken: {Count} positions ({Assets}).", positions.Count, string.Join(", ", holdings.Select(h => h.Asset.Code + h.Asset.Suffix)));
        return ConnectorStep.Done(new AccountRefresh(positions.Sum(p => p.Value ?? 0), [], positions));
    }

    public Task<ConnectorStep<IReadOnlyList<RemoteStatement>>> FetchStatementsAsync(
        IReadOnlyList<RemoteAccount> accounts, IReadOnlySet<string> knownIdentifiers, ChallengeAnswer? answer, CancellationToken ct) =>
        Task.FromResult(ConnectorStep.Done<IReadOnlyList<RemoteStatement>>([]));

    public Task<ConnectorSession?> LogoutAsync(CancellationToken ct)
    {
        RememberNonce();
        return Task.FromResult<ConnectorSession?>(_session);
    }

    private void RememberNonce()
    {
        if (_api.LastNonce > 0) _session.Values[LastNonceKey] = _api.LastNonce.ToString(CultureInfo.InvariantCulture);
    }

    // ---- assets ----

    /// <summary>An asset as shown: its common code (e.g. "EUR"), the variant suffix (e.g. ".S") and the position name.</summary>
    internal sealed record KrakenAsset(string Code, string Suffix, string Label);

    private static readonly Dictionary<string, string> LegacyCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ZEUR"] = "EUR",
        ["ZUSD"] = "USD",
        ["ZGBP"] = "GBP",
        ["ZCAD"] = "CAD",
        ["ZJPY"] = "JPY",
        ["ZAUD"] = "AUD",
        ["ZCHF"] = "CHF",
        ["XXBT"] = "BTC",
        ["XBT"] = "BTC",
        ["XETH"] = "ETH",
        ["XXRP"] = "XRP",
        ["XLTC"] = "LTC",
        ["XXLM"] = "XLM",
        ["XXMR"] = "XMR",
        ["XZEC"] = "ZEC",
        ["XETC"] = "ETC",
        ["XXDG"] = "DOGE",
        ["XDG"] = "DOGE",
        ["XREP"] = "REP",
        ["XMLN"] = "MLN",
    };

    private static readonly Dictionary<string, string> SuffixLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["S"] = "staked",
        ["B"] = "staked",
        ["M"] = "rewards",
        ["F"] = "earn",
        ["P"] = "parachain",
        ["HOLD"] = "on hold",
    };

    /// <summary>Kraken's asset code as shown: legacy X/Z prefixed codes map to the common ones, suffix variants keep the suffix in the name.</summary>
    internal static KrakenAsset MapAsset(string krakenCode)
    {
        var dot = krakenCode.IndexOf('.', StringComparison.Ordinal);
        var baseCode = dot < 0 ? krakenCode : krakenCode[..dot];
        var suffix = dot < 0 ? "" : krakenCode[dot..];
        var code = Normalize(baseCode);
        var name = code == Eur ? "Euro" : code;
        if (suffix.Length > 1) name += " (" + SuffixLabels.GetValueOrDefault(suffix[1..], suffix[1..]) + ")";
        return new KrakenAsset(code, suffix, name);
    }

    private static string Normalize(string code) => LegacyCodes.GetValueOrDefault(code, code.ToUpperInvariant());

    /// <summary>Nonzero balances from BalanceEx (<c>{"ZEUR":{"balance":"1.0",...}}</c>) or Balance (<c>{"ZEUR":"1.0"}</c>).</summary>
    internal static List<(KrakenAsset Asset, decimal Quantity)> ReadBalances(JsonElement result)
    {
        var list = new List<(KrakenAsset, decimal)>();
        if (result.ValueKind != JsonValueKind.Object) return list;
        foreach (var property in result.EnumerateObject())
        {
            var value = property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty("balance", out var b) ? b : property.Value;
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
            if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var quantity) || quantity == 0) continue;
            list.Add((MapAsset(property.Name), quantity));
        }
        return list;
    }

    /// <summary>EUR price per unit of each asset code that has a pair with EUR (either direction); others are left out.</summary>
    private async Task<Dictionary<string, decimal>> FetchEurPricesAsync(List<string> codes, CancellationToken ct)
    {
        var prices = new Dictionary<string, decimal>(StringComparer.Ordinal);
        if (codes.Count == 0) return prices;
        var pairs = await _api.PublicAsync("AssetPairs", null, ct);
        // Per asset code the pair to price it with: ASSET/EUR, or else EUR/ASSET (inverted).
        var byCode = new Dictionary<string, (string Key, bool Inverse)>(StringComparer.Ordinal);
        foreach (var (key, @base, quote) in PairList(pairs))
        {
            if (quote == Eur && codes.Contains(@base, StringComparer.Ordinal) && (!byCode.TryGetValue(@base, out var found) || found.Inverse)) byCode[@base] = (key, false);
            else if (@base == Eur && codes.Contains(quote, StringComparer.Ordinal)) byCode.TryAdd(quote, (key, true));
        }
        if (byCode.Count == 0) return prices;
        var wanted = byCode.ToDictionary(e => e.Value.Key, e => (Code: e.Key, e.Value.Inverse), StringComparer.Ordinal);

        var ticker = await _api.PublicAsync("Ticker", "pair=" + string.Join(',', wanted.Keys), ct);
        foreach (var (key, (code, inverse)) in wanted)
        {
            if (ticker.ValueKind != JsonValueKind.Object || !ticker.TryGetProperty(key, out var t) || !t.TryGetProperty("c", out var c)
                || c.ValueKind != JsonValueKind.Array || c.GetArrayLength() == 0
                || !decimal.TryParse(c[0].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var last) || last <= 0)
                continue;
            prices[code] = inverse ? 1m / last : last;
        }
        return prices;

        static IEnumerable<(string Key, string Base, string Quote)> PairList(JsonElement all)
        {
            if (all.ValueKind != JsonValueKind.Object) yield break;
            foreach (var p in all.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Object) continue;
                var b = p.Value.TryGetProperty("base", out var x) ? x.GetString() : null;
                var q = p.Value.TryGetProperty("quote", out var y) ? y.GetString() : null;
                if (b == null || q == null) continue;
                yield return (p.Name, Normalize(b), Normalize(q));
            }
        }
    }
}
