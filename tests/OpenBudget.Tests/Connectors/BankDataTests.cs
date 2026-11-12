#pragma warning disable CA2000 // test objects live for the test
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors;
using OpenBudget.Connectors.FakeBank;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>Linking, switching, first-sync boundary, dedup, statements and visibility.</summary>
public sealed class BankDataTests
{
    private static readonly DateTime D1102 = new(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);

    private static BankWorld.MmTx Mm(int account, double amount, DateTime date, string name, string purpose, int booked = 1) =>
        new(account, amount, date, name, purpose, booked);

    /// <summary>Imports a MoneyMoney snapshot with a giro account whose IBAN matches the fake bank's.</summary>
    private static async Task<long> ImportGiroAsync(BankWorld w, params BankWorld.MmTx[] txs)
    {
        var snapshot = w.BuildSnapshot([("MM Giro", "DE00 0000 0000 0000 0000 01"), ("MM Other", null)], txs);
        await w.ImportMoneyMoneyAsync(snapshot);
        return SourceScope.Namespace(BankWorld.SourceAlice, 1);
    }

    private static async Task<(int LoginId, long AccountId)> LinkedGiroAsync(BankWorld w, string username, params BankWorld.MmTx[] mm)
    {
        var accountId = await ImportGiroAsync(w, mm);
        var id = await w.AddLoginAsync(BankWorld.Alice, username);
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, false, false)); // list accounts only
        var giro = await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId);
        await w.For(BankWorld.Alice).LinkAsync(giro.Id, accountId);
        await w.SwitchAsync(BankWorld.Alice, giro.Id);
        return (id, accountId);
    }

    // ---- linking and switching ----

    [Fact]
    public async Task Candidates_are_owned_moneymoney_accounts_with_an_iban_suggestion()
    {
        await using var w = await BankWorld.CreateAsync();
        var accountId = await ImportGiroAsync(w);
        var id = await w.AddLoginAsync(BankWorld.Alice);
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, false, false));
        var giro = await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId);

        var candidates = await w.For(BankWorld.Alice).GetLinkCandidatesAsync(giro.Id);
        Assert.Equal(2, candidates.Count);
        Assert.Equal(accountId, Assert.Single(candidates, c => c.SuggestedFor).AccountId);
        // Bob owns no such accounts and cannot see alice's.
        Assert.Empty(await w.For(BankWorld.Bob).GetLinkCandidatesAsync(giro.Id));
    }

    [Fact]
    public async Task Depots_only_link_to_depots_and_cash_accounts_only_to_non_depots()
    {
        await using var w = await BankWorld.CreateAsync();
        var snapshot = w.BuildSnapshot([("MM Giro", "DE00 0000 0000 0000 0000 01"), ("MM Depot", null)], [], depots: ["MM Depot"]);
        await w.ImportMoneyMoneyAsync(snapshot);
        long mmGiro = SourceScope.Namespace(BankWorld.SourceAlice, 1), mmDepot = SourceScope.Namespace(BankWorld.SourceAlice, 2);
        var id = await w.AddLoginAsync(BankWorld.Alice);
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, false, false));
        var svc = w.For(BankWorld.Alice);
        var giro = await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId);
        var depot = await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.DepotId);

        Assert.Equal(mmGiro, Assert.Single(await svc.GetLinkCandidatesAsync(giro.Id)).AccountId);
        Assert.Equal(mmDepot, Assert.Single(await svc.GetLinkCandidatesAsync(depot.Id)).AccountId);

        // The server refuses a mismatch even when asked directly.
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.LinkAsync(depot.Id, mmGiro));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.LinkAsync(giro.Id, mmDepot));
    }

    [Fact]
    public async Task Linking_keeps_moneymoney_until_the_switch_and_unlinking_restores_moneymoney()
    {
        await using var w = await BankWorld.CreateAsync();
        var accountId = await ImportGiroAsync(w);
        var id = await w.AddLoginAsync(BankWorld.Alice, "demo");
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, false, false));
        await w.For(BankWorld.Alice).LinkAsync((await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId)).Id, accountId);
        Assert.Equal(AccountProviders.MoneyMoney, (await w.AccountAsync(accountId)).Provider);
        Assert.True((await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId)).AwaitingSwitch);
        await w.SwitchAsync(BankWorld.Alice, (await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId)).Id);
        Assert.Equal("fake", (await w.AccountAsync(accountId)).Provider);

        // A linked account is no longer offered, nor can it be linked twice.
        var savings = await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.SavingsId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.For(BankWorld.Alice).LinkAsync(savings.Id, accountId));

        var giro = await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId);
        await w.For(BankWorld.Alice).UnlinkAsync(giro.Id);
        Assert.Equal(AccountProviders.MoneyMoney, (await w.AccountAsync(accountId)).Provider);
        Assert.Null((await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId)).LinkedAccountId);
    }

    [Fact]
    public async Task Bobs_account_cannot_be_linked_by_alice()
    {
        await using var w = await BankWorld.CreateAsync();
        w.T.Db.Accounts.Add(new Account { Id = SourceScope.Namespace(3, 1), SourceId = 3, Name = "Bob Giro" });
        await w.T.Db.SaveChangesAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice);
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, false, false));
        var giro = await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.For(BankWorld.Alice).LinkAsync(giro.Id, SourceScope.Namespace(3, 1)));
        Assert.Equal(AccountProviders.MoneyMoney, (await w.AccountAsync(SourceScope.Namespace(3, 1))).Provider);
    }

    [Fact]
    public async Task A_moneymoney_upload_leaves_connector_data_intact_until_unlinked()
    {
        await using var w = await BankWorld.CreateAsync();
        var snapshot = w.BuildSnapshot([("MM Giro", "DE00 0000 0000 0000 0000 01"), ("MM Other", null)],
        [
            Mm(1, -10, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), "Old Shop", "Old purchase"),
            Mm(2, -5, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), "Other Shop", "Other purchase"),
        ]);
        await w.ImportMoneyMoneyAsync(snapshot);
        var accountId = SourceScope.Namespace(BankWorld.SourceAlice, 1);
        var id = await w.AddLoginAsync(BankWorld.Alice, "demo+statements");
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, false, false));
        await w.For(BankWorld.Alice).LinkAsync((await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId)).Id, accountId);
        await w.SwitchAsync(BankWorld.Alice, (await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId)).Id);
        Assert.True((await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, true, false))).Succeeded);

        var before = await w.TransactionsAsync(accountId);
        var balanceBefore = (await w.AccountAsync(accountId)).Balances;
        Assert.Contains(before, t => t.ExternalId != null);
        Assert.Contains(before, t => t.Name == "Old Shop"); // MoneyMoney's own row, older than the boundary

        // The same (or any) MoneyMoney upload again.
        await w.ImportMoneyMoneyAsync(snapshot);

        var after = await w.TransactionsAsync(accountId);
        Assert.Equal(before.Select(t => (t.Id, t.ExternalId, t.Amount)), after.Select(t => (t.Id, t.ExternalId, t.Amount)));
        var account = await w.AccountAsync(accountId);
        Assert.Equal("fake", account.Provider);
        Assert.Equal(balanceBefore, account.Balances);
        Assert.NotNull(account.RefreshTimestamp);
        // The other (MoneyMoney-owned) account was re-imported as usual.
        Assert.Single(await w.TransactionsAsync(SourceScope.Namespace(BankWorld.SourceAlice, 2)));

        // After unlinking, the next upload refills the account from MoneyMoney alone.
        await w.For(BankWorld.Alice).UnlinkAsync((await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId)).Id);
        await w.ImportMoneyMoneyAsync(snapshot);
        var refilled = await w.TransactionsAsync(accountId);
        Assert.Equal("Old Shop", Assert.Single(refilled).Name);
        Assert.Null(refilled[0].ExternalId);
        Assert.Equal(AccountProviders.MoneyMoney, (await w.AccountAsync(accountId)).Provider);
    }

    [Fact]
    public async Task Connector_created_accounts_live_in_the_owners_source_and_become_manual_when_unlinked()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice);
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, false, false));
        Assert.Equal(3, await w.LinkAllAsync(BankWorld.Alice, id));

        var giro = await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId);
        var account = await w.AccountAsync(giro.LinkedAccountId!.Value);
        await using (var db = await w.T.System.CreateDbContextAsync())
        {
            var source = await db.ImportSources.SingleAsync(s => s.Id == account.SourceId);
            Assert.Equal(BankWorld.AliceSourceName, source.Name);
            Assert.Equal(1, await db.ImportSources.CountAsync(s => s.OwnerUserId == BankWorld.Alice));
        }
        Assert.Equal(account.SourceId, (await w.AccountAsync((await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.DepotId)).LinkedAccountId!.Value)).SourceId);
        Assert.Equal("fake", account.Provider);
        Assert.Equal("DE00000000000000000001", account.Iban);
        Assert.Equal(1, account.Type);
        Assert.Equal(7, (await w.AccountAsync((await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.DepotId)).LinkedAccountId!.Value)).Type);

        Assert.True((await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, true, false))).Succeeded);
        Assert.Equal(41, (await w.TransactionsAsync(account.Id)).Count);
        Assert.Equal(1234.56m, (await w.AccountAsync(account.Id)).CurrentBalance);
        await using (var db = await w.T.System.CreateDbContextAsync())
        {
            var depot = (await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.DepotId)).LinkedAccountId!.Value;
            Assert.Equal(2, await db.Holdings.CountAsync(h => h.AccountId == depot));
        }

        await w.For(BankWorld.Alice).UnlinkAsync(giro.Id);
        Assert.Equal(AccountProviders.Manual, (await w.AccountAsync(account.Id)).Provider);
        Assert.Equal(41, (await w.TransactionsAsync(account.Id)).Count); // data stays
        // ...and a detached account can be linked again.
        var candidates = await w.For(BankWorld.Alice).GetLinkCandidatesAsync(giro.Id);
        Assert.Contains(candidates, c => c.AccountId == account.Id);
    }

    [Fact]
    public async Task Connector_transactions_count_in_the_same_totals_as_moneymoney_ones()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice);
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, false, false));
        await w.LinkAllAsync(BankWorld.Alice, id);
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, true, false));
        var giro = (await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId)).LinkedAccountId!.Value;

        var query = w.View(BankWorld.Alice).TransactionQueries;
        var sourceId = (await w.AccountAsync(giro)).SourceId;
        var result = await query.QueryAsync(new TransactionQuery { SourceId = sourceId });
        // 41 giro + 10 savings bookings, all counted, none treated as split parents.
        Assert.Equal(51, result.TotalCount);
        var expected = (await w.TransactionsAsync(giro)).Sum(t => t.Amount) / 100m
            + (await w.TransactionsAsync((await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.SavingsId)).LinkedAccountId!.Value)).Sum(t => t.Amount) / 100m;
        Assert.Equal(expected, result.TotalSum);
    }

    // ---- first-sync boundary ----

    [Fact]
    public async Task First_sync_starts_at_the_newest_booked_day_and_matches_that_day_instead_of_duplicating()
    {
        await using var w = await BankWorld.CreateAsync();
        var (id, accountId) = await LinkedGiroAsync(w, "demo",
            Mm(1, -800, new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc), "Landlord", "Rent 2026-10"),
            // Same day as the newest booking, but named differently by MoneyMoney than by the "bank":
            Mm(1, -52.10, D1102, "GROCERY STORE GMBH", "Groceries 2026-11"),
            // Pending rows must neither define the boundary nor survive.
            Mm(1, -99, new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc), "Pending Thing", "Reserved", booked: 0));

        var s = await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, true, false));
        Assert.True(s.Succeeded, s.Error);

        var rows = await w.TransactionsAsync(accountId);
        // October rent: before the boundary, untouched and not re-fetched. The grocery row: adopted, not duplicated.
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, t => t.Name == "Landlord" && t.ExternalId == null);
        var grocery = Assert.Single(rows, t => t.Amount == -5210);
        Assert.Equal("Example Grocery Store", grocery.Name); // the bank's text wins
        Assert.Equal("id:G-2026-11-FOOD", grocery.ExternalId);
        Assert.DoesNotContain(rows, t => !t.Booked);

        var link = await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId);
        Assert.NotNull(link.LastSuccessAt);
        await using var db = await w.T.System.CreateDbContextAsync();
        Assert.Equal(D1102, (await db.BankLoginAccounts.SingleAsync(a => a.Id == link.Id)).SyncFloorUtc);
    }

    [Fact]
    public async Task Cutover_day_bookings_worded_differently_by_the_bank_are_adopted_by_amount_and_count()
    {
        await using var w = await BankWorld.CreateAsync();
        var oct20 = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
        var (id, accountId) = await LinkedGiroAsync(w, "demo+dups",
            Mm(1, -800, new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc), "Landlord", "Rent 2026-10"),
            // The newest MoneyMoney day holds two identical bookings whose text shares nothing with the bank's.
            Mm(1, -3.50, oct20, "KARTENZAHLUNG", "POS 0815 CAFE"),
            Mm(1, -3.50, oct20, "KARTENZAHLUNG", "POS 0815 CAFE"));

        var s = await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, true, false));
        Assert.True(s.Succeeded, s.Error);

        var rows = await w.TransactionsAsync(accountId);
        // Both cutover-day bookings adopted (one each), none duplicated; the later grocery booking is new; rent untouched.
        var coffee = rows.Where(t => t.Amount == -350).ToList();
        Assert.Equal(2, coffee.Count);
        Assert.All(coffee, t => Assert.Equal("Example Coffee Shop", t.Name));
        Assert.Equal(new[] { "id:G-2026-10-COFFEE-A", "id:G-2026-10-COFFEE-B" }, coffee.Select(t => t.ExternalId).Order(StringComparer.Ordinal));
        Assert.Single(rows, t => t.Amount == -5210);
        Assert.Single(rows, t => t.Amount == -80000 && t.ExternalId == null);
        // No booking from the bank exists twice.
        Assert.All(rows.Where(t => t.ExternalId != null).GroupBy(t => t.ExternalId, StringComparer.Ordinal), g => Assert.Single(g));
    }

    [Fact]
    public async Task Boundary_without_booked_rows_fetches_everything()
    {
        await using var w = await BankWorld.CreateAsync();
        var (id, accountId) = await LinkedGiroAsync(w, "demo",
            Mm(1, -99, new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc), "Pending Thing", "Reserved", booked: 0));
        Assert.True((await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, true, false))).Succeeded);
        var rows = await w.TransactionsAsync(accountId);
        Assert.Equal(41, rows.Count);
        Assert.DoesNotContain(rows, t => !t.Booked);
    }

    [Fact]
    public async Task Later_syncs_never_reach_back_before_the_first_boundary()
    {
        await using var w = await BankWorld.CreateAsync();
        var (id, accountId) = await LinkedGiroAsync(w, "demo",
            Mm(1, -800, new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc), "Landlord", "Rent 2026-10 (MoneyMoney wording)"),
            Mm(1, -52.10, D1102, "GROCERY STORE GMBH", "Groceries 2026-11"));
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, true, false));
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, true, false));
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, true, false));
        // The October rent is still exactly one row; the bank's differently worded October booking was never pulled in.
        Assert.Equal(2, (await w.TransactionsAsync(accountId)).Count);
    }

    // ---- dedup ----

    [Fact]
    public async Task Repeated_syncs_do_not_duplicate_with_bank_ids()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice, "demo+dups");
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, false, false));
        await w.LinkAllAsync(BankWorld.Alice, id);
        var account = (await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId)).LinkedAccountId!.Value;
        for (var i = 0; i < 3; i++) Assert.True((await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, true, false))).Succeeded);
        var rows = await w.TransactionsAsync(account);
        Assert.Equal(43, rows.Count);
        Assert.Equal(2, rows.Count(t => t.Name == "Example Coffee Shop")); // legitimate identical same-day bookings
        Assert.Equal(rows.Count, rows.Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public async Task Repeated_syncs_do_not_duplicate_without_bank_ids_and_keep_identical_same_day_bookings()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice, "demo+noid+dups");
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, false, false));
        await w.LinkAllAsync(BankWorld.Alice, id);
        var account = (await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId)).LinkedAccountId!.Value;
        for (var i = 0; i < 3; i++) Assert.True((await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, true, false))).Succeeded);
        var rows = await w.TransactionsAsync(account);
        Assert.Equal(43, rows.Count);
        Assert.Equal(2, rows.Count(t => t.Name == "Example Coffee Shop"));
        Assert.All(rows, t => Assert.StartsWith("k:", t.ExternalId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Multiplicity_is_matched_by_count_when_a_bank_reports_more_identical_bookings_later()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice);
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, false, false));
        await w.LinkAllAsync(BankWorld.Alice, id);
        var account = (await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId)).LinkedAccountId!.Value;
        var store = new BankTransactionStore();
        var day = new DateOnly(2026, 6, 1);
        RemoteTransaction Coffee() => new(-3.5m, "EUR", day, day, "Cafe", "Coffee", true);

        async Task<int> ApplyAsync(int n)
        {
            await using var db = await w.T.System.CreateDbContextAsync();
            await store.ApplyAsync(db, account, new AccountRefresh(null, Enumerable.Range(0, n).Select(_ => Coffee()).ToList(), []), day, DateTime.UtcNow, CancellationToken.None);
            return await db.Transactions.CountAsync(t => t.AccountId == account);
        }

        Assert.Equal(2, await ApplyAsync(2));
        Assert.Equal(2, await ApplyAsync(2));
        Assert.Equal(3, await ApplyAsync(3)); // a third identical booking appeared
        Assert.Equal(3, await ApplyAsync(3));
    }

    [Fact]
    public async Task Split_parts_are_never_adopted_as_bank_rows()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice);
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, false, false));
        await w.LinkAllAsync(BankWorld.Alice, id);
        var account = (await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId)).LinkedAccountId!.Value;
        var store = new BankTransactionStore();
        var day = new DateOnly(2026, 6, 1);
        long partId;
        await using (var db = await w.T.System.CreateDbContextAsync())
        {
            await store.ApplyAsync(db, account, new AccountRefresh(null, [new(-7m, "EUR", day, day, "Example Market", "Weekly shop", true)], []), day, DateTime.UtcNow, CancellationToken.None);
            var parent = await db.Transactions.SingleAsync(t => t.AccountId == account);
            parent.Splitted = SplitState.Parent;
            partId = SourceScope.Namespace(parent.SourceId, BankTransactionStore.ConnectorRowBase + 1000);
            db.Transactions.Add(new Transaction
            {
                Id = partId,
                SourceId = parent.SourceId,
                AccountId = account,
                Amount = -350,
                Timestamp = parent.Timestamp,
                Splitted = SplitState.Child,
                SplitParentId = parent.Id,
                Name = parent.Name,
            });
            await db.SaveChangesAsync();
        }
        await using (var db = await w.T.System.CreateDbContextAsync())
        {
            // A new booking of the part's amount on the same day: inserted, the part stays a part.
            await store.ApplyAsync(db, account, new AccountRefresh(null,
                [new(-7m, "EUR", day, day, "Example Market", "Weekly shop", true), new(-3.5m, "EUR", day, day, "Cafe", "Coffee", true)], []),
                day, DateTime.UtcNow, CancellationToken.None);
            Assert.Null((await db.Transactions.SingleAsync(t => t.Id == partId)).ExternalId);
            Assert.Equal(2, await db.Transactions.CountAsync(t => t.AccountId == account && t.ExternalId != null));
        }
    }

    [Fact]
    public async Task Pending_rows_are_replaced_on_every_sync_and_booked_rows_are_kept()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice, "demo+pending");
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, false, false));
        await w.LinkAllAsync(BankWorld.Alice, id);
        var account = (await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId)).LinkedAccountId!.Value;

        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, true, false)); // the bank's first session: reservation only
        var first = await w.TransactionsAsync(account);
        var pending = Assert.Single(first, t => !t.Booked);
        Assert.Equal("Example Pending Payee", pending.Name);
        Assert.Equal(41 + 1, first.Count);
        var bookedBefore = first.Where(t => t.Booked).Select(t => t.Id).ToHashSet();

        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, true, false)); // now booked
        var second = await w.TransactionsAsync(account);
        Assert.DoesNotContain(second, t => !t.Booked);
        var nowBooked = Assert.Single(second, t => t.Name == "Example Pending Payee");
        Assert.True(nowBooked.Booked);
        Assert.Equal("id:G-2026-11-PENDING", nowBooked.ExternalId);
        Assert.Equal(42, second.Count);
        Assert.True(bookedBefore.IsSubsetOf(second.Select(t => t.Id).ToHashSet())); // booked rows kept as they were
    }

    [Fact]
    public async Task A_failing_account_keeps_the_others_going_and_shows_the_error()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice, "demo+syncerror");
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, false, false));
        await w.LinkAllAsync(BankWorld.Alice, id);
        var s = await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, true, false));
        Assert.False(s.Succeeded);
        Assert.Contains("temporarily unavailable", s.Error, StringComparison.Ordinal);

        var giro = await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId);
        var savings = await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.SavingsId);
        Assert.Contains("temporarily", giro.LastError, StringComparison.Ordinal);
        Assert.Null(savings.LastError);
        Assert.NotNull(savings.LastSuccessAt);
        Assert.Empty(await w.TransactionsAsync(giro.LinkedAccountId!.Value));
        Assert.Equal(10, (await w.TransactionsAsync(savings.LinkedAccountId!.Value)).Count);
        Assert.Contains("temporarily", (await w.AccountAsync(giro.LinkedAccountId!.Value)).Error, StringComparison.Ordinal);
        Assert.Contains("temporarily", (await w.For(BankWorld.Alice).GetLoginAsync(id))!.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Accounts_the_bank_no_longer_lists_are_closed_and_cannot_be_linked_or_get_statements()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice);
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, false, false));
        await using (var db = await w.T.System.CreateDbContextAsync())
        {
            db.BankLoginAccounts.Add(new BankLoginAccount { BankLoginId = id, RemoteId = "FAKE-OLD", Name = "Old closed account", StatementsEnabled = true });
            await db.SaveChangesAsync();
        }
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, false, false));
        var old = await w.RemoteAsync(BankWorld.Alice, id, "FAKE-OLD");
        Assert.True(old.Closed);
        Assert.False(old.StatementsEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.For(BankWorld.Alice).CreateAccountAsync(old.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.For(BankWorld.Alice).SetStatementsAsync(old.Id, true, StatementsInterval.Monthly));
    }

    [Fact]
    public async Task Deleting_a_login_keeps_data_reverts_providers_and_removes_statements()
    {
        await using var w = await BankWorld.CreateAsync();
        var accountId = await ImportGiroAsync(w);
        var id = await w.AddLoginAsync(BankWorld.Alice, "demo+statements");
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, false, false));
        var svc = w.For(BankWorld.Alice);
        await svc.LinkAsync((await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId)).Id, accountId);
        var savings = await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.SavingsId);
        var created = await svc.CreateAccountAsync(savings.Id);
        await svc.SetStatementsAsync((await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId)).Id, true, StatementsInterval.Daily);
        await w.SyncAsync(BankWorld.Alice, id);
        var files = Directory.GetFiles(w.StatementsDir, "*.pdf", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        var txCount = (await w.TransactionsAsync(accountId)).Count;

        await svc.DeleteLoginAsync(id);

        Assert.Null(await svc.GetLoginAsync(id));
        Assert.Equal(AccountProviders.MoneyMoney, (await w.AccountAsync(accountId)).Provider);
        Assert.Equal(AccountProviders.Manual, (await w.AccountAsync(created)).Provider);
        Assert.Equal(txCount, (await w.TransactionsAsync(accountId)).Count);
        Assert.Empty(Directory.GetFiles(w.StatementsDir, "*.pdf", SearchOption.AllDirectories));
        await using var db = await w.T.System.CreateDbContextAsync();
        Assert.Empty(await db.BankStatements.ToListAsync());
        Assert.Empty(await db.BankLoginAccounts.ToListAsync());
    }

    // ---- statements ----

    [Fact]
    public async Task Statements_are_off_by_default_and_need_a_link()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice, "demo+statements");
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, false, false));
        var giro = await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId);
        Assert.False(giro.StatementsEnabled);
        Assert.Equal(StatementsInterval.Monthly, giro.StatementsInterval);
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.For(BankWorld.Alice).SetStatementsAsync(giro.Id, true, StatementsInterval.Monthly));

        await w.LinkAllAsync(BankWorld.Alice, id);
        Assert.True((await w.SyncAsync(BankWorld.Alice, id)).Succeeded); // "Sync now": nothing switched on, nothing fetched
        Assert.Empty(await w.For(BankWorld.Alice).GetLoginStatementsAsync(id));

        await w.For(BankWorld.Alice).SetStatementsAsync(giro.Id, true, StatementsInterval.ManualOnly);
        // Unlinking switches it off again.
        await w.For(BankWorld.Alice).UnlinkAsync(giro.Id);
        Assert.False((await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId)).StatementsEnabled);
    }

    [Fact]
    public async Task Statements_download_once_per_identifier_and_account_less_documents_come_along()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice, "demo+statements");
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, false, false));
        await w.LinkAllAsync(BankWorld.Alice, id);
        var giro = await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId);
        var savings = await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.SavingsId);
        await w.For(BankWorld.Alice).SetStatementsAsync(giro.Id, true, StatementsInterval.ManualOnly);

        // "Download now" for one account, ignoring its (manual) interval; savings has statements off.
        Assert.Equal(StartResult.Started, await w.For(BankWorld.Alice).DownloadStatementsAsync(id, giro.Id));
        await w.Coordinator.WhenIdleAsync(id);
        Assert.True(w.Coordinator.GetStatus(BankWorld.Alice, id)!.Succeeded);

        var list = await w.For(BankWorld.Alice).GetLoginStatementsAsync(id);
        Assert.Equal(11, list.Count); // 10 monthly + the tax certificate without an account
        Assert.Single(list, s => s.AccountId == null);
        Assert.Equal(10, list.Count(s => s.AccountId == giro.LinkedAccountId));
        Assert.Empty(await w.For(BankWorld.Alice).GetAccountStatementsAsync(savings.LinkedAccountId!.Value));

        var files = Directory.GetFiles(w.StatementsDir, "*.pdf", SearchOption.AllDirectories);
        Assert.Equal(11, files.Length);
        Assert.All(files, f => Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(f), 0, 4), StringComparison.Ordinal));
        Assert.All(files, f => Assert.Contains(BankWorld.Alice, f, StringComparison.Ordinal)); // <root>/<owner>/<login>/...

        // Repeat: nothing is downloaded again.
        await w.SyncAsync(BankWorld.Alice, id);
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, false, true));
        Assert.Equal(11, (await w.For(BankWorld.Alice).GetLoginStatementsAsync(id)).Count);
        Assert.Equal(11, Directory.GetFiles(w.StatementsDir, "*.pdf", SearchOption.AllDirectories).Length);

        await using var db = await w.T.System.CreateDbContextAsync();
        var row = await db.BankStatements.FirstAsync();
        Assert.Equal(64, row.Sha256.Length);
        Assert.Equal(new FileInfo(w.StatementStore.Resolve(row.FilePath)).Length, row.SizeBytes);
        var link = await db.BankLoginAccounts.SingleAsync(a => a.Id == giro.Id);
        Assert.NotNull(link.LastStatementAt);
        Assert.NotNull(link.LastStatementsAttemptAt);
    }

    [Fact]
    public async Task Statements_follow_account_visibility_and_account_less_ones_stay_with_the_owner()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice, "demo+statements");
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, false, false));
        await w.LinkAllAsync(BankWorld.Alice, id);
        var giro = await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId);
        var savings = await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.SavingsId);
        await w.For(BankWorld.Alice).SetStatementsAsync(giro.Id, true, StatementsInterval.Daily);
        await w.SyncAsync(BankWorld.Alice, id);

        // Share a project that uses the giro account (and only it) with bob as viewer.
        var giroAccount = await w.AccountAsync(giro.LinkedAccountId!.Value);
        w.T.Db.Projects.Add(new Project { Id = 1, Name = "Shared", SourceId = giroAccount.SourceId, OwnerUserId = BankWorld.Alice, CreatedAt = DateTime.UtcNow });
        w.T.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = 1, AccountId = giroAccount.Id, Scope = AccountScope.Exclusive });
        w.T.Db.ProjectShares.Add(new ProjectShare { ProjectId = 1, UserId = BankWorld.Bob, Role = ProjectRole.Viewer });
        await w.T.Db.SaveChangesAsync();

        var all = await w.For(BankWorld.Alice).GetLoginStatementsAsync(id);
        var accountLess = all.Single(s => s.AccountId == null);
        var onAccount = all.First(s => s.AccountId == giroAccount.Id);

        var bob = w.For(BankWorld.Bob);
        var bobs = await bob.GetAccountStatementsAsync(giroAccount.Id);
        Assert.Equal(10, bobs.Count);
        Assert.NotNull(await bob.OpenStatementAsync(onAccount.Id));
        Assert.Null(await bob.OpenStatementAsync(accountLess.Id));
        Assert.Empty(await bob.GetAccountStatementsAsync(savings.LinkedAccountId!.Value)); // an account of the project he does not see
        Assert.Empty(await bob.GetLoginStatementsAsync(id)); // the login itself is private
        Assert.Empty(await bob.GetAccountLoginDocumentsAsync(giroAccount.Id)); // so are its documents without an account
        Assert.Equal([accountLess.Id], (await w.For(BankWorld.Alice).GetAccountLoginDocumentsAsync(giroAccount.Id)).Select(d => d.Id));

        // Carol has no share at all.
        var carol = w.For(BankWorld.Carol);
        Assert.Empty(await carol.GetAccountStatementsAsync(giroAccount.Id));
        Assert.Null(await carol.OpenStatementAsync(onAccount.Id));

        // Alice opens everything.
        Assert.NotNull(await w.For(BankWorld.Alice).OpenStatementAsync(accountLess.Id));
    }

    [Fact]
    public async Task A_shared_project_viewer_sees_connector_transactions_and_last_sync_but_no_login_details()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice);
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, false, false));
        await w.LinkAllAsync(BankWorld.Alice, id);
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, true, false));
        var giro = (await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId)).LinkedAccountId!.Value;
        var account = await w.AccountAsync(giro);
        w.T.Db.Projects.Add(new Project { Id = 1, Name = "Shared", SourceId = account.SourceId, OwnerUserId = BankWorld.Alice, CreatedAt = DateTime.UtcNow });
        w.T.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = 1, AccountId = giro, Scope = AccountScope.Exclusive });
        w.T.Db.ProjectShares.Add(new ProjectShare { ProjectId = 1, UserId = BankWorld.Bob, Role = ProjectRole.Viewer });
        await w.T.Db.SaveChangesAsync();

        await using var db = await w.View(BankWorld.Bob).Factory.CreateDbContextAsync();
        Assert.Equal(41, await db.Transactions.CountAsync(t => t.AccountId == giro));
        Assert.Equal(41, await db.Transactions.CountAsync()); // and nothing of the login's other accounts
        var seen = await db.Accounts.SingleAsync(a => a.Id == giro);
        Assert.NotNull(seen.RefreshTimestamp);
        Assert.Null(seen.Error);
        Assert.Empty(await db.BankLogins.ToListAsync());
        Assert.Empty(await db.BankLoginAccounts.ToListAsync());
        Assert.Empty(await w.For(BankWorld.Bob).GetLoginsAsync());
    }
}
