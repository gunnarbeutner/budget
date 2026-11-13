#pragma warning disable CA2000 // test objects live for the test
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBudget.Connectors;
using OpenBudget.Connectors.FakeBank;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>Background runs through the runner and coordinator, and the scheduler's rules.</summary>
public sealed class BackgroundSyncTests
{
    private static SyncRequest Background(int id) => new(id, Transactions: true, Statements: false, Background: true);

    private static async Task<BankSyncStatus> RunBackgroundAsync(BankWorld w, int id)
    {
        Assert.Equal(StartResult.Started, w.Coordinator.LaunchBackground(BankWorld.Alice, Background(id), null));
        await w.Coordinator.WhenIdleAsync(id);
        return w.Coordinator.GetStatus(BankWorld.Alice, id)!;
    }

    private static async Task<BankLogin> StoredAsync(BankWorld w, int id)
    {
        await using var db = await w.T.System.CreateDbContextAsync();
        return await db.BankLogins.AsNoTracking().SingleAsync(l => l.Id == id);
    }

    private static async Task<List<BackgroundSyncRun>> RunsAsync(BankWorld w, int id)
    {
        await using var db = await w.T.System.CreateDbContextAsync();
        return await db.BackgroundSyncRuns.AsNoTracking().Where(r => r.BankLoginId == id).OrderBy(r => r.Id).ToListAsync();
    }

