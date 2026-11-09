using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenBudget.Connectors.Scalable;

public sealed partial class ScalableConnector
{
    private const string DepotSuffix = "depot";
    private const string CashSuffix = "cash";
    private const string DefaultCurrency = "EUR";

    private static string RemoteId(string portfolioId, string kind) => $"{portfolioId}:{kind}";

    private static (string PortfolioId, string Kind)? ParseRemoteId(string remoteId)
    {
        var at = remoteId.LastIndexOf(':', StringComparison.Ordinal);
        return at <= 0 ? null : (remoteId[..at], remoteId[(at + 1)..]);
    }

    private JsonObject Vars(string? portfolioId = null)
    {
        var vars = new JsonObject { ["personId"] = PersonId, ["userId"] = PersonId };
        if (portfolioId != null) vars["portfolioId"] = portfolioId;
        return vars;
    }

    // ---- accounts ----

    public async Task<IReadOnlyList<RemoteAccount>> ListAccountsAsync(CancellationToken ct)
    {
        var data = await _graphQl.ExecuteAsync(ScalableGraphQl.CockpitPath, "fetchCashAccountDetails", ScalableQueries.FetchCashAccountDetails,
            new JsonObject { ["userId"] = PersonId }, ct);
        var names = await FetchPortfolioNamesAsync(ct);

        var accounts = new List<RemoteAccount>();
        var number = 0;
        foreach (var portfolio in data.Path("account", "brokerPortfolios").Items())
        {
            number++;
            var id = portfolio.Str("id");
            if (id == null) continue;
            var name = names.GetValueOrDefault(id) ?? (number == 1 ? "Scalable" : $"Scalable {number}");
            accounts.Add(new RemoteAccount(RemoteId(id, DepotSuffix), $"{name} Depot", RemoteAccountType.Depot, null, DefaultCurrency));
            accounts.Add(new RemoteAccount(RemoteId(id, CashSuffix), $"{name} Verrechnungskonto", RemoteAccountType.Giro, portfolio.Str("cashAccount", "iban"), DefaultCurrency));
        }
#pragma warning disable S1135, MA0026 // a deliberate, documented gap
        // TODO: savings accounts (interest/api/graphql, account { savingsAccounts }): no query document captured yet, so they are not listed.
#pragma warning restore S1135, MA0026
        return accounts;
    }

