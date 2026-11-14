#pragma warning disable CA2000 // test harness: lifetimes are owned by BankWorld
using Microsoft.Data.Sqlite;
using Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBudget.Browser;
using OpenBudget.Connectors;
using OpenBudget.Connectors.FakeBank;
using OpenBudget.Connectors.Sync;
using OpenBudget.Importers.MoneyMoney;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Tests.Connectors;

/// <summary>Optional additions to a <see cref="BankWorld"/>: more connectors, a fake HTTP stack, a capturing logger.</summary>
internal sealed class BankWorldExtras
{
    public IReadOnlyList<ConnectorRegistration> Registrations { get; init; } = [];

    public Func<BankHttpClient>? HttpFactory { get; init; }

    public ILoggerFactory? Loggers { get; init; }

    /// <summary>A fake browser: when set, the runner gets a real <see cref="WebLoginService"/> over it.</summary>
    public IBrowserHost? BrowserHost { get; init; }

    public WebLoginOptions? WebLoginOptions { get; init; }
}

/// <summary>A clock that runs with real time plus an adjustable offset (so delays still work).</summary>
internal sealed class TestClock : TimeProvider
{
    public TimeSpan Offset { get; set; }

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;

    /// <summary>Sets "now" to a fixed instant (time still advances from there).</summary>
    public void Set(DateTime utc) => Offset = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)) - base.GetUtcNow();
}

/// <summary>
/// Users alice, bob and carol; alice has id space 2, bob id space 3. A coordinator,
/// secrets key, fake-bank registry and statement directory are wired over the shared in-memory database.
/// </summary>
internal sealed class BankWorld : IAsyncDisposable
{
    public const string Alice = "u-alice";
    public const string Bob = "u-bob";
    public const string Carol = "u-carol";
    public const int SourceAlice = 2;

    private readonly List<string> _files = [];
    private readonly BankWorldExtras _extras;

    private BankWorld(TestDb t, string? key, BankSyncOptions options, TestClock clock, BankWorldExtras? extras)
    {
        _extras = extras ?? new BankWorldExtras();
        T = t;
        Key = key;
        Options = options;
        Clock = clock;
        Secrets = new SecretsProtector(key);
        Registry = new ConnectorRegistry([FakeBankConnector.Registration, .. _extras.Registrations]);
        StatementsDir = Path.Combine(Path.GetTempPath(), $"ob-stmts-{Guid.NewGuid():N}");
        StatementStore = new BankStatementStore(new BankStorageOptions(StatementsDir));
        WebLogins = _extras.BrowserHost == null
            ? null
            : new WebLoginService(_extras.BrowserHost, _extras.WebLoginOptions ?? new WebLoginOptions { PollInterval = TimeSpan.FromMilliseconds(10), SettleDelay = TimeSpan.Zero },
                clock, _extras.Loggers ?? NullLoggerFactory.Instance);
        Runner = new BankSyncRunner(t.System, Registry, Secrets, new BankTransactionStore(), StatementStore, clock, _extras.Loggers ?? NullLoggerFactory.Instance, _extras.HttpFactory, WebLogins);
        Coordinator = new BankSyncCoordinator(t.System, Runner, Secrets, t.Notifier, options, clock, NullLogger<BankSyncCoordinator>.Instance);
    }

    public TestDb T { get; }
    public string? Key { get; }
    public BankSyncOptions Options { get; }
    public TestClock Clock { get; }
    public SecretsProtector Secrets { get; }
    public ConnectorRegistry Registry { get; }
    public string StatementsDir { get; }
    public BankStatementStore StatementStore { get; }
    public IWebLoginService? WebLogins { get; }
    public BankSyncRunner Runner { get; }
    public BankSyncCoordinator Coordinator { get; }