    [Theory]
    [InlineData("demo+code")]
    [InlineData("demo+approve")]
    [InlineData("demo+choice")]
    [InlineData("demo+refreshcode")]
    public async Task A_step_that_needs_the_owner_marks_the_login_and_leaves_its_schedule_alone(string username)
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice, username);
        if (username.EndsWith("refreshcode", StringComparison.Ordinal))
        {
            await w.SyncAsync(BankWorld.Alice, id, null, "123456");
            await w.LinkAllAsync(BankWorld.Alice, id);
        }
        var before = await StoredAsync(w, id);

        var status = await RunBackgroundAsync(w, id);

        Assert.True(status.Background);
        Assert.True(status.NeedsUser);
        Assert.Null(status.Challenge);
        var login = await StoredAsync(w, id);
        Assert.NotNull(login.NeedsManualSync);
        Assert.NotNull(login.NeedsManualSince);
        Assert.Equal(before.LastSyncAt, login.LastSyncAt);
        Assert.Equal(before.EncryptedSession, login.EncryptedSession);
        Assert.Equal(SyncRunResult.NeedsUser, (await RunsAsync(w, id)).Last().Result);
        Assert.True(await w.For(BankWorld.Alice).AnyNeedsManualSyncAsync() == login.BackgroundEnabled);
    }

    [Fact]
    public async Task A_manual_sync_clears_the_flag_and_a_remembered_session_then_syncs_in_the_background()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice, "demo+session");
        await w.For(BankWorld.Alice).SetBackgroundAsync(id, true);

        Assert.True((await RunBackgroundAsync(w, id)).NeedsUser);
        Assert.True(await w.For(BankWorld.Alice).AnyNeedsManualSyncAsync());

        Assert.True((await w.SyncAsync(BankWorld.Alice, id, null, FakeBankConnector.CodeAnswer)).Succeeded);
        var cleared = await StoredAsync(w, id);
        Assert.Null(cleared.NeedsManualSync);
        Assert.False(await w.For(BankWorld.Alice).AnyNeedsManualSyncAsync());

        w.Clock.Offset += TimeSpan.FromHours(7);
        var status = await RunBackgroundAsync(w, id);
        Assert.True(status.Succeeded, status.Error);
        var login = await StoredAsync(w, id);
        Assert.NotNull(login.LastBackgroundSyncAt);
        Assert.Equal(login.LastBackgroundSyncAt!.Value, login.LastSyncAt!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(SyncRunResult.Done, (await RunsAsync(w, id)).Last().Result);
    }

    [Fact]
    public async Task Rejected_credentials_switch_background_syncs_off_without_retrying()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice, "demo+loginfail");
        await w.For(BankWorld.Alice).SetBackgroundAsync(id, true);

        var status = await RunBackgroundAsync(w, id);

        Assert.False(status.Succeeded);
        var login = await StoredAsync(w, id);
        Assert.True(login.CredentialsInvalid);
        Assert.False(login.BackgroundEnabled);
        Assert.Equal(SyncRunResult.Failed, (await RunsAsync(w, id)).Single().Result);
    }

    [Fact]
    public async Task A_failed_run_backs_off_one_hour_then_doubling_up_to_a_day()
    {
        Assert.Equal(TimeSpan.FromHours(1), BankSyncRunner.BackgroundBackoff(1));
        Assert.Equal(TimeSpan.FromHours(2), BankSyncRunner.BackgroundBackoff(2));
        Assert.Equal(TimeSpan.FromHours(16), BankSyncRunner.BackgroundBackoff(5));
        Assert.Equal(TimeSpan.FromHours(24), BankSyncRunner.BackgroundBackoff(9));

        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice, "demo+syncerror");
        await w.SyncAsync(BankWorld.Alice, id);
        await w.LinkAllAsync(BankWorld.Alice, id);
        await RunBackgroundAsync(w, id);
        var login = await StoredAsync(w, id);
        Assert.Equal(1, login.BackgroundFailures);
        Assert.Equal(w.Clock.GetUtcNow().UtcDateTime.AddHours(1), login.NextBackgroundAttemptAt!.Value, TimeSpan.FromSeconds(5));
    }

    // ---- scheduler ----

    private static BankLogin Due(RefreshInterval interval, DateTime? last) => new() { Id = 7, RefreshInterval = interval, LastSyncAt = last };

    [Fact]
    public void Due_needs_at_least_six_hours_with_jitter_and_respects_the_backoff()
    {
        var options = new BackgroundSyncOptions();
        var now = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(BackgroundSyncService.IsDue(Due(RefreshInterval.EveryTwoHours, null), now, options));
        Assert.False(BackgroundSyncService.IsDue(Due(RefreshInterval.EveryTwoHours, now.AddHours(-5.9)), now, options));
        Assert.True(BackgroundSyncService.IsDue(Due(RefreshInterval.EveryTwoHours, now.AddHours(-6.95)), now, options));
        Assert.False(BackgroundSyncService.IsDue(Due(RefreshInterval.Daily, now.AddHours(-20)), now, options));
        Assert.True(BackgroundSyncService.IsDue(Due(RefreshInterval.Daily, now.AddHours(-28)), now, options));
        Assert.False(BackgroundSyncService.IsDue(Due(RefreshInterval.ManualOnly, null), now, options));
        var backingOff = Due(RefreshInterval.EveryTwoHours, null);
        backingOff.NextBackgroundAttemptAt = now.AddMinutes(10);
        Assert.False(BackgroundSyncService.IsDue(backingOff, now, options));
    }

    [Fact]
    public void Jitter_stays_within_fifteen_percent_and_is_stable_per_login_and_sync()
    {
        var last = new DateTime(2026, 11, 9, 3, 0, 0, DateTimeKind.Utc);
        var factors = Enumerable.Range(1, 200).Select(i => BackgroundSyncService.JitterFactor(i, last)).ToList();
        Assert.All(factors, f => Assert.InRange(f, -1.0, 1.0));
        Assert.True(factors.Distinct().Count() > 150);
        Assert.Equal(BackgroundSyncService.JitterFactor(3, last), BackgroundSyncService.JitterFactor(3, last));
    }

    [Fact]
    public void Quiet_hours_are_midnight_to_six_by_default()
    {
        var options = new BackgroundSyncOptions();
        Assert.True(BackgroundSyncService.InQuietHours(new TimeOnly(0, 30), options));
        Assert.True(BackgroundSyncService.InQuietHours(new TimeOnly(5, 59), options));
        Assert.False(BackgroundSyncService.InQuietHours(new TimeOnly(6, 0), options));
        Assert.False(BackgroundSyncService.InQuietHours(new TimeOnly(23, 0), options));
        Assert.False(BackgroundSyncService.InQuietHours(new TimeOnly(3, 0), new BackgroundSyncOptions { QuietFrom = new(1, 0), QuietUntil = new(1, 0) }));
    }

    private static BackgroundSyncService Scheduler(BankWorld w, BackgroundSyncOptions? options = null) =>
        new(w.T.System, w.Coordinator, w.Registry, options ?? new BackgroundSyncOptions { QuietFrom = new(0, 0), QuietUntil = new(0, 0) },
            w.Options, w.Clock, NullLogger<BackgroundSyncService>.Instance);

    [Fact]
    public async Task A_tick_starts_only_switched_on_due_logins_and_at_most_as_many_as_may_run()
    {
        await using var w = await BankWorld.CreateAsync();
        w.Options.BackgroundConcurrency = 1;
        var off = await w.AddLoginAsync(BankWorld.Alice);
        var first = await w.AddLoginAsync(BankWorld.Alice);
        var second = await w.AddLoginAsync(BankWorld.Alice);
        var flagged = await w.AddLoginAsync(BankWorld.Alice);
        foreach (var id in new[] { first, second, flagged }) await w.For(BankWorld.Alice).SetBackgroundAsync(id, true);
        await using (var db = await w.T.System.CreateDbContextAsync())
        {
            await db.BankLogins.Where(l => l.Id == flagged).ExecuteUpdateAsync(s => s.SetProperty(l => l.NeedsManualSync, "TAN"));
        }

        Assert.Equal(1, await Scheduler(w).TickAsync(default));
        await w.Coordinator.WhenIdleAsync(first);
        Assert.Equal(1, await Scheduler(w).TickAsync(default));
        await w.Coordinator.WhenIdleAsync(second);
        Assert.Equal(0, await Scheduler(w).TickAsync(default));
        Assert.Null(w.Coordinator.GetStatus(BankWorld.Alice, off));
        Assert.Null(w.Coordinator.GetStatus(BankWorld.Alice, flagged));
        Assert.NotNull((await StoredAsync(w, first)).LastBackgroundSyncAt);
    }

    [Fact]
    public async Task No_runs_in_quiet_hours()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice);
        await w.For(BankWorld.Alice).SetBackgroundAsync(id, true);
        var always = new BackgroundSyncOptions { QuietFrom = new(0, 0), QuietUntil = new(23, 59, 59, 999) };
        Assert.Equal(0, await Scheduler(w, always).TickAsync(default));
    }

    [Fact]
    public async Task Fints_banks_run_in_the_background_only_once_switched_on_for_the_bank()
    {
        var registration = FinTs.FinTsTestRegistration.Create();
        await using var w = await BankWorld.CreateAsync(extras: new BankWorldExtras { Registrations = [registration] });
        var view = await w.For(BankWorld.Alice).CreateLoginAsync(registration.ConnectorType, "Bank", "user", "pin");
        await w.For(BankWorld.Alice).SetBackgroundAsync(view.Id, true);
        Assert.False((await w.For(BankWorld.Alice).GetLoginAsync(view.Id))!.FinTsBankAllowed);
        Assert.Equal(0, await Scheduler(w).TickAsync(default));

        // only an admin may switch a bank on
        await Assert.ThrowsAsync<OpenBudget.Services.AccessDeniedException>(() => w.For(BankWorld.Alice).SetFinTsBankAsync(registration.FinTsBlz!, true));
        await using (var db = await w.T.System.CreateDbContextAsync())
        {
            db.FinTsBankSettings.Add(new FinTsBankSetting { Blz = registration.FinTsBlz!, BackgroundEnabled = true });
            await db.SaveChangesAsync();
        }
        Assert.True((await w.For(BankWorld.Alice).GetLoginAsync(view.Id))!.FinTsBankAllowed);
        Assert.Equal(1, await Scheduler(w).TickAsync(default));
        await w.Coordinator.WhenIdleAsync(view.Id);
        // the gate stops before the first dialog: the login was never set up by a manual sync
        Assert.True(w.Coordinator.GetStatus(BankWorld.Alice, view.Id)!.NeedsUser);
    }

    // ---- interactive and background runs of one login ----

    /// <summary>A bank whose login waits until the test lets it go (keeps a background slot busy).</summary>
    private sealed class GatedConnector(TaskCompletionSource gate) : IBankConnector
    {
        public string ConnectorType => "gated";

        public async Task<ConnectorStep<ConnectorSession?>> LoginAsync(ConnectorCredentials credentials, ConnectorSession? storedSession, ChallengeAnswer? answer, CancellationToken ct)
        {
            await gate.Task.WaitAsync(ct);
            return ConnectorStep.Done<ConnectorSession?>(new ConnectorSession());
        }

        public Task<IReadOnlyList<RemoteAccount>> ListAccountsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RemoteAccount>>([]);

        public Task<ConnectorStep<AccountRefresh>> RefreshAsync(RemoteAccount account, DateOnly? since, ChallengeAnswer? answer, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ConnectorStep<IReadOnlyList<RemoteStatement>>> FetchStatementsAsync(
            IReadOnlyList<RemoteAccount> accounts, IReadOnlySet<string> knownIdentifiers, ChallengeAnswer? answer, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ConnectorSession?> LogoutAsync(CancellationToken ct) => Task.FromResult<ConnectorSession?>(null);
    }

    [Fact]
    public async Task Sync_now_replaces_a_background_run_that_only_waits_and_reports_one_that_runs()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = new ConnectorRegistration("gated", "Gated", "test", _ => new GatedConnector(gate)) { Silent = SilentCapability.Full };
        await using var w = await BankWorld.CreateAsync(extras: new BankWorldExtras { Registrations = [registration] });
        w.Options.BackgroundConcurrency = 1;
        var busy = (await w.For(BankWorld.Alice).CreateLoginAsync("gated", "Busy", "u", "p")).Id;
        var waiting = await w.AddLoginAsync(BankWorld.Alice, "demo+code");

        Assert.Equal(StartResult.Started, w.Coordinator.LaunchBackground(BankWorld.Alice, Background(busy), null));
        Assert.Equal(StartResult.Started, w.Coordinator.LaunchBackground(BankWorld.Alice, Background(waiting), null));
        await Task.Delay(100);

        // the running one stays and is reported as running
        Assert.Equal(StartResult.AlreadyRunning, await w.Coordinator.SyncNowAsync(BankWorld.Alice, busy));
        // the waiting one gives way: the owner's sync asks its code and finishes
        Assert.Equal(StartResult.Started, await w.Coordinator.SyncNowAsync(BankWorld.Alice, waiting));
        var status = await w.WaitAsync(BankWorld.Alice, waiting, [FakeBankConnector.CodeAnswer]);
        Assert.True(status.Succeeded, status.Error);
        Assert.False(status.Background);
        Assert.Null((await StoredAsync(w, waiting)).NeedsManualSync);

        gate.SetResult();
        await w.Coordinator.WhenIdleAsync(busy);
        Assert.True(w.Coordinator.GetStatus(BankWorld.Alice, busy)!.Succeeded);
    }

    [Fact]
    public async Task Background_runs_never_accept_an_answer()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice, "demo+code");
        var status = await RunBackgroundAsync(w, id);
        Assert.False(w.Coordinator.SubmitAnswer(BankWorld.Alice, id, status.ChallengeId, FakeBankConnector.CodeAnswer));
    }
}
