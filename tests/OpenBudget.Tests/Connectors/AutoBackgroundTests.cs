#pragma warning disable CA2000 // test objects live for the test
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.FakeBank;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;
using static OpenBudget.Services.BackgroundDefaults;

namespace OpenBudget.Tests.Connectors;

/// <summary>The automatic background setting: on after enough TAN-free syncs, off after a stop, never over the owner's choice.</summary>
public sealed class AutoBackgroundTests
{
    private const int After = 3;

    [Fact]
    public void Clean_syncs_switch_a_capable_login_on_after_the_configured_count()
    {
        var login = new BankLogin();
        Record(login, SyncResult.Clean, capable: true, After);
        Record(login, SyncResult.Clean, capable: true, After);
        Assert.False(login.BackgroundEnabled);
        Record(login, SyncResult.Failed, capable: true, After);
        Assert.Equal(2, login.SyncsWithoutTan);
        Record(login, SyncResult.Clean, capable: true, After);
        Assert.True(login.BackgroundEnabled);
        Assert.False(login.BackgroundByUser);
    }

    [Fact]
    public void A_tan_starts_the_count_again_and_an_incapable_login_never_switches_on()
    {
        var login = new BankLogin();
        Record(login, SyncResult.Clean, capable: true, After);
        Record(login, SyncResult.Clean, capable: true, After);
        Record(login, SyncResult.AskedTan, capable: true, After);
        Record(login, SyncResult.Clean, capable: true, After);
        Assert.Equal(1, login.SyncsWithoutTan);
        Assert.False(login.BackgroundEnabled);

        var incapable = new BankLogin();
        for (var i = 0; i < 5; i++) Record(incapable, SyncResult.Clean, capable: false, After);
        Assert.False(incapable.BackgroundEnabled);
    }

    [Fact]
    public void A_stop_for_the_owner_switches_automatic_background_syncs_off()
    {
        var login = new BankLogin { BackgroundEnabled = true, SyncsWithoutTan = 7 };
        Record(login, SyncResult.StoppedForOwner, capable: true, After);
        Assert.False(login.BackgroundEnabled);
        Assert.Equal(0, login.SyncsWithoutTan);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_owners_choice_is_never_changed(bool chosen)
    {
        var login = new BankLogin { BackgroundEnabled = chosen, BackgroundByUser = true };
        for (var i = 0; i < 5; i++) Record(login, SyncResult.Clean, capable: true, After);
        Record(login, SyncResult.StoppedForOwner, capable: true, After);
        Assert.Equal(chosen, login.BackgroundEnabled);
    }

    private static async Task<BankLogin> StoredAsync(BankWorld w, int id)
    {
        await using var db = await w.T.System.CreateDbContextAsync();
        return await db.BankLogins.AsNoTracking().SingleAsync(l => l.Id == id);
    }

    private static async Task SwitchOffAutomaticallyAsync(BankWorld w, int id)
    {
        await using var db = await w.T.System.CreateDbContextAsync();
        await db.BankLogins.Where(l => l.Id == id).ExecuteUpdateAsync(s => s.SetProperty(l => l.BackgroundEnabled, false));
    }

    [Fact]
    public async Task Manual_syncs_without_a_tan_switch_background_syncs_on()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice);
        await SwitchOffAutomaticallyAsync(w, id);
        for (var i = 0; i < w.Options.AutoBackgroundAfter; i++)
        {
            Assert.False((await StoredAsync(w, id)).BackgroundEnabled);
            Assert.True((await w.SyncAsync(BankWorld.Alice, id)).Succeeded);
        }
        var login = await StoredAsync(w, id);
        Assert.True(login.BackgroundEnabled);
        Assert.False(login.BackgroundByUser);
        var view = (await w.For(BankWorld.Alice).GetLoginAsync(id))!;
        Assert.Equal(w.Options.AutoBackgroundAfter, view.AutoBackgroundAfter);
    }

    [Fact]
    public async Task Manual_syncs_that_ask_for_a_tan_leave_background_syncs_off()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice, "demo+code");
        await SwitchOffAutomaticallyAsync(w, id);
        for (var i = 0; i < w.Options.AutoBackgroundAfter + 1; i++)
            Assert.True((await w.SyncAsync(BankWorld.Alice, id, null, FakeBankConnector.CodeAnswer)).Succeeded);
        var login = await StoredAsync(w, id);
        Assert.False(login.BackgroundEnabled);
        Assert.Equal(0, login.SyncsWithoutTan);
    }

    [Fact]
    public async Task The_owners_off_stays_until_handed_back_to_the_automatic_rule()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice);
        var bank = w.For(BankWorld.Alice);
        await bank.SetBackgroundAsync(id, false);
        for (var i = 0; i < w.Options.AutoBackgroundAfter; i++)
            Assert.True((await w.SyncAsync(BankWorld.Alice, id)).Succeeded);
        var view = (await bank.GetLoginAsync(id))!;
        Assert.False(view.BackgroundEnabled);
        Assert.True(view.BackgroundByUser);

        // enough TAN-free syncs already: back to automatic switches it on at once
        await bank.SetBackgroundAutomaticAsync(id);
        view = (await bank.GetLoginAsync(id))!;
        Assert.True(view.BackgroundEnabled);
        Assert.False(view.BackgroundByUser);
    }

    [Fact]
    public async Task Existing_settings_only_the_owner_can_have_made_are_marked_as_the_owners()
    {
        await using var w = await BankWorld.CreateAsync();
        var off = await w.AddLoginAsync(BankWorld.Alice);
        var invalid = await w.AddLoginAsync(BankWorld.Alice);
        var on = await w.AddLoginAsync(BankWorld.Alice);
        await using (var db = await w.T.System.CreateDbContextAsync())
        {
            await db.BankLogins.Where(l => l.Id != on).ExecuteUpdateAsync(s => s.SetProperty(l => l.BackgroundEnabled, false));
            await db.BankLogins.Where(l => l.Id == invalid).ExecuteUpdateAsync(s => s.SetProperty(l => l.CredentialsInvalid, true));
            Assert.Equal(1, await MarkOwnerChoicesAsync(db, w.Registry, w.Secrets));
        }
        Assert.True((await StoredAsync(w, off)).BackgroundByUser);
        Assert.False((await StoredAsync(w, invalid)).BackgroundByUser);
        Assert.False((await StoredAsync(w, on)).BackgroundByUser);
    }

    [Fact]
    public async Task Signing_in_again_clears_rejected_credentials_and_the_sync_succeeds()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice, "demo+loginfail");
        Assert.False((await w.SyncAsync(BankWorld.Alice, id)).Succeeded);
        var bank = w.For(BankWorld.Alice);
        var view = (await bank.GetLoginAsync(id))!;
        Assert.True(view.CredentialsInvalid);
        Assert.Equal("demo+loginfail", view.Username);

        await bank.UpdateLoginAsync(id, view.DisplayName, view.RefreshInterval, "demo", "5678");
        Assert.True((await w.SyncAsync(BankWorld.Alice, id)).Succeeded);
        view = (await bank.GetLoginAsync(id))!;
        Assert.False(view.CredentialsInvalid);
        Assert.Null(view.LastError);
    }
}
