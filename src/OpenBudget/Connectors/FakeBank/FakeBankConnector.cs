using System.Globalization;

namespace OpenBudget.Connectors.FakeBank;

/// <summary>
/// A bank that exists only in this process: deterministic, obviously made-up data (DE00 IBANs,
/// "Example ..." names) and switchable behaviours (see <see cref="FakeBankSettings"/>) so every
/// part of the connector framework can be exercised without touching a real bank. Enabled in
/// tests, and in a running app only with OPENBUDGET_ENABLE_FAKE_BANK=true.
/// </summary>
public sealed class FakeBankConnector : IBankConnector
{
    public const string TypeName = "fake";
    public const string GiroId = "FAKE-GIRO";
    public const string SavingsId = "FAKE-SAVINGS";
    public const string DepotId = "FAKE-DEPOT";
    public const string GiroIban = "DE00000000000000000001";
    public const string CodeAnswer = "123456";
    public const string ImageAnswer = "4711";
    public const decimal GiroBalance = 1234.56m;

    // Smallest valid PNG (1x1 pixel) standing in for a photoTAN picture.
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private readonly ConnectorContext _context;
    private FakeBankSettings _settings = FakeBankSettings.Parse(null);
    private ConnectorSession _session = new();
    private readonly List<string> _loginStages = [];
    private int _stage;
    private int _polls;
    private bool _refreshCodeAsked;
    private bool _statementCodeAsked;
    private bool _refreshed;

    public FakeBankConnector(ConnectorContext context) => _context = context;

    public static ConnectorRegistration Registration { get; } = new(
        TypeName,
        "Example Bank (fake)",
        "A made-up bank for trying out the connector framework. Behaviour flags go after the username separated by '+': "
        + "code, choice, approve[N], image, loginfail, syncerror, refreshcode, statementcode, pending, dups, noid, statements. "
        + "Codes: 123456 (code), 4711 (image). Any PIN works except \"wrong\".",
        ctx => new FakeBankConnector(ctx));

    public string ConnectorType => TypeName;

    /// <summary>How many syncs this login has completed (kept in the session; drives pending -> booked).</summary>
    private int CompletedSyncs =>
        _session.Values.TryGetValue("syncs", out var s) && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;

    public Task<ConnectorStep<ConnectorSession?>> LoginAsync(
        ConnectorCredentials credentials, ConnectorSession? storedSession, ChallengeAnswer? answer, CancellationToken ct)
    {
        if (answer == null)
        {
            _settings = FakeBankSettings.Parse(credentials.Username);
            _session = storedSession ?? new ConnectorSession();
            _loginStages.Clear();
            _stage = 0;
            _polls = 0;
            if (_settings.Choice) _loginStages.Add("choice");
            if (_settings.Code) _loginStages.Add("code");
            if (_settings.Approval) _loginStages.Add("approval");
            if (_settings.Image) _loginStages.Add("image");

            if (_settings.LoginFail || string.Equals(credentials.Secret, FakeBankSettings.RejectedSecret, StringComparison.Ordinal))
                return Task.FromResult(ConnectorStep.Fail<ConnectorSession?>("Example Bank rejected the credentials.", credentialsRejected: true));
        }
        else
        {
            switch (_loginStages[_stage])
            {
                case "choice":
                    if (answer.Value is not ("app" or "sms"))
                        return Task.FromResult(ConnectorStep.Fail<ConnectorSession?>("Unknown TAN method."));
                    _stage++;
                    break;
                case "code":
                    if (answer.Value != CodeAnswer)
                        return Task.FromResult(ConnectorStep.Fail<ConnectorSession?>("The code was not accepted."));
                    _stage++;
                    break;
                case "image":
                    if (answer.Value != ImageAnswer)
                        return Task.FromResult(ConnectorStep.Fail<ConnectorSession?>("The image code was not accepted."));
                    _stage++;
                    break;
                default: // approval: the app only asks again
                    _polls++;
                    if (_polls >= _settings.ApprovalPolls) _stage++;
                    break;
            }
        }

        if (_stage < _loginStages.Count)
            return Task.FromResult(ConnectorStep.Ask<ConnectorSession?>(LoginChallenge(_loginStages[_stage])));
        return Task.FromResult(ConnectorStep.Done<ConnectorSession?>(_session));
    }

