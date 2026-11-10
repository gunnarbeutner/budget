using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBudget.Connectors.Scalable;

namespace OpenBudget.Connectors.Openbank;

/// <summary>
/// Openbank (Santander) Germany, i.e. the credit cards managed on kunden.openbankpay.com (for instance the
/// Amazon Visa), over the JSON API its web client uses.
///
/// Sign-in: username and password, then the second factor Openbank asks for (an SMS code, or an approval in
/// the app). That yields an access token and a refresh token; the refresh token is kept (encrypted) with the
/// login so later syncs trade it for a new access token instead of signing in again, until Openbank ends
/// the session. Nothing runs between syncs.
///
/// Every card in the global position is a credit-card account (id = Openbank's card id). Pending
/// (AUTHORIZED) card payments are reported unbooked; everything else (LIQUIDATED) is booked.
/// </summary>
public sealed class OpenbankConnector : IBankConnector
{
    public const string TypeName = "openbank";
    internal const string CodeReference = "openbank-otp";
    internal const string ApprovalReference = "openbank-approval";

    internal const string LoginPath = "cb-api-auth/authentication/login";
    internal const string OtpPath = "cb-api-auth/authentication/otp";
    internal const string ChallengePath = "cb-api-auth/authentication/challenges/";
    internal const string RefreshPath = "cb-api-auth/authentication/refresh-token";
    internal const string GlobalPositionPath = "cb-api-global-position/v3/global-position";
    internal const string CardsPath = "cb-api-cards/v3/cards/";

    private const string RefreshTokenKey = "refreshToken";
    private const string SessionExpiresKey = "sessionExpiresAt";
    private const int MaxPages = 50;

    private readonly ConnectorContext _context;
    private readonly OpenbankOptions _options;
    private readonly OpenbankApi _api;
    private readonly Dictionary<string, JsonElement> _cardDetails = new(StringComparer.Ordinal);
    private ConnectorSession _session = new();
    private string? _challengeId;

    public OpenbankConnector(ConnectorContext context, OpenbankOptions options)
    {
        _context = context;
        _options = options;
        _api = new OpenbankApi(context.Http, context.Logger, options);
    }

    public static ConnectorRegistration CreateRegistration(OpenbankOptions options) => new(
        TypeName,
        "Openbank",
        "Openbank credit cards (kunden.openbankpay.com, e.g. Amazon Visa): balance, booked and pending card transactions. Sign in with your Openbank username and password; confirm with the SMS code or in the app.",
        ctx => new OpenbankConnector(ctx, options),
        CredentialRequirement.UsernameAndSecret,
        Statements: false);

    public string ConnectorType => TypeName;

    private ILogger Log => _context.Logger;

    // ---- login ----

    public async Task<ConnectorStep<ConnectorSession?>> LoginAsync(
        ConnectorCredentials credentials, ConnectorSession? storedSession, ChallengeAnswer? answer, CancellationToken ct)
    {
        if (answer == null)
        {
            _session = storedSession ?? new ConnectorSession();
            if (await TryRefreshAsync(ct))
            {
                Log.LogInformation("Reusing the stored Openbank session.");
                return ConnectorStep.Done<ConnectorSession?>(_session);
            }
            return await StartLoginAsync(credentials, ct);
        }

        if (_challengeId == null) return ConnectorStep.Fail<ConnectorSession?>("The Openbank sign-in was interrupted; please sync again.");
        return answer.Challenge.Reference == ApprovalReference
            ? await PollApprovalAsync(ct)
            : await SubmitCodeAsync(answer.Value ?? "", ct);
    }

    /// <summary>Trades the stored refresh token for a new access token; false when there is none or Openbank refuses it.</summary>
    private async Task<bool> TryRefreshAsync(CancellationToken ct)
    {
        if (!_session.Values.TryGetValue(RefreshTokenKey, out var refreshToken) || string.IsNullOrEmpty(refreshToken)) return false;
        var response = await _api.SendAsync(HttpMethod.Post, RefreshPath, new JsonObject { ["refreshToken"] = refreshToken }, ct, authorized: false);
        if (response.Ok && TakeTokens(response.Body))
            return true;
        Log.LogInformation("The stored Openbank session is no longer accepted (HTTP {Status}); signing in again.", (int)response.Status);
        _session.Values.Remove(RefreshTokenKey);
        _session.Values.Remove(SessionExpiresKey);
        _api.AccessToken = null;
        return false;
    }

