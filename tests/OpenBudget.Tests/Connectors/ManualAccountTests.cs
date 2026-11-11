#pragma warning disable CA2000 // test objects live for the test
using OpenBudget.Connectors.FakeBank;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>
/// Keeping MoneyMoney accounts as manual accounts (and returning them), closing accounts, and entering bookings of
/// manual accounts by hand with the balance following.
/// </summary>
public sealed class ManualAccountTests
{
    private static DateTime Day(int month, int day) => new(2026, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static readonly BankWorld.MmTx[] History =
    [
        new(1, -800, Day(9, 3), "LANDLORD", "RENT SEPTEMBER"),
        new(1, -42.5, Day(10, 12), "GROCER", "SHOPPING"),
        new(2, 25, Day(10, 1), "Interest", "Q3"),
    ];

    private static long Giro => SourceScope.Namespace(BankWorld.SourceAlice, 1);
    private static long Savings => SourceScope.Namespace(BankWorld.SourceAlice, 2);

    private static string Snapshot(BankWorld w, IEnumerable<BankWorld.MmTx>? txs = null) =>
        w.BuildSnapshot([("Old Giro", null), ("Old Savings", null)], txs ?? History);

    private static ManualTransactionService Manual(BankWorld w, string user)
    {
        var access = new AccessScopeProvider(w.T.System, new FixedCurrentUser(user), w.T.Notifier);
        return new ManualTransactionService(new ScopedDbContextFactory(w.T.System, access), w.T.System, w.T.Notifier, new ChangeOrigin(), w.Clock);
    }

    [Fact]
    public async Task Keeping_an_account_as_manual_needs_the_takeover_and_stops_uploads_from_touching_it()
    {
        await using var w = await BankWorld.CreateAsync();
        await w.ImportMoneyMoneyAsync(Snapshot(w));
        var switcher = w.Switcher(BankWorld.Alice);

        var state = await switcher.GetSourceStateAsync(BankWorld.SourceAlice);
        Assert.Equal(2, state.MoneyMoneyAccountsWithoutLink);
        Assert.Equal(2, state.MoneyMoneyAccounts.Single(a => a.AccountId == Giro).Bookings);
        Assert.True((await switcher.GetAccountStateAsync(Giro))!.CanKeepManual);

        await Assert.ThrowsAsync<InvalidOperationException>(() => switcher.KeepManualAsync([Giro], takeOverStructure: false));
        Assert.Equal(2, await switcher.KeepManualAsync([Giro, Savings], takeOverStructure: true));
        Assert.Equal(AccountProviders.Manual, (await w.AccountAsync(Giro)).Provider);

        state = await switcher.GetSourceStateAsync(BankWorld.SourceAlice);
        Assert.Equal(0, state.MoneyMoneyAccountsWithoutLink);
        Assert.Equal(2, state.ManualAccounts.Count);
        Assert.True((await switcher.GetAccountStateAsync(Giro))!.CanReturnToMoneyMoney);

        // An upload with other rows leaves the manual accounts' history and balance alone.
        await w.ImportMoneyMoneyAsync(Snapshot(w, [new(1, -1, Day(11, 1), "OTHER", "OTHER")]));
        Assert.Equal(2, (await w.TransactionsAsync(Giro)).Count);
        Assert.Single(await w.TransactionsAsync(Savings));
    }

    [Fact]
    public async Task Accounts_in_the_overview_show_their_group_path()
    {
        await using var w = await BankWorld.CreateAsync();
        await w.ImportMoneyMoneyAsync(Snapshot(w));
        await using (var db = await w.T.System.CreateDbContextAsync())
        {
            var top = new AccountGroup { Id = SourceScope.Namespace(BankWorld.SourceAlice, 900), SourceId = BankWorld.SourceAlice, Name = "Savings" };
            var sub = new AccountGroup { Id = SourceScope.Namespace(BankWorld.SourceAlice, 901), SourceId = BankWorld.SourceAlice, Name = "Old", ParentGroupId = top.Id };
            db.AccountGroups.AddRange(top, sub);
            await db.SaveChangesAsync();
            (await db.Accounts.FindAsync(Savings))!.GroupId = sub.Id;
            await db.SaveChangesAsync();
        }

        var state = await w.Switcher(BankWorld.Alice).GetSourceStateAsync(BankWorld.SourceAlice);
        Assert.Equal(["Savings › Old:Old Savings", ":Old Giro"], state.MoneyMoneyAccounts.Select(a => $"{a.GroupPath}:{a.Name}"));
    }

    [Fact]
    public async Task Returning_to_MoneyMoney_hands_the_account_to_the_next_upload_and_is_refused_while_finished()
    {
        await using var w = await BankWorld.CreateAsync();
        await w.ImportMoneyMoneyAsync(Snapshot(w));
        var switcher = w.Switcher(BankWorld.Alice);
        await switcher.KeepManualAsync([Giro], takeOverStructure: true);
        await Manual(w, BankWorld.Alice).AddAsync(Giro, new ManualTransactionInput(new DateOnly(2026, 11, 2), -5m, null, "Kiosk", null, null, null));

        await switcher.SetImportFinishedAsync(BankWorld.SourceAlice, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => switcher.ReturnToMoneyMoneyAsync(Giro));
        await switcher.SetImportFinishedAsync(BankWorld.SourceAlice, false);

        await switcher.ReturnToMoneyMoneyAsync(Giro);
        Assert.Equal(AccountProviders.MoneyMoney, (await w.AccountAsync(Giro)).Provider);
        await w.ImportMoneyMoneyAsync(Snapshot(w));
        Assert.Equal(2, (await w.TransactionsAsync(Giro)).Count);
        Assert.DoesNotContain(await w.TransactionsAsync(Giro), t => t.Name == "Kiosk");
    }

    [Fact]
    public async Task Linked_accounts_and_other_users_cannot_be_kept_manual()
    {
        await using var w = await BankWorld.CreateAsync();
        await w.ImportMoneyMoneyAsync(Snapshot(w));
        var loginId = await w.AddLoginAsync(BankWorld.Alice);
        await w.SyncAsync(BankWorld.Alice, loginId, new SyncRequest(loginId, false, false));
        var link = await w.RemoteAsync(BankWorld.Alice, loginId, FakeBankConnector.GiroId);
        await w.For(BankWorld.Alice).LinkAsync(link.Id, Giro);

        var switcher = w.Switcher(BankWorld.Alice);
        Assert.False((await switcher.GetAccountStateAsync(Giro))!.CanKeepManual);
        await Assert.ThrowsAsync<InvalidOperationException>(() => switcher.KeepManualAsync([Giro], takeOverStructure: true));
        Assert.Empty(await switcher.PreviewKeepManualAsync([Giro]));
        Assert.Single(await switcher.PreviewKeepManualAsync([Giro, Savings]));

        await Assert.ThrowsAsync<AccessDeniedException>(() => w.Switcher(BankWorld.Bob).KeepManualAsync([Savings], takeOverStructure: true));
        Assert.Equal(AccountProviders.MoneyMoney, (await w.AccountAsync(Savings)).Provider);
    }

    [Fact]
    public async Task Accounts_left_on_MoneyMoney_after_finishing_stay_readable_but_read_only()
    {
        await using var w = await BankWorld.CreateAsync();
        await w.ImportMoneyMoneyAsync(Snapshot(w));
        var switcher = w.Switcher(BankWorld.Alice);
        await switcher.SetImportFinishedAsync(BankWorld.SourceAlice, true);

        Assert.Equal(2, (await w.TransactionsAsync(Giro)).Count);
        var row = (await w.TransactionsAsync(Giro))[0];
        var edits = new TransactionEditService(
            new ScopedDbContextFactory(w.T.System, new AccessScopeProvider(w.T.System, new FixedCurrentUser(BankWorld.Alice), w.T.Notifier)),
            w.T.Notifier, new ChangeOrigin());
        await Assert.ThrowsAsync<AccessDeniedException>(() => edits.SetNoteAsync([row.Id], "note"));

        // Keeping them as manual accounts still works after finishing and makes them editable.
        await switcher.KeepManualAsync([Giro], takeOverStructure: true);
        await edits.SetNoteAsync([row.Id], "note");
    }

    [Fact]
    public async Task Closed_accounts_count_as_handled_and_survive_uploads()
    {
        await using var w = await BankWorld.CreateAsync();
        await w.ImportMoneyMoneyAsync(Snapshot(w));
        var switcher = w.Switcher(BankWorld.Alice);

        Assert.Equal(1, await switcher.SetClosedAsync([Savings], true));
        var state = await switcher.GetSourceStateAsync(BankWorld.SourceAlice);
        Assert.Equal(1, state.MoneyMoneyAccountsWithoutLink);
        Assert.Equal(Savings, Assert.Single(state.ClosedAccounts).AccountId);
        Assert.True((await switcher.GetAccountStateAsync(Savings))!.Closed);

        // Without the takeover the upload replaces the accounts; the flag is OpenBudget's and stays.
        await w.ImportMoneyMoneyAsync(Snapshot(w));
        var savings = await w.AccountAsync(Savings);
        Assert.True(savings.Closed);
        Assert.Equal(AccountProviders.MoneyMoney, savings.Provider);
        Assert.Single(await w.TransactionsAsync(Savings));

        await Assert.ThrowsAsync<AccessDeniedException>(() => w.Switcher(BankWorld.Bob).SetClosedAsync([Savings], false));
        await switcher.SetClosedAsync([Savings], false);
        Assert.False((await w.AccountAsync(Savings)).Closed);
    }

    [Fact]
    public async Task Bookings_of_a_manual_account_are_added_edited_and_deleted_with_the_balance_following()
    {
        await using var w = await BankWorld.CreateAsync();
        await w.ImportMoneyMoneyAsync(Snapshot(w));
        await w.Switcher(BankWorld.Alice).KeepManualAsync([Giro], takeOverStructure: true);
        var manual = Manual(w, BankWorld.Alice);
        Assert.Equal(10m, (await w.AccountAsync(Giro)).CurrentBalance);

        var category = SourceScope.Namespace(BankWorld.SourceAlice, 50);
        await using (var db = await w.T.System.CreateDbContextAsync())
        {
            db.Categories.Add(new Category { Id = category, SourceId = BankWorld.SourceAlice, Name = "Food" });
            await db.SaveChangesAsync();
        }

        var id = await manual.AddAsync(Giro, new ManualTransactionInput(new DateOnly(2026, 11, 5), -12.34m, null, "Bakery", "Bread", category, "breakfast"));
        var added = (await w.TransactionsAsync(Giro)).Single(t => t.Id == id);
        Assert.Equal(-1234, added.Amount);
        Assert.Equal("EUR", added.Currency);
        Assert.Equal(category, added.CategoryId);
        Assert.Equal(TransactionCategorySource.Manual, added.CategorySetBy);
        Assert.False(added.IsNew);
        Assert.Null(added.ExternalId);
        Assert.True(id >= SourceScope.Namespace(BankWorld.SourceAlice, BankTransactionStore.ConnectorRowBase));
        Assert.Equal(-2.34m, (await w.AccountAsync(Giro)).CurrentBalance);

        // A history row from MoneyMoney: amount, date and payee are editable.
        var rent = (await w.TransactionsAsync(Giro)).Single(t => t.Name == "LANDLORD");
        await manual.UpdateAsync(rent.Id, new ManualTransactionInput(new DateOnly(2026, 9, 4), -750m, null, "Landlord", "Rent", null, null));
        rent = (await w.TransactionsAsync(Giro)).Single(t => t.Id == rent.Id);
        Assert.Equal(-75000, rent.Amount);
        Assert.Equal("Landlord", rent.Name);
        Assert.Equal(BankTransactionStore.ToTimestamp(new DateOnly(2026, 9, 4)), rent.Timestamp);
        Assert.Equal(47.66m, (await w.AccountAsync(Giro)).CurrentBalance);

        // Another currency leaves the balance alone.
        await manual.UpdateAsync(id, new ManualTransactionInput(new DateOnly(2026, 11, 5), -12.34m, "usd", "Bakery", null, null, null));
        Assert.Equal("USD", (await w.TransactionsAsync(Giro)).Single(t => t.Id == id).Currency);
        Assert.Equal(60m, (await w.AccountAsync(Giro)).CurrentBalance);

        await manual.DeleteAsync(rent.Id);
        Assert.DoesNotContain(await w.TransactionsAsync(Giro), t => t.Id == rent.Id);
        Assert.Equal(810m, (await w.AccountAsync(Giro)).CurrentBalance);
    }

    [Fact]
    public async Task Bookings_are_entered_only_on_manual_accounts_by_the_owner()
    {
        await using var w = await BankWorld.CreateAsync();
        await w.ImportMoneyMoneyAsync(Snapshot(w));
        await w.Switcher(BankWorld.Alice).KeepManualAsync([Giro], takeOverStructure: true);
        var input = new ManualTransactionInput(new DateOnly(2026, 11, 5), -1m, null, "X", null, null, null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Manual(w, BankWorld.Alice).AddAsync(Savings, input));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Manual(w, BankWorld.Bob).AddAsync(Giro, input));
        var mmRow = (await w.TransactionsAsync(Savings))[0];
        await Assert.ThrowsAsync<InvalidOperationException>(() => Manual(w, BankWorld.Alice).UpdateAsync(mmRow.Id, input));
        Assert.True(await Manual(w, BankWorld.Alice).CanEnterAsync(Giro));
        Assert.False(await Manual(w, BankWorld.Alice).CanEnterAsync(Savings));
    }
}
