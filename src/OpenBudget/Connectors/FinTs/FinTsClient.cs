using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenBudget.Connectors.FinTs;

/// <summary>
/// A narrow FinTS 3.0 PIN/TAN client for one login: dialogs (init, synchronization, end), TAN handling
/// (choice of method, TAN entry, decoupled app approval), BPD/UPD caching and the business transactions
/// OpenBudget needs. The parameters the bank sent and the customer system id live in the connector session.
/// Nothing sensitive is logged: only segment names and return codes.
/// </summary>
internal sealed partial class FinTsClient
{
    public const string SystemIdKey = "fints.sysid";
    public const string TanMethodKey = "fints.tan";
    public const string BpdKey = "fints.bpd";
    public const string UpdKey = "fints.upd";
    public const string InventoryKeyPrefix = "fints.inv.";

    private const string OneStep = "999";
    private const string CamtV2 = "urn:iso:std:iso:20022:tech:xsd:camt.052.001.02";
    private const int MaxPages = 50;
    private static readonly int[] BalanceVersions = [5, 6, 7];
    private static readonly int[] Mt940Versions = [5, 6, 7];
    private static readonly int[] Mt940NationalVersions = [5, 6];
    private static readonly int[] CamtVersions = [1];
    private static readonly int[] DepotVersions = [5, 6];
    private static readonly int[] StatementVersions = [3, 4, 5];

    private static readonly HashSet<int> CredentialCodes = [9930, 9931, 9942, 3938];

    private readonly IFinTsTransport _transport;
    private readonly FinTsBankConfig _config;
    private readonly FinTsOptions _options;
    private readonly ConnectorCredentials _credentials;
    private readonly ConnectorSession _session;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    private FinTsBankData _data = FinTsBankData.Empty;
    private string _systemId = "0";
    private string? _tanId;
    private string _dialogId = "0";
    private int _messageNumber;
    private bool _dialogOpen;

    public FinTsClient(
        IFinTsTransport transport, FinTsBankConfig config, FinTsOptions options, ConnectorCredentials credentials,
        ConnectorSession? stored, TimeProvider clock, ILogger logger)
    {
        _transport = transport;
        _config = config;
        _options = options;
        _credentials = credentials;
        _session = stored ?? new ConnectorSession();
        _clock = clock;
        _logger = logger;
        LoadSession();
    }

    public ConnectorSession Session => _session;

    /// <summary>The session with the latest parameter data and ids, for the app to store.</summary>
    public ConnectorSession ExportSession()
    {
        SaveSession();
        return _session;
    }

    public IReadOnlyList<FinTsAccountInfo> AccountInfos => _data.Accounts;

    private void LoadSession()
    {
        var values = _session.Values;
        if (values.TryGetValue(SystemIdKey, out var sys) && sys.Length > 0) _systemId = sys;
        if (values.TryGetValue(TanMethodKey, out var tan) && tan.Length > 0) _tanId = tan;
        try
        {
            var bpd = values.TryGetValue(BpdKey, out var b) ? FinTsWire.Parse(b) : [];
            var upd = values.TryGetValue(UpdKey, out var u) ? FinTsWire.Parse(u) : [];
            _data = FinTsBankData.Create(bpd, upd);
        }
        catch (FinTsProtocolException)
        {
            _data = FinTsBankData.Empty;
        }
    }

    private void SaveSession()
    {
        var values = _session.Values;
        values[SystemIdKey] = _systemId;
        if (_tanId != null) values[TanMethodKey] = _tanId;
        values[BpdKey] = FinTsWire.Write(_data.BpdSegments);
        values[UpdKey] = FinTsWire.Write(_data.UpdSegments);
    }

    // ---------------------------------------------------------------- messages

    private string _lastSent = string.Empty;

