#pragma warning disable CA2000 // test objects live for the test
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors;
using OpenBudget.Connectors.FakeBank;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>Secrets handling, owner-only access, challenge kinds and the scheduling entry points.</summary>
public sealed class BankSyncTests
{
    // ---- secrets and the missing / wrong key ----

    [Fact]
    public async Task Credentials_are_stored_encrypted_and_never_exposed_by_the_view()
    {
        await using var w = await BankWorld.CreateAsync();
        var view = await w.For(BankWorld.Alice).CreateLoginAsync("fake", "My fake", "alice-user", "pin-98765");

        await using var db = await w.T.System.CreateDbContextAsync();
        var row = await db.BankLogins.AsNoTracking().SingleAsync();
        Assert.StartsWith("v1:", row.EncryptedCredentials, StringComparison.Ordinal);
        Assert.DoesNotContain("alice-user", row.EncryptedCredentials, StringComparison.Ordinal);
        Assert.DoesNotContain("pin-98765", row.EncryptedCredentials, StringComparison.Ordinal);
        Assert.Equal("alice-user", view.Username);
        Assert.DoesNotContain("pin-98765", view.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_key_disables_bank_logins_gracefully()
    {
        await using var w = await BankWorld.CreateAsync(key: null);
        var svc = w.For(BankWorld.Alice);
        Assert.False(svc.IsEnabled);
        Assert.Contains("OPENBUDGET_SECRETS_KEY", svc.DisabledReason, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateLoginAsync("fake", "x", "u", "p"));
        Assert.Empty(await svc.GetLoginsAsync());
        Assert.Equal(StartResult.Disabled, await w.Coordinator.StartAsync(BankWorld.Alice, new SyncRequest(1, true, true)));
        Assert.Equal(0, await svc.StartDueAsync());
        Assert.Equal(0, await svc.SyncAllAsync());
    }

    [Fact]
    public async Task Wrong_key_asks_for_credentials_again_instead_of_crashing()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice);

        var other = w.CoordinatorWithKey(SecretsProtector.GenerateKey());
        Assert.Equal(StartResult.Started, await other.StartAsync(BankWorld.Alice, new SyncRequest(id, true, false)));
        await other.WhenIdleAsync(id);
        var status = other.GetStatus(BankWorld.Alice, id)!;
        Assert.False(status.Succeeded);
        Assert.Contains("re-enter", status.Error, StringComparison.OrdinalIgnoreCase);

        var login = await w.For(BankWorld.Alice).GetLoginAsync(id);
        Assert.True(login!.CredentialsInvalid);
        // No automatic retries while the credentials are flagged.
        Assert.Equal(0, await w.Coordinator.StartDueAsync(BankWorld.Alice));

        await w.For(BankWorld.Alice).UpdateLoginAsync(id, "Fake", RefreshInterval.EveryTwoHours, "demo", "1234");
        Assert.False((await w.For(BankWorld.Alice).GetLoginAsync(id))!.CredentialsInvalid);
        Assert.True((await w.SyncAsync(BankWorld.Alice, id)).Succeeded);
        other.Dispose();
    }