    private async Task<ConnectorStep<ConnectorSession?>> StartLoginAsync(ConnectorCredentials credentials, CancellationToken ct)
    {
        var response = await _api.SendAsync(HttpMethod.Post, LoginPath,
            new JsonObject { ["username"] = credentials.Username, ["password"] = credentials.Secret }, ct, authorized: false);
        if (response.Status is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
            return ConnectorStep.Fail<ConnectorSession?>("Openbank rejected the username or password.", credentialsRejected: true);
        if (response.Status == HttpStatusCode.Locked)
            return ConnectorStep.Fail<ConnectorSession?>("Openbank has locked the access; unlock it in the Openbank app or website first.", credentialsRejected: true);
        if (!response.Ok)
            return ConnectorStep.Fail<ConnectorSession?>($"Openbank's sign-in answered HTTP {(int)response.Status}.");
        if (TakeTokens(response.Body)) return SignedIn();
        return AskSecondFactor(response.Body);
    }

    /// <summary>The second factor Openbank asks for: a code (SMS) or an approval in the app.</summary>
    private ConnectorStep<ConnectorSession?> AskSecondFactor(JsonElement? body)
    {
        _challengeId = body.Str("challengeId");
        if (string.IsNullOrEmpty(_challengeId))
            return ConnectorStep.Fail<ConnectorSession?>("Openbank's sign-in answered without a session or a confirmation step.");
        var method = body.Str("method")?.ToUpperInvariant();
        if (method is "PUSH" or "APP")
            return ConnectorStep.Ask<ConnectorSession?>(ConnectorChallenge.Approval(
                "Confirm the sign-in in the Openbank app.", _options.ApprovalPollInterval, ApprovalReference));
        var length = body.Dec("otpLength") is { } l ? (int)l : (int?)null;
        return ConnectorStep.Ask<ConnectorSession?>(ConnectorChallenge.Code(
            "Enter the code Openbank sent you by SMS.", length, numeric: true, reference: CodeReference));
    }

    private async Task<ConnectorStep<ConnectorSession?>> SubmitCodeAsync(string code, CancellationToken ct)
    {
        var response = await _api.SendAsync(HttpMethod.Post, OtpPath,
            new JsonObject { ["challengeId"] = _challengeId, ["otp"] = code.Trim() }, ct, authorized: false);
        if (response.Ok && TakeTokens(response.Body)) return SignedIn();
        if (response.Status is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
            return ConnectorStep.Fail<ConnectorSession?>("Openbank did not accept the code.");
        return ConnectorStep.Fail<ConnectorSession?>($"Openbank's code check answered HTTP {(int)response.Status}.");
    }

    private async Task<ConnectorStep<ConnectorSession?>> PollApprovalAsync(CancellationToken ct)
    {
        var response = await _api.SendAsync(HttpMethod.Get, ChallengePath + Uri.EscapeDataString(_challengeId!), null, ct, authorized: false);
        if (!response.Ok)
            return ConnectorStep.Fail<ConnectorSession?>($"Openbank's confirmation check answered HTTP {(int)response.Status}.");
        if (TakeTokens(response.Body)) return SignedIn();
        return response.Body.Str("status")?.ToUpperInvariant() switch
        {
            "REJECTED" or "DENIED" or "CANCELLED" => ConnectorStep.Fail<ConnectorSession?>("The sign-in was rejected in the Openbank app."),
            "EXPIRED" => ConnectorStep.Fail<ConnectorSession?>("The confirmation in the Openbank app expired."),
            _ => ConnectorStep.Ask<ConnectorSession?>(ConnectorChallenge.Approval(
                "Confirm the sign-in in the Openbank app.", _options.ApprovalPollInterval, ApprovalReference)),
        };
    }

    private ConnectorStep<ConnectorSession?> SignedIn()
    {
        _challengeId = null;
        Log.LogInformation("Signed in to Openbank.");
        return ConnectorStep.Done<ConnectorSession?>(_session);
    }

    /// <summary>Takes accessToken/refreshToken from a token response; false when it carries none. Tokens are never logged.</summary>
    private bool TakeTokens(JsonElement? body)
    {
        var access = body.Str("accessToken");
        if (string.IsNullOrEmpty(access)) return false;
        _api.AccessToken = access;
        if (body.Str("refreshToken") is { Length: > 0 } refresh) _session.Values[RefreshTokenKey] = refresh;
        if (body.Str("sessionExpiresAt") is { Length: > 0 } expires)
        {
            _session.Values[SessionExpiresKey] = expires;
            Log.LogInformation("Openbank session valid until {ExpiresAt}.", expires);
        }
        return true;
    }

    /// <summary>Runs a data call; when the access token has run out mid-sync, refreshes once and retries.</summary>
    private async Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        try
        {
            return await _api.GetAsync(path, ct);
        }
        catch (OpenbankUnauthorizedException)
        {
            if (!await TryRefreshAsync(ct))
                throw new ConnectorException("The Openbank session has ended; the next sync signs in again.");
            try
            {
                return await _api.GetAsync(path, ct);
            }
            catch (OpenbankUnauthorizedException ex)
            {
                throw new ConnectorException("Openbank no longer accepts the session; the next sync signs in again.", ex);
            }
        }
    }

    public Task<ConnectorSession?> LogoutAsync(CancellationToken ct) =>
        Task.FromResult<ConnectorSession?>(_session.Values.ContainsKey(RefreshTokenKey) ? _session : null);

    // ---- accounts ----

    public async Task<IReadOnlyList<RemoteAccount>> ListAccountsAsync(CancellationToken ct)
    {
        var position = await GetAsync(GlobalPositionPath, ct);
        var accounts = new List<RemoteAccount>();
        foreach (var card in position.Path("resource", "cardsInfo", "cards").Items())
        {
            if (card.Str("id") is not { Length: > 0 } id) continue;
            var detail = await CardDetailAsync(id, ct);
            var last4 = detail.Str("last4DigitsPan") ?? card.Str("last4DigitsPan");
            var alias = card.Str("alias") ?? detail.Str("alias");
            var product = card.Str("productTypeName") ?? detail.Str("productTypeName") ?? "Openbank card";
            var name = !string.IsNullOrWhiteSpace(alias) ? alias.Trim() : last4 != null ? $"{product} ••{last4}" : product;
            var currency = card.Str("currencyCode") ?? detail.Str("currencyCode") ?? "EUR";
            var iban = detail.Str("virtualIban")?.Replace(" ", "", StringComparison.Ordinal);
            accounts.Add(new RemoteAccount(id, name, RemoteAccountType.CreditCard, string.IsNullOrEmpty(iban) ? null : iban, currency));
        }
        return accounts;
    }

    private async Task<JsonElement> CardDetailAsync(string cardId, CancellationToken ct)
    {
        if (_cardDetails.TryGetValue(cardId, out var cached)) return cached;
        var detail = (await GetAsync(CardsPath + Uri.EscapeDataString(cardId) + "?filter=BASIC", ct)).Path("resource") ?? default;
        _cardDetails[cardId] = detail;
        return detail;
    }

    // ---- balance and transactions ----

    public async Task<ConnectorStep<AccountRefresh>> RefreshAsync(RemoteAccount account, DateOnly? since, ChallengeAnswer? answer, CancellationToken ct)
    {
        var detail = await CardDetailAsync(account.RemoteId, ct);
        // The amount owed on the card (Openbank reports it positive) is a negative balance.
        var balance = detail.ValueKind == JsonValueKind.Object ? -detail.Dec("creditDetails", "currentBalance") : null;
        var transactions = await FetchTransactionsAsync(account, since, ct);
        return ConnectorStep.Done(new AccountRefresh(balance, transactions, []));
    }

    /// <summary>
    /// Reads the card's transactions newest first. The web client asks for one page of 99; when a page comes back
    /// full and still reaches <paramref name="since"/>, the next page is asked for with an offset. If Openbank
    /// refuses such a follow-up request, what was read so far is kept.
    /// </summary>
    private async Task<List<RemoteTransaction>> FetchTransactionsAsync(RemoteAccount account, DateOnly? since, CancellationToken ct)
    {
        var result = new List<RemoteTransaction>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var basePath = CardsPath + Uri.EscapeDataString(account.RemoteId) + "/transactions?limit=" + _options.PageSize.ToString(CultureInfo.InvariantCulture) + "&epp=true";
        for (var page = 0; page < MaxPages; page++)
        {
            JsonElement body;
            var path = page == 0 ? basePath : basePath + "&offset=" + (page * _options.PageSize).ToString(CultureInfo.InvariantCulture);
            try
            {
                body = await GetAsync(path, ct);
            }
            catch (ConnectorException ex) when (page > 0)
            {
                Log.LogWarning(ex, "Openbank refused a further page of card transactions; keeping the {Count} read so far.", result.Count);
                break;
            }

            var elements = body.Path("elements").Items().ToList();
            var fresh = 0;
            DateOnly? oldest = null;
            foreach (var e in elements)
            {
                var id = e.Str("id");
                if (id != null && !seen.Add(id)) continue;
                fresh++;
                if (Map(e, account.Currency) is not { } tx) continue;
                oldest = oldest is { } o && o < tx.BookingDate ? o : tx.BookingDate;
                if (since is { } s && tx.BookingDate < s) continue;
                result.Add(tx);
            }
            var full = elements.Count >= _options.PageSize;
            if (!full || fresh == 0 || (since is { } limit && oldest is { } old && old < limit)) break;
        }
        return result;
    }

    /// <summary>One card transaction; null for denied authorizations and entries without amount or date.</summary>
    internal static RemoteTransaction? Map(JsonElement e, string accountCurrency)
    {
        var denial = e.Str("denialCode");
        if (!string.IsNullOrEmpty(denial) && denial != "0") return null;
        var amountNode = e.Path("amounts").Items().FirstOrDefault();
        var amount = amountNode.ValueKind == JsonValueKind.Object ? amountNode.Dec("totalAmount") : null;
        amount ??= e.Dec("detailedAmount", "totalAmount");
        if (amount is not { } value) return null;
        if (!DateOnly.TryParseExact(e.Str("date") ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return null;
        DateOnly? valueDate = DateOnly.TryParseExact(e.Str("settlementDate") ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var settled)
            ? settled : null;
        var status = e.Str("transactionStatusCode")?.ToUpperInvariant();
        var booked = status is not ("AUTHORIZED" or "PENDING");
        var description = e.Str("description")?.Trim();
        var merchant = e.Str("merchantDetails", "name")?.Trim();
        var name = !string.IsNullOrEmpty(merchant) ? merchant : description;
        var purposeParts = new List<string>();
        if (!string.IsNullOrEmpty(description) && !string.Equals(description, name, StringComparison.Ordinal)) purposeParts.Add(description);
        if (e.Path("detailedAmount") is { } d && d.Str("originalCurrency") is { Length: > 0 } origCur
            && !string.Equals(origCur, accountCurrency, StringComparison.OrdinalIgnoreCase) && d.Dec("originalAmountExchange") is { } orig)
        {
            var rate = d.Dec("exchangeRate") is { } r ? $" (rate {r.ToString(CultureInfo.InvariantCulture)})" : "";
            purposeParts.Add($"{orig.ToString("0.00", CultureInfo.InvariantCulture)} {origCur}{rate}");
        }
        var currency = amountNode.ValueKind == JsonValueKind.Object ? amountNode.Str("currency") : null;
        return new RemoteTransaction(
            value,
            currency ?? e.Str("currencyCode") ?? accountCurrency,
            date,
            valueDate,
            name,
            purposeParts.Count > 0 ? string.Join(" · ", purposeParts) : null,
            booked,
            BankTransactionId: e.Str("id"));
    }

    // ---- statements ----

    /// <summary>Statements are not offered: the endpoint of Openbank's statement downloads is not known yet.</summary>
    public Task<ConnectorStep<IReadOnlyList<RemoteStatement>>> FetchStatementsAsync(
        IReadOnlyList<RemoteAccount> accounts, IReadOnlySet<string> knownIdentifiers, ChallengeAnswer? answer, CancellationToken ct) =>
        Task.FromResult(ConnectorStep.Done<IReadOnlyList<RemoteStatement>>([]));
}