    private static ConnectorChallenge LoginChallenge(string stage) => stage switch
    {
        "choice" => ConnectorChallenge.Choice("Choose a TAN method.", [new ChallengeOption("app", "Example TAN app"), new ChallengeOption("sms", "Example SMS")], "fake-choice"),
        "code" => ConnectorChallenge.Code($"Enter the code {CodeAnswer} (fake bank).", 6, numeric: true, reference: "fake-code"),
        "image" => ConnectorChallenge.ImageCode($"Enter the code {ImageAnswer} (fake photoTAN).", TinyPng, "image/png", 4, numeric: true, reference: "fake-image"),
        _ => ConnectorChallenge.Approval("Approve the login in your Example Bank app (fake: completes by itself).", TimeSpan.FromMilliseconds(200), "fake-approval"),
    };

    public Task<IReadOnlyList<RemoteAccount>> ListAccountsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RemoteAccount>>(
        [
            new RemoteAccount(GiroId, "Example Giro", RemoteAccountType.Giro, GiroIban, "EUR"),
            new RemoteAccount(SavingsId, "Example Savings", RemoteAccountType.Savings, "DE00000000000000000002", "EUR"),
            new RemoteAccount(DepotId, "Example Depot", RemoteAccountType.Depot, null, "EUR"),
        ]);

    public Task<ConnectorStep<AccountRefresh>> RefreshAsync(RemoteAccount account, DateOnly? since, ChallengeAnswer? answer, CancellationToken ct)
    {
        _refreshed = true;
        var first = string.Equals(account.RemoteId, GiroId, StringComparison.Ordinal);
        if (first && _settings.RefreshCode && !_refreshCodeAsked)
        {
            _refreshCodeAsked = true;
            return Task.FromResult(ConnectorStep.Ask<AccountRefresh>(ConnectorChallenge.Code($"Enter the code {CodeAnswer} to read the account (fake bank).", 6, true, "fake-refresh")));
        }
        if (first && _settings.RefreshCode && answer != null && answer.Value != CodeAnswer)
            return Task.FromResult(ConnectorStep.Fail<AccountRefresh>("The code was not accepted."));
        if (first && _settings.SyncError)
            throw new ConnectorException("Example Bank is temporarily unavailable.");

        var refresh = account.RemoteId switch
        {
            GiroId => new AccountRefresh(GiroBalance, Filter(GiroTransactions(), since), []),
            SavingsId => new AccountRefresh(5000.00m, Filter(SavingsTransactions(), since), []),
            DepotId => DepotRefresh(),
            _ => throw new ConnectorException($"Unknown account {account.RemoteId}."),
        };
        return Task.FromResult(ConnectorStep.Done(refresh));
    }

    private static List<RemoteTransaction> Filter(IEnumerable<RemoteTransaction> all, DateOnly? since) =>
        all.Where(t => since == null || t.BookingDate >= since).ToList();

    private string? Id(string id) => _settings.NoIds ? null : id;

    private List<RemoteTransaction> GiroTransactions()
    {
        var list = new List<RemoteTransaction>();
        for (var m = 1; m <= 10; m++)
        {
            var ym = $"2026-{m:D2}";
            list.Add(new RemoteTransaction(2500.00m, "EUR", new DateOnly(2026, m, 1), new DateOnly(2026, m, 1), "Example Employer", $"Salary {ym}", true, Id($"G-{ym}-SAL")));
            list.Add(new RemoteTransaction(-800.00m, "EUR", new DateOnly(2026, m, 3), new DateOnly(2026, m, 3), "Example Landlord", $"Rent {ym}", true, Id($"G-{ym}-RENT")));
            list.Add(new RemoteTransaction(-(40m + m) - 0.67m, "EUR", new DateOnly(2026, m, 12), new DateOnly(2026, m, 12), "Example Grocery Store", $"Groceries {ym}", true, Id($"G-{ym}-FOOD")));
            list.Add(new RemoteTransaction(-19.99m, "EUR", new DateOnly(2026, m, 20), new DateOnly(2026, m, 20), "Example Streaming", $"Subscription {ym}", true, Id($"G-{ym}-STREAM")));
        }
        list.Add(new RemoteTransaction(-52.10m, "EUR", new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 2), "Example Grocery Store", "Groceries 2026-11", true, Id("G-2026-11-FOOD")));