    /// <summary>
    /// The outgoing message's shape for diagnosis: security function, whether a system id is set, and each segment's name,
    /// version and element count. Never values (PIN, user id, system id, TAN).
    /// </summary>
    private string Describe(List<FinTsSegmentBuilder> segments) =>
        $"HNHBKv3 HNVSKv3 HNVSDv1 HNSHKv4(sf={SecurityFunction}, sysid={(string.Equals(_systemId, "0", StringComparison.Ordinal) ? "unset" : "set")}) "
        + string.Join(' ', segments.Select(s => $"{s.Name}v{s.Version.ToString(CultureInfo.InvariantCulture)}[{s.ElementCount.ToString(CultureInfo.InvariantCulture)}]"))
        + " HNSHAv2 HNHBSv1";

    private string SecurityFunction => _tanId ?? _config.ProbeSecurityFunction ?? OneStep;

    private FinTsTanMethod? Method => _tanId == null ? null : _data.Methods.FirstOrDefault(m => string.Equals(m.Id, _tanId, StringComparison.Ordinal));

    private async Task<FinTsResponse> SendAsync(List<FinTsSegmentBuilder> segments, string? tan, CancellationToken ct)
    {
        _messageNumber++;
        var identity = new FinTsIdentity(_config.Blz, _credentials.Username, _credentials.Secret, _systemId);
        _lastSent = Describe(segments);
        var message = FinTsMessageBuilder.Build(
            identity, _dialogId, _messageNumber, SecurityFunction, RandomNumberGenerator.GetInt32(1_000_000, 10_000_000),
            _clock.GetUtcNow().UtcDateTime, segments, tan);
        _logger.LogDebug("FinTS send: {Segments}", string.Join(' ', segments.Select(s => s.Name + "v" + s.Version.ToString(CultureInfo.InvariantCulture))));

        var answer = await _transport.ExchangeAsync(message, ct);
        var response = FinTsResponse.Parse(answer);
        _logger.LogDebug(
            "FinTS receive: {Segments}; codes {Codes}",
            string.Join(' ', response.Segments.Select(s => s.Name)),
            string.Join(' ', response.Returns.Select(r => r.Code.ToString(CultureInfo.InvariantCulture))));

        if (string.Equals(_dialogId, "0", StringComparison.Ordinal) && response.DialogId.Length > 0 && !string.Equals(response.DialogId, "0", StringComparison.Ordinal))
        {
            _dialogId = response.DialogId;
            _dialogOpen = true;
        }
        Absorb(response);
        return response;
    }

    private void Absorb(FinTsResponse response)
    {
        if (FinTsBankData.ExtractBpd(response.Segments) is { } bpd) _data = _data.WithBpd(bpd);
        if (FinTsBankData.ExtractUpd(response.Segments) is { } upd) _data = _data.WithUpd(upd);
    }

    private static FinTsSegmentBuilder Tan(int version, string process, string? reference = null, string? jobName = null) =>
        process switch
        {
            "4" => new FinTsSegmentBuilder("HKTAN", version).Add("4").Add(jobName),
            // process 2 (TAN for an earlier order): segment id, account and order hash stay empty; the order reference
            // is element 5 and "further TAN follows" element 6 in HKTAN versions 6 and 7.
            _ => new FinTsSegmentBuilder("HKTAN", version).Add(process).Add(string.Empty).Add(string.Empty).Add(string.Empty).Add(reference).Add("N"),
        };

    // ---------------------------------------------------------------- dialogs

    /// <summary>Opens a dialog (init, plus synchronization when asked) and, where the bank wants it, takes the TAN step.</summary>
    private async Task<FinTsResponse> InitAsync(bool synchronize, IChallengeAsker ask, CancellationToken ct)
    {
        _dialogId = "0";
        _messageNumber = 0;
        _dialogOpen = false;

        var updVersion = _config.UpdInventory ? 0 : _data.UpdVersion;
        var segments = new List<FinTsSegmentBuilder>
        {
            new FinTsSegmentBuilder("HKIDN", 2).Add("280", _config.Blz).Add(_credentials.Username).Add(_systemId).Add("1"),
            new FinTsSegmentBuilder("HKVVB", 3)
                .Add(_data.BpdVersion.ToString(CultureInfo.InvariantCulture))
                .Add(updVersion.ToString(CultureInfo.InvariantCulture))
                .Add("1")
                .Add(_options.ProductId)
                .Add(_options.ProductVersion.Length > 5 ? _options.ProductVersion[..5] : _options.ProductVersion),
        };
        if (_tanId != null && !string.Equals(_tanId, OneStep, StringComparison.Ordinal) && Method is { Supported: true } m)
            segments.Add(Tan(m.HktanVersion, "4", jobName: "HKIDN"));
        if (synchronize) segments.Add(new FinTsSegmentBuilder("HKSYN", 3).Add("0"));

        var response = await SendAsync(segments, null, ct);
        return response.HasError ? response : await ResolveTanAsync(response, ask, ct);
    }