    [Fact]
    public async Task Rejected_credentials_flag_the_login_and_stop_automatic_syncs()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice, "demo+loginfail");
        var status = await w.SyncAsync(BankWorld.Alice, id);
        Assert.False(status.Succeeded);
        Assert.Contains("rejected", status.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True((await w.For(BankWorld.Alice).GetLoginAsync(id))!.CredentialsInvalid);
        Assert.Equal(0, await w.Coordinator.StartDueAsync(BankWorld.Alice));
    }

    // ---- owner-only ----

    [Fact]
    public async Task Other_users_cannot_see_edit_sync_or_delete_a_login()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice);
        await w.SyncAsync(BankWorld.Alice, id);
        var bob = w.For(BankWorld.Bob);

        Assert.Empty(await bob.GetLoginsAsync());
        Assert.Null(await bob.GetLoginAsync(id));
        Assert.Empty(await bob.GetAccountsAsync(id));
        Assert.Empty(await bob.GetLoginStatementsAsync(id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.UpdateLoginAsync(id, "hacked", RefreshInterval.Never));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.DeleteLoginAsync(id));
        Assert.Equal(StartResult.NotFound, await bob.SyncNowAsync(id));
        Assert.Equal(StartResult.NotFound, await w.Coordinator.StartAsync(BankWorld.Bob, new SyncRequest(id, true, true)));
        Assert.Equal(StartResult.NotFound, await w.Coordinator.DownloadStatementsAsync(BankWorld.Bob, id, 1));
        Assert.Equal(0, await bob.SyncAllAsync());

        var remote = await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId);
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.CreateAccountAsync(remote.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.UnlinkAsync(remote.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.SetStatementsAsync(remote.Id, false, StatementsInterval.Monthly));

        // Still intact for alice.
        Assert.Equal("Fake", (await w.For(BankWorld.Alice).GetLoginAsync(id))!.DisplayName);
    }

    [Fact]
    public async Task Filters_and_write_guard_hide_and_protect_logins_at_the_context_level()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice);
        await w.SyncAsync(BankWorld.Alice, id);

        await using var bobDb = await w.View(BankWorld.Bob).Factory.CreateDbContextAsync();
        Assert.Empty(await bobDb.BankLogins.ToListAsync());
        Assert.Empty(await bobDb.BankLoginAccounts.ToListAsync());

        bobDb.BankLogins.Add(new BankLogin { OwnerUserId = BankWorld.Alice, ConnectorType = "fake", DisplayName = "planted" });
        await Assert.ThrowsAsync<AccessDeniedException>(() => bobDb.SaveChangesAsync());

        await using var aliceDb = await w.View(BankWorld.Alice).Factory.CreateDbContextAsync();
        Assert.Single(await aliceDb.BankLogins.ToListAsync());
        Assert.Equal(3, await aliceDb.BankLoginAccounts.CountAsync());
    }

    [Fact]
    public async Task Challenges_are_only_visible_to_and_answerable_by_the_owner()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice, "demo+code");
        Assert.Equal(StartResult.Started, await w.Coordinator.StartAsync(BankWorld.Alice, new SyncRequest(id, true, false)));
        var status = await WaitForPhaseAsync(w, BankWorld.Alice, id, SyncPhase.WaitingForInput);

        Assert.Empty(w.Coordinator.GetStatuses(BankWorld.Bob));
        Assert.Null(w.Coordinator.GetStatus(BankWorld.Bob, id));
        Assert.False(w.Coordinator.SubmitAnswer(BankWorld.Bob, id, status.ChallengeId, FakeBankConnector.CodeAnswer));
        Assert.False(await w.For(BankWorld.Bob).SubmitAnswerAsync(id, status.ChallengeId, FakeBankConnector.CodeAnswer));
        Assert.False(await w.For(BankWorld.Bob).CancelSyncAsync(id));
        Assert.Empty(await w.For(BankWorld.Bob).GetStatusesAsync());
        Assert.Single(await w.For(BankWorld.Alice).GetStatusesAsync());
        Assert.Equal(SyncPhase.WaitingForInput, w.Coordinator.GetStatus(BankWorld.Alice, id)!.Phase);

        // Notifications about the sync are scoped to the owner's sessions.
        var change = new DataChange(DataArea.BankSync, UserId: BankWorld.Alice);
        Assert.True((await w.View(BankWorld.Alice).Access.GetAsync()).IsChangeVisible(change));
        Assert.False((await w.View(BankWorld.Bob).Access.GetAsync()).IsChangeVisible(change));

        Assert.True(await w.For(BankWorld.Alice).SubmitAnswerAsync(id, status.ChallengeId, FakeBankConnector.CodeAnswer));
        await w.Coordinator.WhenIdleAsync(id);
        Assert.True(w.Coordinator.GetStatus(BankWorld.Alice, id)!.Succeeded);
    }

    private static async Task<BankSyncStatus> WaitForPhaseAsync(BankWorld w, string user, int id, SyncPhase phase)
    {
        for (var i = 0; i < 1000; i++)
        {
            var s = w.Coordinator.GetStatus(user, id);
            if (s?.Phase == phase) return s;
            await Task.Delay(10);
        }
        throw new TimeoutException($"never reached {phase}");
    }

    // ---- challenge kinds ----

    [Fact]
    public async Task Code_challenge_end_to_end_and_wrong_code_fails()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice, "demo+code");
        Assert.Equal(StartResult.Started, await w.Coordinator.StartAsync(BankWorld.Alice, new SyncRequest(id, true, false)));
        var s = await WaitForPhaseAsync(w, BankWorld.Alice, id, SyncPhase.WaitingForInput);
        Assert.Equal(ChallengeKind.Code, s.Challenge!.Kind);
        Assert.Equal(6, s.Challenge.Length);
        Assert.True(s.Challenge.Numeric);
        Assert.False(w.Coordinator.SubmitAnswer(BankWorld.Alice, id, Guid.NewGuid(), "x")); // stale challenge id
        Assert.False(w.Coordinator.SubmitAnswer(BankWorld.Alice, id, s.ChallengeId, " ")); // empty
        Assert.True(w.Coordinator.SubmitAnswer(BankWorld.Alice, id, s.ChallengeId, FakeBankConnector.CodeAnswer));
        await w.Coordinator.WhenIdleAsync(id);
        Assert.True(w.Coordinator.GetStatus(BankWorld.Alice, id)!.Succeeded);
        Assert.Equal(3, (await w.For(BankWorld.Alice).GetAccountsAsync(id)).Count);

        var bad = await w.SyncAsync(BankWorld.Alice, id, null, "000000");
        Assert.False(bad.Succeeded);
        Assert.Contains("not accepted", bad.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Method_choice_is_validated_and_chains_into_the_next_step()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice, "demo+choice+code");
        Assert.Equal(StartResult.Started, await w.Coordinator.StartAsync(BankWorld.Alice, new SyncRequest(id, true, false)));
        var choice = await WaitForPhaseAsync(w, BankWorld.Alice, id, SyncPhase.WaitingForInput);
        Assert.Equal(ChallengeKind.Choice, choice.Challenge!.Kind);
        Assert.Equal(["app", "sms"], choice.Challenge.Options.Select(o => o.Id).ToArray());
        Assert.False(w.Coordinator.SubmitAnswer(BankWorld.Alice, id, choice.ChallengeId, "carrier-pigeon"));
        Assert.True(w.Coordinator.SubmitAnswer(BankWorld.Alice, id, choice.ChallengeId, "sms"));

        for (var i = 0; i < 500 && w.Coordinator.GetStatus(BankWorld.Alice, id)!.Challenge?.Kind != ChallengeKind.Code; i++) await Task.Delay(10);
        var code = w.Coordinator.GetStatus(BankWorld.Alice, id)!;
        Assert.Equal(ChallengeKind.Code, code.Challenge!.Kind);
        Assert.NotEqual(choice.ChallengeId, code.ChallengeId);
        Assert.True(w.Coordinator.SubmitAnswer(BankWorld.Alice, id, code.ChallengeId, FakeBankConnector.CodeAnswer));
        await w.Coordinator.WhenIdleAsync(id);
        Assert.True(w.Coordinator.GetStatus(BankWorld.Alice, id)!.Succeeded);
    }

    [Fact]
    public async Task Image_challenge_carries_the_picture()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice, "demo+image");
        Assert.Equal(StartResult.Started, await w.Coordinator.StartAsync(BankWorld.Alice, new SyncRequest(id, true, false)));
        var s = await WaitForPhaseAsync(w, BankWorld.Alice, id, SyncPhase.WaitingForInput);
        Assert.Equal(ChallengeKind.Image, s.Challenge!.Kind);
        Assert.Equal("image/png", s.Challenge.ImageContentType);
        Assert.Equal(0x89, s.Challenge.Image![0]);
        Assert.True(w.Coordinator.SubmitAnswer(BankWorld.Alice, id, s.ChallengeId, FakeBankConnector.ImageAnswer));
        await w.Coordinator.WhenIdleAsync(id);
        Assert.True(w.Coordinator.GetStatus(BankWorld.Alice, id)!.Succeeded);
    }

    [Fact]
    public async Task Decoupled_approval_is_polled_until_the_bank_reports_done()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice, "demo+approve3");
        Assert.Equal(StartResult.Started, await w.Coordinator.StartAsync(BankWorld.Alice, new SyncRequest(id, true, false)));
        var s = await WaitForPhaseAsync(w, BankWorld.Alice, id, SyncPhase.WaitingForApproval);
        Assert.Equal(ChallengeKind.Approval, s.Challenge!.Kind);
        // Nothing to answer for an approval.
        Assert.False(w.Coordinator.SubmitAnswer(BankWorld.Alice, id, s.ChallengeId, "x"));
        await w.Coordinator.WhenIdleAsync(id);
        var done = w.Coordinator.GetStatus(BankWorld.Alice, id)!;
        Assert.True(done.Succeeded);
        Assert.Null(done.Challenge);
    }

    [Fact]
    public async Task Unanswered_challenge_expires_and_the_sync_fails()
    {
        await using var w = await BankWorld.CreateAsync(challengeTimeout: TimeSpan.FromMilliseconds(250));
        var id = await w.AddLoginAsync(BankWorld.Alice, "demo+code");
        var s = await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, true, false));
        Assert.False(s.Succeeded);
        Assert.Contains("expired", s.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expired", (await w.For(BankWorld.Alice).GetLoginAsync(id))!.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.False(w.Coordinator.SubmitAnswer(BankWorld.Alice, id, s.ChallengeId, FakeBankConnector.CodeAnswer));
    }

    [Fact]
    public async Task Approval_that_never_completes_expires_too()
    {
        await using var w = await BankWorld.CreateAsync(challengeTimeout: TimeSpan.FromMilliseconds(300));
        var id = await w.AddLoginAsync(BankWorld.Alice, "demo+approve999");
        var s = await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, true, false));
        Assert.False(s.Succeeded);
        Assert.Contains("expired", s.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Challenges_can_also_arise_while_refreshing_and_fetching_statements()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice, "demo+refreshcode+statementcode+statements");
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, true, false));
        await w.LinkAllAsync(BankWorld.Alice, id);
        var giro = await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId);
        await w.For(BankWorld.Alice).SetStatementsAsync(giro.Id, true, StatementsInterval.Daily);

        var s = await w.SyncAsync(BankWorld.Alice, id, null, FakeBankConnector.CodeAnswer, FakeBankConnector.CodeAnswer);
        Assert.True(s.Succeeded, s.Error);
        Assert.NotEmpty(await w.For(BankWorld.Alice).GetLoginStatementsAsync(id));
        Assert.NotEmpty(await w.TransactionsAsync((await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId)).LinkedAccountId!.Value));
    }

    [Fact]
    public async Task One_sync_at_a_time_per_login_and_cancel_works()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice, "demo+code");
        Assert.Equal(StartResult.Started, await w.Coordinator.StartAsync(BankWorld.Alice, new SyncRequest(id, true, false)));
        await WaitForPhaseAsync(w, BankWorld.Alice, id, SyncPhase.WaitingForInput);
        Assert.Equal(StartResult.AlreadyRunning, await w.Coordinator.SyncNowAsync(BankWorld.Alice, id));
        Assert.True(await w.For(BankWorld.Alice).CancelSyncAsync(id));
        await w.Coordinator.WhenIdleAsync(id);
        var s = w.Coordinator.GetStatus(BankWorld.Alice, id)!;
        Assert.False(s.Succeeded);
        Assert.Contains("cancel", s.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StartResult.Started, await w.Coordinator.StartAsync(BankWorld.Alice, new SyncRequest(id, true, false))); // free again
        await w.For(BankWorld.Alice).CancelSyncAsync(id);
        await w.Coordinator.WhenIdleAsync(id);
    }

    // ---- scheduling entry points ----

    private static async Task SetLastSyncAsync(BankWorld w, int id, TimeSpan ago, RefreshInterval interval)
    {
        await using var db = await w.T.System.CreateDbContextAsync();
        var login = await db.BankLogins.SingleAsync(l => l.Id == id);
        login.LastSyncAt = w.Clock.GetUtcNow().UtcDateTime - ago;
        login.RefreshInterval = interval;
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData(RefreshInterval.EveryTwoHours, 3, 1)]
    [InlineData(RefreshInterval.EveryTwoHours, 1, 0)]
    [InlineData(RefreshInterval.FourTimesDaily, 5, 0)]
    [InlineData(RefreshInterval.FourTimesDaily, 7, 1)]
    [InlineData(RefreshInterval.Daily, 23, 0)]
    [InlineData(RefreshInterval.Daily, 25, 1)]
    [InlineData(RefreshInterval.Weekly, 24 * 6, 0)]
    [InlineData(RefreshInterval.Weekly, 24 * 8, 1)]
    [InlineData(RefreshInterval.ManualOnly, 24 * 400, 0)]
    [InlineData(RefreshInterval.Never, 24 * 400, 0)]
    public async Task Page_load_trigger_starts_only_what_is_due(RefreshInterval interval, int hoursAgo, int expectedStarts)
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice);
        await SetLastSyncAsync(w, id, TimeSpan.FromHours(hoursAgo), interval);

        Assert.Equal(expectedStarts, await w.For(BankWorld.Alice).StartDueAsync());
        await w.Coordinator.WhenIdleAsync(id);
        Assert.Equal(0, await w.For(BankWorld.Bob).StartDueAsync()); // never someone else's logins
    }

    [Fact]
    public async Task Page_load_trigger_ignores_other_users_logins_and_running_syncs()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice, "demo+code");
        Assert.Equal(1, await w.For(BankWorld.Alice).StartDueAsync());
        await WaitForPhaseAsync(w, BankWorld.Alice, id, SyncPhase.WaitingForInput);
        Assert.Equal(0, await w.For(BankWorld.Alice).StartDueAsync()); // already running
        Assert.Equal(0, await w.For(BankWorld.Bob).StartDueAsync());
        await w.For(BankWorld.Alice).CancelSyncAsync(id);
        await w.Coordinator.WhenIdleAsync(id);
    }

    [Fact]
    public async Task Sync_all_covers_manual_logins_but_skips_never_while_sync_now_always_runs()
    {
        await using var w = await BankWorld.CreateAsync();
        var auto = await w.AddLoginAsync(BankWorld.Alice, interval: RefreshInterval.Daily);
        var manual = await w.AddLoginAsync(BankWorld.Alice, interval: RefreshInterval.ManualOnly);
        var never = await w.AddLoginAsync(BankWorld.Alice, interval: RefreshInterval.Never);

        Assert.Equal(2, await w.For(BankWorld.Alice).SyncAllAsync());
        foreach (var id in new[] { auto, manual, never }) await w.Coordinator.WhenIdleAsync(id);
        Assert.NotNull(w.Coordinator.GetStatus(BankWorld.Alice, auto));
        Assert.NotNull(w.Coordinator.GetStatus(BankWorld.Alice, manual));
        Assert.Null(w.Coordinator.GetStatus(BankWorld.Alice, never));

        Assert.Equal(StartResult.Started, await w.For(BankWorld.Alice).SyncNowAsync(never));
        await w.Coordinator.WhenIdleAsync(never);
        Assert.True(w.Coordinator.GetStatus(BankWorld.Alice, never)!.Succeeded);
        Assert.NotNull((await w.For(BankWorld.Alice).GetLoginAsync(never))!.LastSyncAt);
    }

    [Fact]
    public async Task Sync_all_and_page_load_decide_statements_independently_of_transactions()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice, "demo+statements", RefreshInterval.EveryTwoHours);
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, true, false));
        await w.LinkAllAsync(BankWorld.Alice, id);
        var giro = await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId);
        await w.For(BankWorld.Alice).SetStatementsAsync(giro.Id, true, StatementsInterval.Monthly);
        await SetLastSyncAsync(w, id, TimeSpan.FromMinutes(5), RefreshInterval.EveryTwoHours); // transactions NOT due

        // Statements are due (never fetched) even though transactions are not: only statements run.
        Assert.Equal(1, await w.For(BankWorld.Alice).StartDueAsync());
        await w.Coordinator.WhenIdleAsync(id);
        Assert.NotEmpty(await w.For(BankWorld.Alice).GetLoginStatementsAsync(id));
        var login = await w.For(BankWorld.Alice).GetLoginAsync(id);
        Assert.True(login!.LastSyncAt > w.Clock.GetUtcNow().UtcDateTime.AddMinutes(-30) && login.LastSyncAt < w.Clock.GetUtcNow().UtcDateTime.AddMinutes(-1));

        // Now both are quiet until the clock moves.
        Assert.Equal(0, await w.For(BankWorld.Alice).StartDueAsync());
    }

    [Fact]
    public async Task Monthly_statement_schedule_end_to_end_with_a_controllable_clock()
    {
        await using var w = await BankWorld.CreateAsync();
        // No "statements" flag: the bank has nothing new, so the daily retry must kick in.
        var id = await w.AddLoginAsync(BankWorld.Alice, "demo", RefreshInterval.Never);
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, true, false));
        await w.LinkAllAsync(BankWorld.Alice, id);
        var giro = await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId);
        await w.For(BankWorld.Alice).SetStatementsAsync(giro.Id, true, StatementsInterval.Monthly);

        Assert.Equal(1, await w.For(BankWorld.Alice).StartDueAsync()); // first try
        await w.Coordinator.WhenIdleAsync(id);
        Assert.Equal(0, await w.For(BankWorld.Alice).StartDueAsync()); // nothing arrived, but not again within a day
        w.Clock.Offset += TimeSpan.FromHours(25);
        Assert.Equal(1, await w.For(BankWorld.Alice).StartDueAsync()); // daily retry
        await w.Coordinator.WhenIdleAsync(id);

        // A statement for the current month arrives (the bank now has one): afterwards nothing is due until next month.
        await using (var db = await w.T.System.CreateDbContextAsync())
        {
            var row = await db.BankLoginAccounts.SingleAsync(a => a.Id == giro.Id);
            row.LastStatementAt = w.Clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync();
        }
        w.Clock.Offset += TimeSpan.FromHours(30);
        Assert.Equal(0, await w.For(BankWorld.Alice).StartDueAsync());
        w.Clock.Offset += TimeSpan.FromDays(40);
        Assert.Equal(1, await w.For(BankWorld.Alice).StartDueAsync()); // a new month began
        await w.Coordinator.WhenIdleAsync(id);
    }
}