        if (_settings.Duplicates)
        {
            list.Add(new RemoteTransaction(-3.50m, "EUR", new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 20), "Example Coffee Shop", "Coffee", true, Id("G-2026-10-COFFEE-A")));
            list.Add(new RemoteTransaction(-3.50m, "EUR", new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 20), "Example Coffee Shop", "Coffee", true, Id("G-2026-10-COFFEE-B")));
        }

        if (_settings.Pending)
        {
            var booked = CompletedSyncs >= 1;
            list.Add(new RemoteTransaction(-12.34m, "EUR", new DateOnly(2026, 11, 8), booked ? new DateOnly(2026, 11, 8) : null, "Example Pending Payee", "Online order", booked, booked ? Id("G-2026-11-PENDING") : null));
        }
        return list;
    }

    private IEnumerable<RemoteTransaction> SavingsTransactions()
    {
        for (var m = 1; m <= 10; m++)
        {
            var ym = $"2026-{m:D2}";
            yield return new RemoteTransaction(0.50m, "EUR", new DateOnly(2026, m, 28), new DateOnly(2026, m, 28), "Example Interest", $"Interest {ym}", true, Id($"S-{ym}-INT"));
        }
    }

    private static AccountRefresh DepotRefresh()
    {
        RemotePosition[] positions =
        [
            new("Example World ETF", "XX0000000001", "A0EXAM", "EXWL", 10.5m, 100.25m, "EUR", 1052.625m, 90m),
            new("Example Bond Fund", "XX0000000002", "A0EXBD", "EXBD", 4m, 50m, "EUR", 200m, 52m),
        ];
        return new AccountRefresh(positions.Sum(p => p.Value ?? 0), [], positions);
    }

    public Task<ConnectorStep<IReadOnlyList<RemoteStatement>>> FetchStatementsAsync(
        IReadOnlyList<RemoteAccount> accounts, IReadOnlySet<string> knownIdentifiers, ChallengeAnswer? answer, CancellationToken ct)
    {
        if (!_settings.Statements) return Task.FromResult(ConnectorStep.Done<IReadOnlyList<RemoteStatement>>([]));
        if (_settings.StatementCode && !_statementCodeAsked)
        {
            _statementCodeAsked = true;
            return Task.FromResult(ConnectorStep.Ask<IReadOnlyList<RemoteStatement>>(ConnectorChallenge.Code($"Enter the code {CodeAnswer} to download statements (fake bank).", 6, true, "fake-statements")));
        }
        if (_settings.StatementCode && answer != null && answer.Value != CodeAnswer)
            return Task.FromResult(ConnectorStep.Fail<IReadOnlyList<RemoteStatement>>("The code was not accepted."));

        var result = new List<RemoteStatement>();
        if (accounts.Any(a => string.Equals(a.RemoteId, GiroId, StringComparison.Ordinal)))
        {
            for (var m = 1; m <= 10; m++)
            {
                var ym = $"2026-{m:D2}";
                var identifier = $"FAKE-STMT-{ym}";
                if (knownIdentifiers.Contains(identifier)) continue;
                var date = new DateOnly(2026, m, DateTime.DaysInMonth(2026, m));
                result.Add(new RemoteStatement(identifier, GiroId, date, $"Statement {ym}", FakePdf.Create($"Example Bank statement {ym} (fake)")));
            }
        }
        if (!knownIdentifiers.Contains("FAKE-TAX-2025"))
            result.Add(new RemoteStatement("FAKE-TAX-2025", null, new DateOnly(2026, 1, 31), "Tax certificate 2025", FakePdf.Create("Example Bank tax certificate 2025 (fake)")));
        return Task.FromResult(ConnectorStep.Done<IReadOnlyList<RemoteStatement>>(result));
    }

    public Task<ConnectorSession?> LogoutAsync(CancellationToken ct)
    {
        // Only a session that actually read accounts counts as a completed sync (drives pending -> booked).
        if (_refreshed) _session.Values["syncs"] = (CompletedSyncs + 1).ToString(CultureInfo.InvariantCulture);
        _context.Logger.LogDebug("Fake bank logout.");
        return Task.FromResult<ConnectorSession?>(_session);
    }
}
