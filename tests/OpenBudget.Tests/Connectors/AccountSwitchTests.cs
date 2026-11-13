#pragma warning disable CA2000 // test objects live for the test
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.FakeBank;
using OpenBudget.Connectors.Sync;
using OpenBudget.Importers.MoneyMoney;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>Switching a linked account from MoneyMoney to its bank connection: preview, dedupe, carry-over, new markers, undo, one-time import.</summary>
public sealed class AccountSwitchTests
{
    private static DateTime Day(int month, int day) => new(2026, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static BankWorld.MmTx Mm(double amount, DateTime date, string name, string purpose, int booked = 1) =>
        new(1, amount, date, name, purpose, booked);

    /// <summary>
    /// MoneyMoney's giro history ends on Oct 25. In the compared window (from Sep 25): the October rent and groceries
    /// worded differently than by the bank, the October streaming booking worded like the bank's, a cash deposit the bank
    /// does not know, and a pending row. The September rent lies before the window.
    /// </summary>
    private static readonly BankWorld.MmTx[] History =
    [
        Mm(-800, Day(9, 3), "VERMIETER", "MIETE SEPTEMBER"),
        Mm(-800, Day(10, 3), "VERMIETER", "MIETE OKTOBER"),
        Mm(-50.67, Day(10, 12), "SUPERMARKT", "EINKAUF"),
        Mm(-19.99, Day(10, 20), "Example Streaming", "Subscription 2026-10"),
        Mm(100, Day(10, 25), "Cash deposit", "Branch"),
        Mm(-5, Day(10, 25), "Reserved", "Card", booked: 0),
    ];

    private static long Giro => RowIds.Namespace(BankWorld.SourceAlice, 1);

    private static string Snapshot(BankWorld w) =>
        w.BuildSnapshot([("MM Giro", "DE00 0000 0000 0000 0000 01"), ("MM Other", null)], History);

    /// <summary>Imports the history, links the giro (still MoneyMoney's) and syncs once so the bank's rows are fetched.</summary>
    private static async Task<(int LoginId, int LinkId)> LinkedAndFetchedAsync(BankWorld w)
    {
        await w.ImportMoneyMoneyAsync(Snapshot(w));
        var loginId = await w.AddLoginAsync(BankWorld.Alice);
        await w.SyncAsync(BankWorld.Alice, loginId, new SyncRequest(loginId, false, false));
        var link = await w.RemoteAsync(BankWorld.Alice, loginId, FakeBankConnector.GiroId);
        await w.For(BankWorld.Alice).LinkAsync(link.Id, Giro);
        var s = await w.SyncAsync(BankWorld.Alice, loginId, new SyncRequest(loginId, true, false));
        Assert.True(s.Succeeded, s.Error);
        return (loginId, link.Id);
    }

    [Fact]
    public async Task A_sync_before_the_switch_only_fetches_for_the_preview()
    {
        await using var w = await BankWorld.CreateAsync();
        var (loginId, linkId) = await LinkedAndFetchedAsync(w);

        Assert.Equal(AccountProviders.MoneyMoney, (await w.AccountAsync(Giro)).Provider);
        var rows = await w.TransactionsAsync(Giro);
        Assert.Equal(History.Length, rows.Count);
        Assert.All(rows, t => Assert.Null(t.ExternalId));
        var link = await w.RemoteAsync(BankWorld.Alice, loginId, FakeBankConnector.GiroId);
        Assert.True(link.AwaitingSwitch);
        Assert.NotNull(link.PendingRefreshAt);
        Assert.Null(link.LastSuccessAt);

        var preview = Assert.Single(await w.Switcher(BankWorld.Alice).PreviewAsync([linkId]));
        Assert.Equal(new DateOnly(2026, 10, 25), preview.HistoryEnd);
        Assert.Equal(new DateOnly(2026, 9, 25), preview.ComparedFrom);
        Assert.Equal(3, preview.Matched);            // rent, groceries, streaming of October
        Assert.Equal(2, preview.NewFromBank);        // November groceries and the October salary
        Assert.Equal(1, preview.UnmatchedMoneyMoney); // the cash deposit
        Assert.Equal(1, preview.PendingReplaced);
        Assert.Equal(2, preview.HistoryRows);         // September rent and the cash deposit stay as they are
        Assert.NotNull(preview.BankBalance);
    }

    [Fact]
    public async Task The_switch_merges_duplicates_keeps_the_users_fields_and_marks_nothing_new()
    {
        await using var w = await BankWorld.CreateAsync();
        var (loginId, linkId) = await LinkedAndFetchedAsync(w);

        long rentId, groceryId, childId, category;
        await using (var db = await w.T.System.CreateDbContextAsync())
        {
            category = (await db.Categories.FirstAsync(c => c.OwnerUserId == BankWorld.Alice)).Id;
            var rent = await db.Transactions.SingleAsync(t => t.AccountId == Giro && t.Purpose == "MIETE OKTOBER");
            rent.Note = "October rent";
            rent.Checked = true;
            rent.CategoryId = category;
            rentId = rent.Id;
            // The groceries are split in MoneyMoney: a parent fully allocated to one child.
            var grocery = await db.Transactions.SingleAsync(t => t.AccountId == Giro && t.Purpose == "EINKAUF");
            grocery.Splitted = SplitState.Parent;
            grocery.SplitOriginalAmount = grocery.Amount;
            groceryId = grocery.Id;
            childId = RowIds.Namespace(BankWorld.SourceAlice, 500);
            db.Transactions.Add(new Transaction
            {
                Id = childId,
                OwnerUserId = BankWorld.Alice,
                AccountId = Giro,
                Amount = grocery.Amount,
                Timestamp = grocery.Timestamp,
                Name = "SUPERMARKT",
                Splitted = SplitState.Child,
                SplitParentId = grocery.Id,
                CategoryId = category,
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await w.SwitchAsync(BankWorld.Alice, linkId));

        var account = await w.AccountAsync(Giro);
        Assert.Equal("fake", account.Provider);
        Assert.StartsWith("11:", account.Balances, StringComparison.Ordinal);
        var rows = await w.TransactionsAsync(Giro);
        // 5 booked MoneyMoney rows + the split child + 2 new bank rows; the pending row is gone.
        Assert.Equal(8, rows.Count);
        Assert.DoesNotContain(rows, t => !t.Booked);
        Assert.All(rows, t => Assert.False(t.IsNew));
        Assert.All(rows.Where(t => t.ExternalId != null).GroupBy(t => t.ExternalId, StringComparer.Ordinal), g => Assert.Single(g));

        var adoptedRent = rows.Single(t => t.Id == rentId);
        Assert.Equal("id:G-2026-10-RENT", adoptedRent.ExternalId);
        Assert.Equal("Example Landlord", adoptedRent.Name); // the bank's row wins
        Assert.Equal("Rent 2026-10", adoptedRent.Purpose);
        Assert.Equal("DE02120300000000202051", adoptedRent.CounterpartyIban);
        Assert.Equal("October rent", adoptedRent.Note);
        Assert.True(adoptedRent.Checked);
        Assert.Equal(category, adoptedRent.CategoryId);

        var adoptedGrocery = rows.Single(t => t.Id == groceryId);
        Assert.Equal("id:G-2026-10-FOOD", adoptedGrocery.ExternalId);
        Assert.Equal(SplitState.Parent, adoptedGrocery.Splitted);
        Assert.Equal(groceryId, rows.Single(t => t.Id == childId).SplitParentId);

        Assert.Contains(rows, t => t.Name == "VERMIETER" && t.ExternalId == null); // September: history before the window
        Assert.Contains(rows, t => t.Name == "Cash deposit" && t.ExternalId == null);

        var link = await w.RemoteAsync(BankWorld.Alice, loginId, FakeBankConnector.GiroId);
        Assert.False(link.AwaitingSwitch);
        Assert.NotNull(link.SwitchedAt);
        Assert.NotNull(link.LastSuccessAt);

        // The switched account's rows are editable now.
        Assert.Equal(EditBlock.None, TransactionEditRules.Check(null, BankWorld.Alice, Giro, account.Provider));
    }

    [Fact]
    public async Task Uploads_ignore_a_switched_account_and_later_syncs_mark_only_arrivals_new()
    {
        await using var w = await BankWorld.CreateAsync();
        var (loginId, linkId) = await LinkedAndFetchedAsync(w);
        // The browser import hands the structure over when it finishes.
        await new StructureTakeoverService(w.T.System, w.T.Notifier, new ChangeOrigin()).TakeOverAsync(BankWorld.Alice);
        await w.SwitchAsync(BankWorld.Alice, linkId);
        var before = await w.TransactionsAsync(Giro);
        var name = (await w.AccountAsync(Giro)).Name;

        // MoneyMoney renames the account and delivers different rows: none of it reaches the switched account.
        var other = w.BuildSnapshot([("Renamed Giro", null), ("MM Other", null)], [Mm(-1, Day(11, 5), "Something", "Else")]);
        await w.ImportMoneyMoneyAsync(other);
        var after = await w.TransactionsAsync(Giro);
        Assert.Equal(before.Select(t => (t.Id, t.ExternalId, t.Amount, t.Name)), after.Select(t => (t.Id, t.ExternalId, t.Amount, t.Name)));
        Assert.Equal(name, (await w.AccountAsync(Giro)).Name);

        // A booking that arrives with a later sync is new.
        await using (var db = await w.T.System.CreateDbContextAsync())
            await db.Transactions.Where(t => t.AccountId == Giro && t.ExternalId == "id:G-2026-11-FOOD").ExecuteDeleteAsync();
        Assert.True((await w.SyncAsync(BankWorld.Alice, loginId, new SyncRequest(loginId, true, false))).Succeeded);
        var rows = await w.TransactionsAsync(Giro);
        Assert.True(Assert.Single(rows, t => t.ExternalId == "id:G-2026-11-FOOD").IsNew);
        Assert.Single(rows, t => t.IsNew);
    }

    [Fact]
    public async Task Switching_without_fetched_rows_lets_the_first_sync_match_without_new_markers()
    {
        await using var w = await BankWorld.CreateAsync();
        await w.ImportMoneyMoneyAsync(Snapshot(w));
        var loginId = await w.AddLoginAsync(BankWorld.Alice);
        await w.SyncAsync(BankWorld.Alice, loginId, new SyncRequest(loginId, false, false));
        var link = await w.RemoteAsync(BankWorld.Alice, loginId, FakeBankConnector.GiroId);
        await w.For(BankWorld.Alice).LinkAsync(link.Id, Giro);

        var preview = Assert.Single(await w.Switcher(BankWorld.Alice).PreviewAsync([link.Id]));
        Assert.Null(preview.BankDataFetchedAt);

        await w.SwitchAsync(BankWorld.Alice, link.Id);
        Assert.True((await w.SyncAsync(BankWorld.Alice, loginId, new SyncRequest(loginId, true, false))).Succeeded);
        var rows = await w.TransactionsAsync(Giro);
        Assert.All(rows, t => Assert.False(t.IsNew));
        Assert.Single(rows, t => t.ExternalId == "id:G-2026-11-FOOD");
    }

    [Fact]
    public async Task The_first_sync_of_a_connector_created_account_marks_nothing_new()
    {
        await using var w = await BankWorld.CreateAsync();
        var loginId = await w.AddLoginAsync(BankWorld.Alice);
        await w.SyncAsync(BankWorld.Alice, loginId, new SyncRequest(loginId, false, false));
        await w.LinkAllAsync(BankWorld.Alice, loginId);
        var account = (await w.RemoteAsync(BankWorld.Alice, loginId, FakeBankConnector.GiroId)).LinkedAccountId!.Value;
        await w.SyncAsync(BankWorld.Alice, loginId, new SyncRequest(loginId, true, false));
        var rows = await w.TransactionsAsync(account);
        Assert.NotEmpty(rows);
        Assert.All(rows, t => Assert.False(t.IsNew));
    }

    [Fact]
    public async Task Only_the_owner_may_switch()
    {
        await using var w = await BankWorld.CreateAsync();
        var (_, linkId) = await LinkedAndFetchedAsync(w);

        // Bob sees neither the link nor the source: nothing happens.
        Assert.Equal(0, await w.Switcher(BankWorld.Bob).SwitchAsync([linkId]));
        Assert.Empty(await w.Switcher(BankWorld.Bob).PreviewAsync([linkId]));
        await Assert.ThrowsAsync<AccessDeniedException>(() => w.Switcher(BankWorld.Bob).GetSourceStateAsync(BankWorld.Alice));
        await Assert.ThrowsAsync<AccessDeniedException>(() => w.Switcher(BankWorld.Bob).SetImportFinishedAsync(BankWorld.Alice, true));

        Assert.Equal(1, await w.Switcher(BankWorld.Alice).SwitchAsync([linkId]));
    }

    [Fact]
    public async Task A_finished_import_refuses_imports_until_resumed()
    {
        await using var w = await BankWorld.CreateAsync();
        var (_, linkId) = await LinkedAndFetchedAsync(w);
        var switcher = w.Switcher(BankWorld.Alice);
        await switcher.SwitchAsync([linkId]);

        var state = await switcher.GetSourceStateAsync(BankWorld.Alice);
        Assert.True(Assert.Single(state.Links).Switched);
        Assert.Null(state.ImportFinishedAt);

        await switcher.SetImportFinishedAsync(BankWorld.Alice, true);
        Assert.NotNull((await switcher.GetSourceStateAsync(BankWorld.Alice)).ImportFinishedAt);
        await Assert.ThrowsAsync<MoneyMoneyImportFinishedException>(() => w.ImportMoneyMoneyAsync(Snapshot(w)));

        await switcher.SetImportFinishedAsync(BankWorld.Alice, false);
        await w.ImportMoneyMoneyAsync(Snapshot(w));
    }
}
