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

    private static long Giro => RowIds.Namespace(BankWorld.SourceAlice, 1);
    private static long Savings => RowIds.Namespace(BankWorld.SourceAlice, 2);

    private static string Snapshot(BankWorld w, IEnumerable<BankWorld.MmTx>? txs = null) =>
        w.BuildSnapshot([("Old Giro", null), ("Old Savings", null)], txs ?? History);

    private static ManualTransactionService Manual(BankWorld w, string user)
    {
        var access = new AccessScopeProvider(w.T.System, new FixedCurrentUser(user), w.T.Notifier);
        return new ManualTransactionService(new ScopedDbContextFactory(w.T.System, access), w.T.System, w.T.Notifier, new ChangeOrigin());
    }

    [Fact]
    public async Task Keeping_an_account_as_manual_needs_the_takeover_and_stops_uploads_from_touching_it()
    {
        await using var w = await BankWorld.CreateAsync();
        await w.ImportMoneyMoneyAsync(Snapshot(w));
        var switcher = w.Switcher(BankWorld.Alice);

        var state = await switcher.GetSourceStateAsync(BankWorld.Alice);
        Assert.Equal(2, state.MoneyMoneyAccountsWithoutLink);
        Assert.Equal(2, state.MoneyMoneyAccounts.Single(a => a.AccountId == Giro).Bookings);
        Assert.True((await switcher.GetAccountStateAsync(Giro))!.CanKeepManual);

        Assert.Equal(2, await switcher.KeepManualAsync([Giro, Savings]));
        Assert.Equal(AccountProviders.Manual, (await w.AccountAsync(Giro)).Provider);

        state = await switcher.GetSourceStateAsync(BankWorld.Alice);
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
            var top = new AccountGroup { Id = RowIds.Namespace(BankWorld.SourceAlice, 900), OwnerUserId = BankWorld.Alice, Name = "Savings" };
            var sub = new AccountGroup { Id = RowIds.Namespace(BankWorld.SourceAlice, 901), OwnerUserId = BankWorld.Alice, Name = "Old", ParentGroupId = top.Id };
            db.AccountGroups.AddRange(top, sub);
            await db.SaveChangesAsync();
            (await db.Accounts.FindAsync(Savings))!.GroupId = sub.Id;
            await db.SaveChangesAsync();
        }

        var state = await w.Switcher(BankWorld.Alice).GetSourceStateAsync(BankWorld.Alice);
        Assert.Equal(["Savings › Old:Old Savings", ":Old Giro"], state.MoneyMoneyAccounts.Select(a => $"{a.GroupPath}:{a.Name}"));
    }

    [Fact]
    public async Task Returning_to_MoneyMoney_hands_the_account_to_the_next_upload_and_is_refused_while_finished()
    {
        await using var w = await BankWorld.CreateAsync();
        await w.ImportMoneyMoneyAsync(Snapshot(w));
        var switcher = w.Switcher(BankWorld.Alice);
        await switcher.KeepManualAsync([Giro]);
        await Manual(w, BankWorld.Alice).AddAsync(Giro, new ManualTransactionInput(new DateOnly(2026, 11, 2), -5m, "Kiosk", null, null, null));

        await switcher.SetImportFinishedAsync(BankWorld.Alice, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => switcher.ReturnToMoneyMoneyAsync(Giro));
        await switcher.SetImportFinishedAsync(BankWorld.Alice, false);

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
        Assert.Equal(0, await switcher.KeepManualAsync([Giro]));
        Assert.Empty(await switcher.PreviewKeepManualAsync([Giro]));
        Assert.Single(await switcher.PreviewKeepManualAsync([Giro, Savings]));

        await Assert.ThrowsAsync<AccessDeniedException>(() => w.Switcher(BankWorld.Bob).KeepManualAsync([Savings]));
        Assert.Equal(AccountProviders.MoneyMoney, (await w.AccountAsync(Savings)).Provider);
    }

    [Fact]
    public async Task Finishing_the_import_keeps_the_remaining_MoneyMoney_accounts_as_manual_and_editable_ones()
    {
        await using var w = await BankWorld.CreateAsync();
        await w.ImportMoneyMoneyAsync(Snapshot(w));
        var switcher = w.Switcher(BankWorld.Alice);
        var row = (await w.TransactionsAsync(Giro))[0];
        var edits = new TransactionEditService(
            new ScopedDbContextFactory(w.T.System, new AccessScopeProvider(w.T.System, new FixedCurrentUser(BankWorld.Alice), w.T.Notifier)),
            w.T.Notifier, new ChangeOrigin());
        await Assert.ThrowsAsync<AccessDeniedException>(() => edits.SetNoteAsync([row.Id], "note"));

        await switcher.SetImportFinishedAsync(BankWorld.Alice, true);

        Assert.Equal(AccountProviders.Manual, (await w.AccountAsync(Giro)).Provider);
        Assert.Equal(2, (await w.TransactionsAsync(Giro)).Count);
        await edits.SetNoteAsync([row.Id], "note");
    }

    [Fact]
    public async Task Closed_accounts_count_as_handled_and_survive_uploads()
    {
        await using var w = await BankWorld.CreateAsync();
        await w.ImportMoneyMoneyAsync(Snapshot(w));
        var switcher = w.Switcher(BankWorld.Alice);

        Assert.Equal(1, await switcher.SetClosedAsync([Savings], true));
        var state = await switcher.GetSourceStateAsync(BankWorld.Alice);
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
        await w.Switcher(BankWorld.Alice).KeepManualAsync([Giro]);
        var manual = Manual(w, BankWorld.Alice);
        Assert.Equal(10m, (await w.AccountAsync(Giro)).CurrentBalance);

        var category = RowIds.Namespace(BankWorld.SourceAlice, 50);
        await using (var db = await w.T.System.CreateDbContextAsync())
        {
            db.Categories.Add(new Category { Id = category, OwnerUserId = BankWorld.Alice, Name = "Food" });
            await db.SaveChangesAsync();
        }

        var id = await manual.AddAsync(Giro, new ManualTransactionInput(new DateOnly(2026, 11, 5), -12.34m, "Bakery", "Bread", category, "breakfast"));
        var added = (await w.TransactionsAsync(Giro)).Single(t => t.Id == id);
        Assert.Equal(-1234, added.Amount);
        Assert.Equal("EUR", added.Currency);
        Assert.Equal(category, added.CategoryId);
        Assert.Equal(TransactionCategorySource.Manual, added.CategorySetBy);
        Assert.False(added.IsNew);
        Assert.Null(added.ExternalId);
        Assert.True(id >= RowIds.Namespace(BankWorld.SourceAlice, BankTransactionStore.ConnectorRowBase));
        Assert.Equal(-2.34m, (await w.AccountAsync(Giro)).CurrentBalance);

        // A history row from MoneyMoney: amount, date and payee are editable.
        var rent = (await w.TransactionsAsync(Giro)).Single(t => t.Name == "LANDLORD");
        await manual.UpdateAsync(rent.Id, new ManualTransactionInput(new DateOnly(2026, 9, 4), -750m, "Landlord", "Rent", null, null));
        rent = (await w.TransactionsAsync(Giro)).Single(t => t.Id == rent.Id);
        Assert.Equal(-75000, rent.Amount);
        Assert.Equal("Landlord", rent.Name);
        Assert.Equal(BankTransactionStore.ToTimestamp(new DateOnly(2026, 9, 4)), rent.Timestamp);
        Assert.Equal(47.66m, (await w.AccountAsync(Giro)).CurrentBalance);

        // An edited booking keeps its currency.
        await manual.UpdateAsync(id, new ManualTransactionInput(new DateOnly(2026, 11, 5), -12.34m, "Bakery", null, null, null));
        Assert.Equal("EUR", (await w.TransactionsAsync(Giro)).Single(t => t.Id == id).Currency);
        Assert.Equal(47.66m, (await w.AccountAsync(Giro)).CurrentBalance);

        await manual.DeleteAsync(rent.Id);
        Assert.DoesNotContain(await w.TransactionsAsync(Giro), t => t.Id == rent.Id);
        Assert.Equal(797.66m, (await w.AccountAsync(Giro)).CurrentBalance);
    }

    [Fact]
    public async Task An_offsetting_entry_books_the_opposite_amount_with_payee_and_category_on_a_manual_account()
    {
        await using var w = await BankWorld.CreateAsync();
        await w.ImportMoneyMoneyAsync(Snapshot(w));
        await w.Switcher(BankWorld.Alice).KeepManualAsync([Giro]);
        var category = RowIds.Namespace(BankWorld.SourceAlice, 51);
        int projectId;
        await using (var db = await w.T.System.CreateDbContextAsync())
        {
            db.Categories.Add(new Category { Id = category, OwnerUserId = BankWorld.Alice, Name = "Own transfer" });
            var project = new Project { Name = "Household", OwnerUserId = BankWorld.Alice };
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            projectId = project.Id;
            db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = projectId, AccountId = Giro });
            db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = projectId, AccountId = Savings });
            var interest = db.Transactions.Single(t => t.AccountId == Savings);
            interest.CategoryId = category;
            await db.SaveChangesAsync();
        }
        var manual = Manual(w, BankWorld.Alice);
        var original = (await w.TransactionsAsync(Savings)).Single();

        // Only the manual account is offered, and not for its own bookings.
        Assert.Equal([Giro], (await manual.GetOffsetTargetsAsync(projectId, Savings)).Select(t => t.AccountId));
        Assert.Empty(await manual.GetOffsetTargetsAsync(projectId, Giro));
        Assert.Empty(await Manual(w, BankWorld.Bob).GetOffsetTargetsAsync(projectId, Savings));

        var id = await manual.CreateOffsettingEntryAsync(original.Id, Giro, "Offsetting entry for Interest");
        var entry = (await w.TransactionsAsync(Giro)).Single(t => t.Id == id);
        Assert.Equal(-original.Amount, entry.Amount);
        Assert.Equal(-2500, entry.Amount);
        Assert.Equal(original.Currency, entry.Currency);
        Assert.Equal(original.Timestamp, entry.Timestamp);
        Assert.Equal(original.Name, entry.Name);
        Assert.Equal(original.Purpose, entry.Purpose);
        Assert.Equal(category, entry.CategoryId);
        Assert.Equal("Offsetting entry for Interest", entry.Note);
        Assert.Equal(-15m, (await w.AccountAsync(Giro)).CurrentBalance);

        // Not on a MoneyMoney account, not by someone else, not on the booking's own account.
        var giroRow = (await w.TransactionsAsync(Giro)).First(t => t.Id != id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manual.CreateOffsettingEntryAsync(giroRow.Id, Savings, null));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Manual(w, BankWorld.Bob).CreateOffsettingEntryAsync(original.Id, Giro, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manual.CreateOffsettingEntryAsync(giroRow.Id, Giro, null));
        Assert.Equal(-15m, (await w.AccountAsync(Giro)).CurrentBalance);
    }

    [Fact]
    public async Task Bookings_are_entered_only_on_manual_accounts_by_the_owner()
    {
        await using var w = await BankWorld.CreateAsync();
        await w.ImportMoneyMoneyAsync(Snapshot(w));
        await w.Switcher(BankWorld.Alice).KeepManualAsync([Giro]);
        var input = new ManualTransactionInput(new DateOnly(2026, 11, 5), -1m, "X", null, null, null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Manual(w, BankWorld.Alice).AddAsync(Savings, input));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Manual(w, BankWorld.Bob).AddAsync(Giro, input));
        var mmRow = (await w.TransactionsAsync(Savings))[0];
        await Assert.ThrowsAsync<InvalidOperationException>(() => Manual(w, BankWorld.Alice).UpdateAsync(mmRow.Id, input));
        Assert.True(await Manual(w, BankWorld.Alice).CanEnterAsync(Giro));
        Assert.False(await Manual(w, BankWorld.Alice).CanEnterAsync(Savings));
    }

    [Fact]
    public async Task The_initial_balance_of_a_manual_account_moves_its_balance_and_is_the_owners_alone()
    {
        await using var w = await BankWorld.CreateAsync();
        await w.ImportMoneyMoneyAsync(Snapshot(w));
        await w.Switcher(BankWorld.Alice).KeepManualAsync([Giro]);
        var alice = Manual(w, BankWorld.Alice);
        var before = await w.AccountAsync(Giro);

        var state = (await alice.GetInitialBalanceAsync(Giro))!;
        Assert.Equal(InitialBalanceLock.None, state.Lock);
        await alice.SetInitialBalanceAsync(Giro, before.InitialBalance - 48453);
        var after = await w.AccountAsync(Giro);
        Assert.Equal(before.InitialBalance - 48453, after.InitialBalance);
        Assert.Equal(before.CurrentBalance - 484.53m, after.CurrentBalance);

        // Accounts the bank still updates keep their initial balance.
        Assert.Equal(InitialBalanceLock.Bank, (await alice.GetInitialBalanceAsync(Savings))!.Lock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => alice.SetInitialBalanceAsync(Savings, 100));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Manual(w, BankWorld.Bob).SetInitialBalanceAsync(Giro, 0));
        Assert.Equal(after.InitialBalance, (await w.AccountAsync(Giro)).InitialBalance);
    }

    [Fact]
    public async Task A_manual_booking_needs_a_payee_and_is_always_booked()
    {
        await using var w = await BankWorld.CreateAsync();
        await w.ImportMoneyMoneyAsync(Snapshot(w));
        await w.Switcher(BankWorld.Alice).KeepManualAsync([Giro]);
        var alice = Manual(w, BankWorld.Alice);

        await Assert.ThrowsAsync<ArgumentException>(() => alice.AddAsync(Giro, new ManualTransactionInput(new DateOnly(2026, 11, 5), -3m, "  ", null, null, null)));
        var id = await alice.AddAsync(Giro, new ManualTransactionInput(new DateOnly(2026, 11, 5), -3m, "Kiosk", null, null, null));
        await Assert.ThrowsAsync<ArgumentException>(() => alice.UpdateAsync(id, new ManualTransactionInput(new DateOnly(2026, 11, 5), -3m, null, null, null, null)));

        await using (var db = await w.T.System.CreateDbContextAsync())
        {
            db.Transactions.Single(t => t.Id == id).Booked = false;
            await db.SaveChangesAsync();
        }
        await alice.UpdateAsync(id, new ManualTransactionInput(new DateOnly(2026, 11, 5), -3m, "Kiosk", null, null, null));
        Assert.True((await w.TransactionsAsync(Giro)).Single(t => t.Id == id).Booked);
    }

    [Fact]
    public async Task The_currency_of_a_manual_account_changes_only_while_it_has_no_bookings()
    {
        await using var w = await BankWorld.CreateAsync();
        await w.ImportMoneyMoneyAsync(Snapshot(w));
        await w.Switcher(BankWorld.Alice).KeepManualAsync([Giro]);
        var alice = Manual(w, BankWorld.Alice);

        var state = (await alice.GetInitialBalanceAsync(Giro))!;
        Assert.True(state.HasBookings);
        Assert.False(state.CurrencyEditable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => alice.SetCurrencyAsync(Giro, "USD"));

        await using (var db = await w.T.System.CreateDbContextAsync())
        {
            db.Transactions.RemoveRange(db.Transactions.Where(t => t.AccountId == Giro));
            await db.SaveChangesAsync();
        }
        Assert.True((await alice.GetInitialBalanceAsync(Giro))!.CurrencyEditable);
        await Assert.ThrowsAsync<ArgumentException>(() => alice.SetCurrencyAsync(Giro, "US"));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Manual(w, BankWorld.Bob).SetCurrencyAsync(Giro, "USD"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => alice.SetCurrencyAsync(Savings, "USD"));
        await alice.SetCurrencyAsync(Giro, "usd");
        Assert.Equal("USD", (await w.AccountAsync(Giro)).Currency);

        // New bookings take the account's currency.
        var id = await alice.AddAsync(Giro, new ManualTransactionInput(new DateOnly(2026, 11, 5), -3m, "Kiosk", null, null, null));
        Assert.Equal("USD", (await w.TransactionsAsync(Giro)).Single(t => t.Id == id).Currency);
        Assert.False((await alice.GetInitialBalanceAsync(Giro))!.CurrencyEditable);
    }

    [Fact]
    public void Calibrating_sets_the_initial_balance_so_the_day_balance_matches()
    {
        static DateOnly Local(DateTime utc) => DateOnly.FromDateTime(utc);
        (DateTime, decimal)[] flows = [(Day(3, 1), -100m), (Day(3, 10), 50m), (Day(3, 20), -20m)];

        // Current balance 30 (after all three); entered 100 today → initial moves by 70.
        Assert.Equal(1000 + 7000, ManualTransactionService.CalibratedInitialBalance(1000, 30m, flows, new DateOnly(2026, 4, 1), 100m, Local));
        // At the end of 10 March the account showed 30 + 20 = 50; entered 40 → initial moves by -10.
        Assert.Equal(1000 - 1000, ManualTransactionService.CalibratedInitialBalance(1000, 30m, flows, new DateOnly(2026, 3, 10), 40m, Local));
    }
}