    public static async Task<BankWorld> CreateAsync(string? key = "generate", TimeSpan? challengeTimeout = null, BankWorldExtras? extras = null)
    {
        var t = new TestDb();
        t.AddUser(Alice, "alice");
        t.AddUser(Bob, "bob");
        t.AddUser(Carol, "carol");
        t.AddIdSpace(Alice, SourceAlice);
        t.AddIdSpace(Bob, 3);
        await t.Db.SaveChangesAsync();
        var options = new BankSyncOptions { ChallengeTimeout = challengeTimeout ?? TimeSpan.FromSeconds(30), DefaultPollInterval = TimeSpan.FromMilliseconds(20) };
        var clock = new TestClock();
        clock.Set(new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc));
        return new BankWorld(t, key == "generate" ? SecretsProtector.GenerateKey() : key, options, clock, extras);
    }

    /// <summary>A second protector over the same data but another key (simulates a rotated/wrong key).</summary>
    public BankSyncCoordinator CoordinatorWithKey(string key)
    {
        var secrets = new SecretsProtector(key);
        var runner = new BankSyncRunner(T.System, Registry, secrets, new BankTransactionStore(), StatementStore, Clock, _extras.Loggers ?? NullLoggerFactory.Instance, _extras.HttpFactory, WebLogins);
        return new BankSyncCoordinator(T.System, runner, secrets, T.Notifier, Options, Clock, NullLogger<BankSyncCoordinator>.Instance);
    }

    public BankLoginService For(string? userId)
    {
        var access = new AccessScopeProvider(T.System, new FixedCurrentUser(userId), T.Notifier);
        var factory = new ScopedDbContextFactory(T.System, access);
        var origin = new ChangeOrigin();
        return new BankLoginService(factory, access, new AccountService(factory),
            Coordinator, Registry, Secrets, StatementStore, T.Notifier, origin);
    }

    /// <summary>The account switch service as <paramref name="userId"/> sees it.</summary>
    public AccountSwitchService Switcher(string? userId)
    {
        var access = new AccessScopeProvider(T.System, new FixedCurrentUser(userId), T.Notifier);
        var factory = new ScopedDbContextFactory(T.System, access);
        var origin = new ChangeOrigin();
        return new AccountSwitchService(factory, access, T.Notifier, origin);
    }

    /// <summary>The app's service graph for a user (reads/writes through global filters and write guard).</summary>
    public UserView View(string? userId) => T.ForUser(userId);

    public async Task<int> AddLoginAsync(string user, string username = "demo", RefreshInterval interval = RefreshInterval.EveryTwoHours)
    {
        var view = await For(user).CreateLoginAsync(FakeBankConnector.TypeName, "Fake", username, "1234", interval);
        return view.Id;
    }

    /// <summary>Runs a sync of <paramref name="loginId"/> for its owner, answering challenges with <paramref name="answers"/> (in order).</summary>
    public async Task<BankSyncStatus> SyncAsync(string user, int loginId, SyncRequest? request = null, params string[] answers)
    {
        var start = await Coordinator.StartAsync(user, request ?? new SyncRequest(loginId, true, true));
        Assert.Equal(StartResult.Started, start);
        return await WaitAsync(user, loginId, answers);
    }

    public async Task<BankSyncStatus> WaitAsync(string user, int loginId, string[] answers)
    {
        var next = 0;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var s = Coordinator.GetStatus(user, loginId);
            if (s is { Phase: SyncPhase.Finished }) return s;
            if (s is { Phase: SyncPhase.WaitingForInput } && next < answers.Length && Coordinator.SubmitAnswer(user, loginId, s.ChallengeId, answers[next]))
                next++;
            await Task.Delay(10);
        }
        throw new TimeoutException("sync did not finish");
    }

    public async Task<int> LinkAllAsync(string user, int loginId, long? existingForGiro = null)
    {
        var svc = For(user);
        var accounts = await svc.GetAccountsAsync(loginId);
        foreach (var a in accounts)
        {
            if (a.RemoteId == FakeBankConnector.GiroId && existingForGiro is { } id)
            {
                await svc.LinkAsync(a.Id, id);
            }
            else
            {
                await svc.CreateAccountAsync(a.Id);
            }
        }
        return accounts.Count;
    }

    public async Task<BankLoginAccountView> RemoteAsync(string user, int loginId, string remoteId) =>
        (await For(user).GetAccountsAsync(loginId)).Single(a => a.RemoteId == remoteId);

    /// <summary>Changes the stored connector session values of <paramref name="loginId"/> (simulates a session saved by an older version).</summary>
    public async Task EditSessionAsync(int loginId, Action<Dictionary<string, string>> edit)
    {
        await using var db = await T.System.CreateDbContextAsync();
        var login = await db.BankLogins.FirstAsync(l => l.Id == loginId);
        Assert.True(Secrets.TryDecrypt(login.EncryptedSession, BankSyncRunner.SessionPurpose, out var json));
        var session = System.Text.Json.JsonSerializer.Deserialize<ConnectorSession>(json)!;
        edit(session.Values);
        login.EncryptedSession = Secrets.Encrypt(System.Text.Json.JsonSerializer.Serialize(session), BankSyncRunner.SessionPurpose);
        await db.SaveChangesAsync();
    }

    public async Task<List<Transaction>> TransactionsAsync(long accountId)
    {
        await using var db = await T.System.CreateDbContextAsync();
        return await db.Transactions.AsNoTracking().Where(t => t.AccountId == accountId).OrderBy(t => t.Timestamp).ThenBy(t => t.Id).ToListAsync();
    }

    /// <summary>Hands the account to <paramref name="provider"/> directly (as linking or a portfolio would).</summary>
    public async Task SetProviderAsync(long id, string provider)
    {
        await using var db = await T.System.CreateDbContextAsync();
        await db.Accounts.Where(a => a.Id == id).ExecuteUpdateAsync(u => u.SetProperty(a => a.Provider, provider));
    }

    public async Task<Account> AccountAsync(long id)
    {
        await using var db = await T.System.CreateDbContextAsync();
        return await db.Accounts.AsNoTracking().FirstAsync(a => a.Id == id);
    }

    // ---- MoneyMoney snapshots ----

    public sealed record MmTx(int Account, double Amount, DateTime Date, string Name, string Purpose, int Booked = 1);

    public string BuildSnapshot(IEnumerable<(string Name, string? Iban)> accounts, IEnumerable<MmTx> txs, IReadOnlyCollection<string>? depots = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ob-mm-{Guid.NewGuid():N}.sqlite");
        _files.Add(path);
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE accounts (name TEXT, iban TEXT, currency TEXT, type INTEGER, portfolio INTEGER, balances TEXT, sort INTEGER, refresh_tsp INTEGER, error TEXT, group_key INTEGER);
                CREATE TABLE account_groups (name TEXT, group_key INTEGER, sort INTEGER);
                CREATE TABLE category_groups (name TEXT, group_key INTEGER, sort INTEGER, color TEXT);
                CREATE TABLE categories (name TEXT, type INTEGER, color TEXT, rules TEXT, group_key INTEGER, sort INTEGER);
                CREATE TABLE transactions (local_account_key INTEGER, amount REAL, currency TEXT, timestamp INTEGER, name TEXT, comment TEXT, unformatted_purpose TEXT, category_key INTEGER, checkmark INTEGER, splitted INTEGER, booked INTEGER);
                INSERT INTO categories (name, type) VALUES ('', 1);
                """;
            cmd.ExecuteNonQuery();
        }
        foreach (var (name, iban) in accounts)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO accounts (name, iban, currency, type, portfolio, balances, refresh_tsp) VALUES ($n, $i, 'EUR', $t, $p, '11:10.000000,3:EUR,', 1700000000)";
            bool depot = depots?.Contains(name, StringComparer.Ordinal) == true;
            cmd.Parameters.AddWithValue("$t", depot ? 7 : 1);
            cmd.Parameters.AddWithValue("$p", depot ? 1 : 0);
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$i", (object?)iban ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        foreach (var tx in txs)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO transactions (local_account_key, amount, currency, timestamp, name, unformatted_purpose, category_key, checkmark, splitted, booked)
                VALUES ($a, $amt, 'EUR', $ts, $n, $p, 1, 0, 0, $b)
                """;
            cmd.Parameters.AddWithValue("$a", tx.Account);
            cmd.Parameters.AddWithValue("$amt", tx.Amount);
            cmd.Parameters.AddWithValue("$ts", new DateTimeOffset(DateTime.SpecifyKind(tx.Date, DateTimeKind.Utc)).ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$n", tx.Name);
            cmd.Parameters.AddWithValue("$p", tx.Purpose);
            cmd.Parameters.AddWithValue("$b", tx.Booked);
            cmd.ExecuteNonQuery();
        }
        return path;
    }

    public Task<ImportResult> ImportMoneyMoneyAsync(string snapshot) =>
        new MoneyMoneyImportService(T.Factory, T.Notifier, NullLogger<MoneyMoneyImportService>.Instance)
            .ImportFromFileAsync(Alice, snapshot, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        // let running sync jobs finish before the database they write to is closed
        await Coordinator.StopAsync(CancellationToken.None);
        Coordinator.Dispose();
        await T.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var f in _files)
        {
            try { File.Delete(f); } catch { /* best-effort */ }
        }
        try { Directory.Delete(StatementsDir, recursive: true); } catch { /* best-effort */ }
    }
}