    /// <summary>
    /// Logs in: discovers the allowed TAN methods when none is chosen yet (asking the user among several),
    /// synchronizes the customer system id on the first login, and leaves a dialog open for the business transactions.
    /// </summary>
    public async Task LoginAsync(IChallengeAsker ask, CancellationToken ct)
    {
        if (!_options.IsConfigured) throw new FinTsException(FinTsOptions.NotConfiguredMessage);

        var needSync = string.Equals(_systemId, "0", StringComparison.Ordinal);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            // The system id is requested only once the TAN method is settled: a dialog under the one-step placeholder is just a probe.
            var syncing = needSync && _tanId != null;
            var response = await InitAsync(syncing, ask, ct);
            if (response.Returns.FirstOrDefault(r => CredentialCodes.Contains(r.Code)) is { } rejected)
                throw new FinTsException($"The bank rejected the login: {rejected.Text} ({rejected.Code})", credentialsRejected: true);

            var allowed = AllowedMethods(response);
            var wrongMethod = allowed != null && !allowed.Contains(SecurityFunction);
            if (response.HasError && !wrongMethod)
            {
                _logger.LogInformation("FinTS dialog init refused ({Error}); sent {Structure}", response.FirstError(), _lastSent);
                throw new FinTsException($"The bank refused the login: {response.FirstError()}");
            }
            if (wrongMethod)
            {
                await EndAsync(ct);
                _tanId = await ChooseMethodAsync(allowed!, ask);
                continue;
            }

            _tanId ??= allowed != null && allowed.Count == 1 ? allowed[0] : OneStep;
            if (needSync && !syncing)
            {
                await EndAsync(ct);
                continue;
            }
            if (syncing)
            {
                var synced = response.Of("HISYN").FirstOrDefault()?.Element(0);
                if (string.IsNullOrEmpty(synced)) throw new FinTsException("The bank did not assign a customer system id.");
                _systemId = synced;
                needSync = false;
                await EndAsync(ct);
                continue;
            }
            SaveSession();
            return;
        }
        throw new FinTsException("Could not agree on a TAN method with the bank.");
    }

    private static List<string>? AllowedMethods(FinTsResponse response) =>
        response.Find(3920) is { } r ? r.Parameters.Where(p => p.Length > 0).ToList() : null;

    private async Task<string> ChooseMethodAsync(List<string> allowed, IChallengeAsker ask)
    {
        var candidates = allowed.Where(id => _data.Methods.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal)) is not { Supported: false }).ToList();
        if (candidates.Count == 0) throw new FinTsException("The bank offers no TAN method this application supports.");
        if (candidates.Count == 1) return candidates[0];

        var options = candidates
            .Select(id => new ChallengeOption(id, _data.Methods.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal))?.Name ?? "TAN method " + id))
            .ToList();
        var answer = await ask.AskAsync(ConnectorChallenge.Choice("Choose a TAN method.", options, "fints-tan-method"));
        if (answer.Value == null || !candidates.Contains(answer.Value, StringComparer.Ordinal))
            throw new FinTsException("Unknown TAN method.");
        return answer.Value;
    }

    /// <summary>Ends the dialog; failures are ignored (the sync's result must not depend on a goodbye).</summary>
    public async Task EndAsync(CancellationToken ct)
    {
        if (!_dialogOpen) return;
        _dialogOpen = false;
        try
        {
            var response = await SendAsync([new FinTsSegmentBuilder("HKEND", 1).Add(_dialogId)], null, ct);
            _ = response;
        }
        catch (Exception ex) when (ex is ConnectorException or FinTsProtocolException)
        {
            _logger.LogDebug(ex, "FinTS dialog end failed.");
        }
    }

    // ---------------------------------------------------------------- TAN

    private async Task<FinTsResponse> ResolveTanAsync(FinTsResponse response, IChallengeAsker ask, CancellationToken ct)
    {
        for (var round = 0; round < 5; round++)
        {
            if (response.HasError) return response;
            var decoupledCode = response.Has(3955);
            if (!decoupledCode && !response.Has(30)) return response;

            var hitan = response.Of("HITAN").FirstOrDefault();
            var reference = hitan?.Element(2) ?? string.Empty;
            var method = Method;
            var version = method is { Supported: true } ? method.HktanVersion : 7;
            var text = CleanChallenge(hitan?.Element(3));

            if (decoupledCode || method?.Decoupled == true)
                response = await PollApprovalAsync(version, reference, text, method, ask, ct);
            else
                response = await EnterTanAsync(version, reference, text, method, ask, ct);
        }
        throw new FinTsException("The bank kept asking for further authorization.");
    }

    private async Task<FinTsResponse> PollApprovalAsync(int version, string reference, string bankText, FinTsTanMethod? method, IChallengeAsker ask, CancellationToken ct)
    {
        var message = string.IsNullOrWhiteSpace(bankText)
            ? $"Approve the request in your bank's app ({method?.Name ?? "decoupled TAN"})."
            : $"Approve the request in your bank's app: {bankText}";
        var maxPolls = method is { MaxPolls: > 0 } ? method.MaxPolls : 999;
        for (var poll = 0; poll < maxPolls; poll++)
        {
            var wait = poll == 0 ? method?.WaitFirst : method?.WaitNext;
            await ask.AskAsync(ConnectorChallenge.Approval(message, wait is { TotalSeconds: > 0 } w ? w : null, reference));
            var response = await SendAsync([Tan(version, "S", reference)], null, ct);
            if (response.HasError) return response;
            if (response.Has(3956) || response.Has(3955) || response.Has(30)) continue;
            return response;
        }
        throw new FinTsException("The approval in the bank's app did not arrive in time.");
    }

    private async Task<FinTsResponse> EnterTanAsync(int version, string reference, string bankText, FinTsTanMethod? method, IChallengeAsker ask, CancellationToken ct)
    {
        // The bank's text often only names the order ("Bitte bestätigen Sie …"), so say which TAN is expected.
        var how = method?.Name is { Length: > 0 } name
            ? name.Contains("sms", StringComparison.OrdinalIgnoreCase) ? $"Enter the TAN sent to you by SMS ({name})." : $"Enter the TAN from {name}."
            : "Enter the TAN your bank sent you.";
        var message = string.IsNullOrWhiteSpace(bankText) ? how : $"{how} {bankText.Trim()}";
        var answer = await ask.AskAsync(ConnectorChallenge.Code(message, method?.MaxInputLength, method?.Numeric ?? false, reference));
        if (string.IsNullOrWhiteSpace(answer.Value)) throw new FinTsException("No TAN was entered.");
        return await SendAsync([Tan(version, "2", reference)], answer.Value.Trim(), ct);
    }

    private static string CleanChallenge(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var withBreaks = BreakTag().Replace(text, "\n");
        return AnyTag().Replace(withBreaks, string.Empty).Trim();
    }

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BreakTag();

    [GeneratedRegex(@"<[^>]{0,200}>", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AnyTag();

    // ---------------------------------------------------------------- business transactions

    private async Task<List<FinTsResponse>> ExecuteAsync(string job, Func<string?, FinTsSegmentBuilder> build, IChallengeAsker ask, CancellationToken ct)
    {
        var pages = new List<FinTsResponse>();
        string? touchdown = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var segments = new List<FinTsSegmentBuilder> { build(touchdown) };
            if (Method is { Supported: true } m && _data.NeedsTan(job)) segments.Add(Tan(m.HktanVersion, "4", jobName: job));

            var response = await SendAsync(segments, null, ct);
            response = await ResolveTanAsync(response, ask, ct);
            if (response.HasError)
            {
                _logger.LogInformation("FinTS {Job} refused ({Error}); sent {Structure}", job, response.FirstError(), _lastSent);
                throw new FinTsException($"The bank rejected {job}: {response.FirstError()}");
            }
            pages.Add(response);

            var more = response.Returns.FirstOrDefault(r => r.Code == 3040 && (r.ForSegment == null || r.ForSegment == FinTsMessageBuilder.FirstBusinessSegment));
            touchdown = more is { Parameters.Count: > 0 } ? more.Parameters[0] : null;
            if (string.IsNullOrEmpty(touchdown)) return pages;
        }
        throw new FinTsException($"The bank kept sending further pages for {job}.");
    }

    private FinTsAccountInfo InfoFor(RemoteAccount account) =>
        _data.Accounts.FirstOrDefault(a => string.Equals(a.RemoteId, account.RemoteId, StringComparison.Ordinal))
        ?? throw new ConnectorException("The account is no longer listed by the bank.");

    /// <summary>The account as a data element group: national (number, sub-number, country, BLZ) or international (IBAN, BIC, ...).</summary>
    private string?[] AccountDeg(FinTsAccountInfo info, bool international)
    {
        var blz = info.Blz.Length > 0 ? info.Blz : _config.Blz;
        return international
            ? [info.Iban, info.Bic ?? _config.FallbackBic, info.AccountNumber, info.SubNumber, "280", blz]
            : [info.AccountNumber, info.SubNumber, "280", blz];
    }

    public IReadOnlyList<RemoteAccount> ListAccounts() =>
        _data.Accounts.Select(a => new RemoteAccount(a.RemoteId, AccountName(a), a.Kind, a.Iban, a.Currency)).ToList();

    private static string AccountName(FinTsAccountInfo a)
    {
        var tail = a.RemoteId.Length > 4 ? a.RemoteId[^4..] : a.RemoteId;
        var product = a.Product.Length > 0 ? a.Product : a.Name.Length > 0 ? a.Name : "Account";
        return $"{product} ...{tail}";
    }

    public async Task<AccountRefresh> RefreshAsync(RemoteAccount account, DateOnly? since, IChallengeAsker ask, CancellationToken ct)
    {
        var info = InfoFor(account);
        if (info.Kind == RemoteAccountType.Depot) return await RefreshDepotAsync(info, ask, ct);

        var inventoryKey = InventoryKeyPrefix + info.RemoteId;
        if (_config.UpdInventory && info.Inventory != null && since != null
            && _session.Values.TryGetValue(inventoryKey, out var seen) && string.Equals(seen, info.Inventory + "|0", StringComparison.Ordinal))
        {
            _logger.LogDebug("FinTS: nothing has changed according to the UPD inventory marker; skipping the transaction fetch.");
            return new AccountRefresh(null, [], []);
        }

        var transactions = await FetchTransactionsAsync(info, since, ask, ct);
        decimal? balance = null;
        if (info.Allows("HKSAL") && _data.BestVersion("HKSAL", string.IsNullOrEmpty(info.Iban) ? BalanceVersions.Where(v => v < 7).ToArray() : BalanceVersions) is { } version)
        {
            var pages = await ExecuteAsync("HKSAL", _ =>
            {
                var s = new FinTsSegmentBuilder("HKSAL", version).Add(AccountDeg(info, version >= 7)).Add("N");
                return s;
            }, ask, ct);
            balance = pages.SelectMany(p => p.Of("HISAL")).Select(FinTsBalance.FromSegment).FirstOrDefault(b => b != null)?.Booked;
        }

        if (info.Inventory != null) _session.Values[inventoryKey] = info.Inventory + (transactions.Any(t => !t.Booked) ? "|1" : "|0");
        return new AccountRefresh(balance, transactions, []);
    }

    private async Task<List<RemoteTransaction>> FetchTransactionsAsync(FinTsAccountInfo info, DateOnly? since, IChallengeAsker ask, CancellationToken ct)
    {
        // camt and the version 7 jobs address the account by IBAN, a mandatory field there; accounts the bank lists
        // without one (savings books, cooperative shares) can only be read with the national account number.
        var hasIban = !string.IsNullOrEmpty(info.Iban);
        if (hasIban && info.Allows("HKCAZ") && _data.BestVersion("HKCAZ", CamtVersions) is { } camtVersion && CamtFormat(camtVersion) is { } format)
        {
            var start = StartDate("HKCAZ", camtVersion, since);
            var pages = await ExecuteAsync("HKCAZ", touchdown =>
            {
                var seg = new FinTsSegmentBuilder("HKCAZ", camtVersion).Add(AccountDeg(info, true)).Add(format).Add("N").Add(start);
                return touchdown == null ? seg : seg.Add(string.Empty).Add(string.Empty).Add(touchdown);
            }, ask, ct);
            return ReadCamt(pages);
        }
        if (info.Allows("HKKAZ") && _data.BestVersion("HKKAZ", hasIban ? Mt940Versions : Mt940NationalVersions) is { } mtVersion)
        {
            var start = StartDate("HKKAZ", mtVersion, since);
            var pages = await ExecuteAsync("HKKAZ", touchdown =>
            {
                var seg = new FinTsSegmentBuilder("HKKAZ", mtVersion).Add(AccountDeg(info, mtVersion >= 7)).Add("N").Add(start);
                return touchdown == null ? seg : seg.Add(string.Empty).Add(string.Empty).Add(touchdown);
            }, ask, ct);
            return ReadMt940(pages, info.Currency);
        }
        throw new ConnectorException("The bank offers no way to read transactions of this account.");
    }

    /// <summary>
    /// The "from" date, always given (as MoneyMoney does) and kept inside the bank's storage window (the first parameter,
    /// in days, of HICAZS/HIKAZS): Baader refuses a missing or too old date with 9210, ING keeps 390 days.
    /// </summary>
    private string StartDate(string job, int version, DateOnly? since)
    {
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        var parameters = _data.ParameterSegment(job, version) is { Elements.Count: > 0 } p ? p.Elements[^1] : [];
        var days = parameters.Count > 0 && int.TryParse(parameters[0], NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : DefaultWindowDays;
        var earliest = today.AddDays(-(days - 1));
        var start = since ?? earliest;
        if (start < earliest) start = earliest;
        if (start > today) start = today;
        return FinTsFormats.FormatDate(start);
    }

    private const int DefaultWindowDays = 90;

    private string? CamtFormat(int version)
    {
        var formats = _data.ParameterSegment("HKCAZ", version)?.Deg(3).Skip(3).Where(f => f.Length > 0).ToList() ?? [];
        if (formats.Count == 0) return CamtV2;
        if (formats.Contains(CamtV2, StringComparer.Ordinal)) return CamtV2;
        return formats.FirstOrDefault(f => f.Contains("camt.052", StringComparison.Ordinal));
    }

    private static List<RemoteTransaction> ReadCamt(List<FinTsResponse> pages)
    {
        var result = new List<RemoteTransaction>();
        foreach (var segment in pages.SelectMany(p => p.Of("HICAZ")))
        {
            result.AddRange(UniqueDocuments(segment.Deg(2)).SelectMany(d => CamtParser.Parse(d, bookedDocument: true)));
            result.AddRange(UniqueDocuments(segment.Deg(3)).SelectMany(d => CamtParser.Parse(d, bookedDocument: false)));
        }
        return result;
    }

    private static IEnumerable<string> UniqueDocuments(IReadOnlyList<string> documents) =>
        documents.Where(d => d.Length > 0).Distinct(StringComparer.Ordinal);

    private static List<RemoteTransaction> ReadMt940(List<FinTsResponse> pages, string currency)
    {
        var result = new List<RemoteTransaction>();
        foreach (var segment in pages.SelectMany(p => p.Of("HIKAZ")))
        {
            if (segment.Element(0).Length > 0) result.AddRange(Mt940Parser.Parse(segment.Element(0), currency, booked: true));
            if (segment.Element(1).Length > 0) result.AddRange(Mt940Parser.Parse(segment.Element(1), currency, booked: false));
        }
        return result;
    }

    private async Task<AccountRefresh> RefreshDepotAsync(FinTsAccountInfo info, IChallengeAsker ask, CancellationToken ct)
    {
        var version = _data.BestVersion("HKWPD", DepotVersions) ?? throw new ConnectorException("The bank offers no depot statement.");
        var pages = await ExecuteAsync("HKWPD", touchdown =>
        {
            var seg = new FinTsSegmentBuilder("HKWPD", version).Add(AccountDeg(info, false));
            return touchdown == null ? seg : seg.Add(string.Empty).Add(string.Empty).Add(string.Empty).Add(touchdown);
        }, ask, ct);
        var positions = pages.SelectMany(p => p.Of("HIWPD")).SelectMany(s => Mt535Parser.Parse(s.Element(0))).ToList();
        var total = positions.Sum(p => p.Value ?? 0m);
        return new AccountRefresh(positions.Count == 0 ? null : total, [], positions);
    }

    // ---------------------------------------------------------------- statements (HKEKA)

    public async Task<List<RemoteStatement>> FetchStatementsAsync(
        IReadOnlyList<RemoteAccount> accounts, IReadOnlySet<string> known, IChallengeAsker ask, CancellationToken ct)
    {
        var result = new List<RemoteStatement>();
        if (_data.BestVersion("HKEKA", StatementVersions) is not { } version) return result;
        var formats = _data.ParameterSegment("HKEKA", version)?.Deg(3).Skip(3) ?? [];
        if (!formats.Contains("3", StringComparer.Ordinal)) return result;

        var thisYear = _clock.GetUtcNow().Year;
        var years = known.Count == 0 ? new[] { thisYear, thisYear - 1 } : [thisYear];
        foreach (var account in accounts)
        {
            var info = InfoFor(account);
            if (!info.Allows("HKEKA")) continue;
            foreach (var year in years)
            {
                var pages = await ExecuteAsync("HKEKA", touchdown =>
                {
                    var seg = new FinTsSegmentBuilder("HKEKA", version)
                        .Add(AccountDeg(info, version >= 4))
                        .Add("3")
                        .Add(string.Empty)
                        .Add(year.ToString(CultureInfo.InvariantCulture));
                    return touchdown == null ? seg : seg.Add(string.Empty).Add(touchdown);
                }, ask, ct);
                foreach (var segment in pages.SelectMany(p => p.Of("HIEKA")))
                {
                    if (ReadStatement(segment, account) is { } statement && !known.Contains(statement.Identifier)
                        && result.All(r => !string.Equals(r.Identifier, statement.Identifier, StringComparison.Ordinal)))
                        result.Add(statement);
                }
            }
        }
        return result;
    }

    private static RemoteStatement? ReadStatement(FinTsSegment segment, RemoteAccount account)
    {
        if (!string.Equals(segment.Element(0), "3", StringComparison.Ordinal)) return null;
        var range = segment.Deg(1);
        var start = range.Count > 0 ? range[0] : string.Empty;
        var end = range.Count > 1 ? range[1] : string.Empty;
        string content, key;
        DateOnly? date;
        if (segment.Version >= 5)
        {
            content = segment.Element(5);
            date = FinTsFormats.ParseDate(segment.Element(2)) ?? FinTsFormats.ParseDate(end);
            key = segment.Element(4).Length > 0 ? $"{segment.Element(3)}-{segment.Element(4)}" : $"{start}-{end}";
        }
        else
        {
            content = segment.Element(2);
            date = FinTsFormats.ParseDate(end);
            key = $"{start}-{end}";
        }
        if (!content.StartsWith("%PDF", StringComparison.Ordinal) || date == null) return null;
        return new RemoteStatement(
            $"eka:{account.RemoteId}:{key}", account.RemoteId, date.Value,
            $"Statement {date.Value:yyyy-MM-dd}", Encoding.Latin1.GetBytes(content));
    }
}