    private async Task<Dictionary<string, string>> FetchPortfolioNamesAsync(CancellationToken ct)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var data = await _graphQl.ExecuteAsync(ScalableGraphQl.CockpitPath, "getBrokerPortfolios", ScalableQueries.GetBrokerPortfolios,
                new JsonObject { ["userId"] = PersonId }, ct);
            foreach (var portfolio in data.Path("account", "brokerPortfolios").Items())
            {
                var name = portfolio.Path("personalizations").Items().Select(p => p.Str("name")).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
                if (portfolio.Str("id") is { } id && name != null) names[id] = name;
            }
        }
        catch (ConnectorException ex)
        {
            Log.LogWarning(ex, "Portfolio names are not available ({Message}); using generic names.", ex.Message);
        }
        return names;
    }

    // ---- balances, bookings, positions ----

    public async Task<ConnectorStep<AccountRefresh>> RefreshAsync(RemoteAccount account, DateOnly? since, ChallengeAnswer? answer, CancellationToken ct)
    {
        try
        {
            var parsed = ParseRemoteId(account.RemoteId) ?? throw new ConnectorException($"Unknown Scalable account {account.RemoteId}.");
            return ConnectorStep.Done(parsed.Kind switch
            {
                DepotSuffix => await RefreshDepotAsync(parsed.PortfolioId, ct),
                CashSuffix => await RefreshCashAsync(parsed.PortfolioId, since, ct),
                _ => throw new ConnectorException($"Unknown Scalable account {account.RemoteId}."),
            });
        }
        catch (ScalableSessionExpiredException)
        {
            throw new ConnectorException("Scalable ended the session during the sync (UNAUTHENTICATED); the next sync logs in again.");
        }
    }

    private async Task<AccountRefresh> RefreshCashAsync(string portfolioId, DateOnly? since, CancellationToken ct)
    {
        var balanceData = await _graphQl.ExecuteAsync(ScalableGraphQl.BrokerPath, "getCashBalance", ScalableQueries.GetCashBalance, Vars(portfolioId), ct);
        var balance = balanceData.Dec("account", "brokerPortfolio", "payments", "buyingPower", "cashBalance");

        var transactions = await FetchTransactionsAsync(portfolioId, since, ScalableQueries.MoreTransactions, "moreTransactions",
            ["account", "brokerPortfolio", "moreTransactions"], optional: false, ct);
        transactions.AddRange(await FetchTransactionsAsync(portfolioId, since, ScalableQueries.MoreCryptoTransactions, "moreCryptoTransactions",
            ["account", "brokerPortfolio", "crypto", "moreTransactions"], optional: true, ct));
        return new AccountRefresh(balance, transactions, []);
    }

    private async Task<AccountRefresh> RefreshDepotAsync(string portfolioId, CancellationToken ct)
    {
        var data = await _graphQl.ExecuteAsync(ScalableGraphQl.BrokerPath, "getPortfolioGroupsInventory", ScalableQueries.GetPortfolioGroupsInventory, Vars(portfolioId), ct);
        var inventory = data.Path("account", "brokerPortfolio", "inventory");
        var securities = (inventory?.Path("portfolioGroups")).Items().SelectMany(g => g.Path("items").Items()).SelectMany(g => g.Path("items").Items())
            .Concat((inventory?.Path("ungroupedInventoryItems")).Items().SelectMany(g => g.Path("items").Items()));
        var positions = MapPositions(securities);

        // ETP holdings of the portfolio's second inventory; optional because portfolios without them may reject the query.
        try
        {
            var extra = await _graphQl.ExecuteAsync(ScalableGraphQl.BrokerPath, "getInventory", ScalableQueries.GetInventory, Vars(portfolioId), ct);
            var etps = extra.Path("account", "brokerPortfolio", "crypto", "coins").Items()
                .SelectMany(c => c.Path("inventory").Items()).Select(i => i.Path("etpSecurity")).Where(e => e != null).Select(e => e!.Value);
            var known = positions.Select(p => p.Isin).ToHashSet(StringComparer.Ordinal);
            positions.AddRange(MapPositions(etps).Where(p => p.Isin == null || !known.Contains(p.Isin)));
        }
        catch (ConnectorException ex)
        {
            Log.LogWarning(ex, "The extra inventory query failed; continuing with the regular positions: {Message}", ex.Message);
        }
        return new AccountRefresh(positions.Sum(p => p.Value ?? 0), [], positions);
    }

    private static List<RemotePosition> MapPositions(IEnumerable<JsonElement> securities)
    {
        var result = new List<RemotePosition>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in securities)
        {
            var quantity = s.Dec("inventory", "position", "filled");
            var isin = s.Str("isin");
            if (quantity is not > 0 || (isin != null && !seen.Add(isin))) continue;
            var price = s.Dec("quoteTick", "midPrice");
            result.Add(new RemotePosition(
                s.Str("name") ?? isin ?? "Unknown security", isin, s.Str("wkn"), null, quantity.Value,
                price, s.Str("quoteTick", "currency") ?? DefaultCurrency, price is { } p ? Math.Round(quantity.Value * p, 2, MidpointRounding.AwayFromZero) : null,
                s.Dec("inventory", "position", "fifoPrice")));
        }
        return result;
    }

    // ---- statements ----

    public async Task<ConnectorStep<IReadOnlyList<RemoteStatement>>> FetchStatementsAsync(
        IReadOnlyList<RemoteAccount> accounts, IReadOnlySet<string> knownIdentifiers, ChallengeAnswer? answer, CancellationToken ct)
    {
        try
        {
            var data = await _graphQl.ExecuteAsync(ScalableGraphQl.CockpitPath, "GetMailbox", ScalableQueries.GetMailbox,
                new JsonObject { ["userId"] = PersonId }, ct);
            var byPortfolio = MapDocumentAccounts(accounts);
            var result = new List<RemoteStatement>();
            foreach (var doc in data.Path("documentMailbox", "documents").Items())
            {
                if (await TryDownloadAsync(doc, knownIdentifiers, byPortfolio, ct) is { } statement) result.Add(statement);
            }
            return ConnectorStep.Done<IReadOnlyList<RemoteStatement>>(result);
        }
        catch (ScalableSessionExpiredException)
        {
            throw new ConnectorException("Scalable ended the session while fetching documents (UNAUTHENTICATED); the next sync logs in again.");
        }
    }

    /// <summary>Wanted accounts by portfolio id; a depot wins over the cash account of the same portfolio.</summary>
    private static Dictionary<string, string> MapDocumentAccounts(IReadOnlyList<RemoteAccount> accounts)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in accounts.OrderBy(a => a.Type == RemoteAccountType.Depot ? 0 : 1))
        {
            if (ParseRemoteId(a.RemoteId) is { } p) map.TryAdd(p.PortfolioId, a.RemoteId);
        }
        return map;
    }

    private async Task<RemoteStatement?> TryDownloadAsync(JsonElement doc, IReadOnlySet<string> known, Dictionary<string, string> byPortfolio, CancellationToken ct)
    {
        var id = doc.Str("id");
        var path = doc.Str("fileDownloadPath");
        if (id == null || string.IsNullOrEmpty(path) || known.Contains(id)) return null;
        if (doc.Str("fileType") is { } type && !type.Contains("pdf", StringComparison.OrdinalIgnoreCase)) return null;

        string? accountId = null;
        if (doc.Str("portfolioId") is { Length: > 0 } portfolio && !byPortfolio.TryGetValue(portfolio, out accountId))
            return null; // belongs to a portfolio whose statements are not wanted

        var url = new Uri(new Uri(_options.BaseUri, "cockpit/"), path);
        if (!IsAppHost(url)) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _context.Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType is "text/html")
        {
            Log.LogWarning("Scalable document download failed: HTTP {Status}; it is retried at the next sync.", (int)response.StatusCode);
            return null;
        }
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        var date = DateTimeOffset.TryParse(doc.Str("date"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? DateOnly.FromDateTime(d.UtcDateTime) : DateOnly.FromDateTime(Now);
        return new RemoteStatement(id, accountId, date, doc.Str("subject") ?? doc.Str("fileName") ?? id, bytes);
    }
}
