using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;
using OpenBudget.Services.DataFixes;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// Automatic and manual pairing of transfers between an owner's own accounts, and paired bookings counting as
/// transfers. All data is made up.
/// </summary>
public sealed class TransferPairingTests
{
    private const string GiroIban = "DE00 1111 2222 3333 4444 55";
    private const string SavingsIban = "DE00 9999 8888 7777 6666 55";
    private static readonly DateOnly Day = new(2026, 11, 10);

    private static TransferCandidate Row(long id, long account, long cents, int dayOffset = 0, string? iban = null, string? name = null, string? purpose = null) =>
        new(id, account, cents, "EUR", Day.AddDays(dayOffset), name, purpose, iban);

    private static Dictionary<long, TransferAccount> Accounts(string? giroIban = GiroIban, string? savingsIban = SavingsIban) => new()
    {
        [1] = new(1, giroIban, "Giro"),
        [2] = new(2, savingsIban, "Tagesgeld"),
        [3] = new(3, null, "Reisekasse"),
    };

    [Fact]
    public void Opposite_equal_amounts_with_the_other_accounts_IBAN_pair()
    {
        var pairs = TransferPairing.Match([Row(10, 1, -5000, iban: SavingsIban), Row(11, 2, 5000, 1, iban: GiroIban)], Accounts());

        Assert.Equal([(10L, 11L)], pairs);
    }

    [Fact]
    public void Amounts_must_match_exactly_and_lie_on_different_accounts()
    {
        Assert.Empty(TransferPairing.Match([Row(10, 1, -5000, iban: SavingsIban), Row(11, 2, 5001, iban: GiroIban)], Accounts()));
        Assert.Empty(TransferPairing.Match([Row(10, 1, -5000, iban: GiroIban), Row(11, 1, 5000, iban: GiroIban)], Accounts()));
        Assert.Empty(TransferPairing.Match(
            [Row(10, 1, -5000, iban: SavingsIban), Row(11, 2, 5000, iban: GiroIban) with { Currency = "USD" }], Accounts()));
    }

    [Fact]
    public void Bookings_further_apart_than_the_window_stay_apart()
    {
        Assert.Single(TransferPairing.Match([Row(10, 1, -5000, iban: SavingsIban), Row(11, 2, 5000, TransferPairing.MaxDaysApart)], Accounts()));
        Assert.Empty(TransferPairing.Match([Row(10, 1, -5000, iban: SavingsIban), Row(11, 2, 5000, TransferPairing.MaxDaysApart + 1)], Accounts()));
    }

    [Fact]
    public void Without_supporting_evidence_nothing_pairs()
    {
        Assert.Empty(TransferPairing.Match([Row(10, 1, -5000, name: "Shop"), Row(11, 3, 5000, name: "Refund")], Accounts()));
    }

    [Fact]
    public void A_counterparty_IBAN_of_someone_else_contradicts_the_pair()
    {
        // The incoming side names the giro, but the outgoing side went to a stranger.
        Assert.Empty(TransferPairing.Match(
            [Row(10, 1, -5000, iban: "DE00 5555 5555 5555 5555 55"), Row(11, 2, 5000, iban: GiroIban)], Accounts()));
        // An account without IBAN cannot be the other side of a booking that names another own account.
        Assert.Empty(TransferPairing.Match([Row(10, 1, -5000, iban: SavingsIban, purpose: "Reisekasse"), Row(11, 3, 5000)], Accounts()));
    }

    [Fact]
    public void Naming_the_other_account_in_the_purpose_supports_a_pair()
    {
        var pairs = TransferPairing.Match([Row(10, 1, -2000, purpose: "Umbuchung auf Reisekasse"), Row(11, 3, 2000)], Accounts());
        Assert.Equal([(10L, 11L)], pairs);

        // Only as a whole word.
        Assert.Empty(TransferPairing.Match([Row(10, 1, -2000, purpose: "Reisekassenbon"), Row(11, 3, 2000)], Accounts()));
    }

    [Fact]
    public void Several_equally_good_candidates_pair_none()
    {
        var pairs = TransferPairing.Match(
            [Row(10, 1, -5000, iban: SavingsIban), Row(11, 2, 5000, iban: GiroIban), Row(12, 2, 5000, 2, iban: GiroIban)], Accounts());

        Assert.Empty(pairs);
    }

    [Fact]
    public void A_better_supported_candidate_wins_over_a_weaker_one()
    {
        var pairs = TransferPairing.Match(
            [Row(10, 1, -5000, iban: SavingsIban, purpose: "Reisekasse"), Row(11, 2, 5000), Row(12, 3, 5000)], Accounts());

        Assert.Equal([(10L, 11L)], pairs);
    }

    private static async Task<TestDb> SeedAsync()
    {
        var t = new TestDb();
        t.AddAccount(1, "Giro").Iban = GiroIban;
        t.AddAccount(2, "Tagesgeld").Iban = SavingsIban;
        await t.Db.SaveChangesAsync();
        return t;
    }

    private static Transaction Add(TestDb t, long id, long account, long cents, int dayOffset = 0, string? iban = null, bool booked = true)
    {
        var row = new Transaction
        {
            Id = id,
            OwnerUserId = TestDb.DefaultOwner,
            AccountId = account,
            Amount = cents,
            Booked = booked,
            Name = "Transfer",
            CounterpartyIban = iban,
            Timestamp = new DateTimeOffset(Day.AddDays(dayOffset).ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero).ToUnixTimeSeconds(),
        };
        t.Db.Transactions.Add(row);
        return row;
    }

    private static async Task<Dictionary<long, Transaction>> RowsAsync(TestDb t)
    {
        await using var db = await t.Factory.CreateDbContextAsync();
        return await db.Transactions.AsNoTracking().ToDictionaryAsync(r => r.Id);
    }

    [Fact]
    public async Task Pairing_stores_the_pair_on_both_bookings()
    {
        await using var t = await SeedAsync();
        Add(t, 10, 1, -5000, iban: SavingsIban);
        Add(t, 11, 2, 5000, 1, iban: GiroIban);
        Add(t, 12, 1, -700);
        await t.Db.SaveChangesAsync();

        await using (var db = await t.Factory.CreateDbContextAsync())
            Assert.Equal(1, await TransferPairing.PairAsync(db, TestDb.DefaultOwner, default));

        var rows = await RowsAsync(t);
        Assert.Equal(11, rows[10].TransferPeerId);
        Assert.Equal(10, rows[11].TransferPeerId);
        Assert.Null(rows[12].TransferPeerId);
    }

    [Fact]
    public async Task Pending_bookings_pair_only_once_booked()
    {
        await using var t = await SeedAsync();
        Add(t, 10, 1, -5000, iban: SavingsIban);
        var pending = Add(t, 11, 2, 5000, iban: GiroIban, booked: false);
        await t.Db.SaveChangesAsync();

        await using (var db = await t.Factory.CreateDbContextAsync())
            Assert.Equal(0, await TransferPairing.PairAsync(db, TestDb.DefaultOwner, default));

        // The bank replaces the pending row with a booked one.
        t.Db.Transactions.Remove(pending);
        Add(t, 12, 2, 5000, 1, iban: GiroIban);
        await t.Db.SaveChangesAsync();
        await using (var db = await t.Factory.CreateDbContextAsync())
            Assert.Equal(1, await TransferPairing.PairAsync(db, TestDb.DefaultOwner, default));
        Assert.Equal(12, (await RowsAsync(t))[10].TransferPeerId);
    }

    [Fact]
    public async Task A_pair_whose_other_side_is_gone_is_dropped()
    {
        await using var t = await SeedAsync();
        Add(t, 10, 1, -5000, iban: SavingsIban).TransferPeerId = 99;
        await t.Db.SaveChangesAsync();

        await using (var db = await t.Factory.CreateDbContextAsync())
            await TransferPairing.PairAsync(db, TestDb.DefaultOwner, default);

        Assert.Null((await RowsAsync(t))[10].TransferPeerId);
    }

    [Fact]
    public async Task Unpairing_is_sticky_and_manual_pairs_stay()
    {
        await using var t = await SeedAsync();
        Add(t, 10, 1, -5000, iban: SavingsIban);
        Add(t, 11, 2, 5000, iban: GiroIban);
        Add(t, 20, 1, -1234);
        Add(t, 21, 2, 1300, 9);
        await t.Db.SaveChangesAsync();
        await using (var db = await t.Factory.CreateDbContextAsync())
            await TransferPairing.PairAsync(db, TestDb.DefaultOwner, default);

        var service = new TransferPairService(t.ForUser(TestDb.DefaultOwner).Factory, t.Notifier);
        Assert.Equal(11, (await service.GetCounterpartAsync(10))?.Id);
        await service.UnpairAsync(10);
        await service.PairAsync(20, 21);
        await using (var db = await t.Factory.CreateDbContextAsync())
            Assert.Equal(0, await TransferPairing.PairAsync(db, TestDb.DefaultOwner, default));

        var rows = await RowsAsync(t);
        Assert.Null(rows[10].TransferPeerId);
        Assert.Null(rows[11].TransferPeerId);
        Assert.Equal(TransferPairState.Declined, rows[11].TransferState);
        Assert.Equal(21, rows[20].TransferPeerId);
        Assert.Equal(TransferPairState.Manual, rows[21].TransferState);
        Assert.Equal([11L], (await service.GetCandidatesAsync(10)).Select(c => c.Id));
    }

    [Fact]
    public async Task Only_the_owner_changes_a_pairing()
    {
        await using var t = await SeedAsync();
        t.AddUser("u-other", "other");
        Add(t, 10, 1, -5000);
        Add(t, 11, 2, 5000);
        await t.Db.SaveChangesAsync();

        var service = new TransferPairService(t.ForUser("u-other").Factory, t.Notifier);
        await Assert.ThrowsAnyAsync<Exception>(() => service.PairAsync(10, 11));
        Assert.Null((await RowsAsync(t))[10].TransferPeerId);
    }

    [Fact]
    public async Task A_paired_booking_without_category_is_not_uncategorized()
    {
        await using var t = await SeedAsync();
        Add(t, 10, 1, -5000, iban: SavingsIban);
        Add(t, 11, 2, 5000, iban: GiroIban);
        Add(t, 12, 1, -700);
        await t.Db.SaveChangesAsync();
        await using (var db = await t.Factory.CreateDbContextAsync())
            await TransferPairing.PairAsync(db, TestDb.DefaultOwner, default);

        var page = await t.TransactionQueries.QueryAsync(new TransactionQuery { OwnerUserId = TestDb.DefaultOwner, UncategorizedOnly = true, Take = 50 });
        Assert.Equal([12L], page.Rows.Select(r => r.Id));
        var all = await t.TransactionQueries.QueryAsync(new TransactionQuery
        {
            OwnerUserId = TestDb.DefaultOwner,
            IncludeUncategorized = true,
            CategoryLeafIds = [],
            Take = 50,
        });
        Assert.Equal([12L], all.Rows.Select(r => r.Id));
    }

    [Fact]
    public async Task Subscription_detection_skips_paired_bookings()
    {
        await using var t = new TestDb();
        t.AddProject(1);
        t.AddAccount(1, "Giro");
        t.AddAccount(2, "Depotkasse");
        t.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = 1, AccountId = 1, Scope = AccountScope.Exclusive });
        var today = new DateOnly(2026, 11, 15);
        for (var m = 0; m < 6; m++)
        {
            var ts = new DateTimeOffset(new DateOnly(2026, 11, 1).AddMonths(-m).ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero).ToUnixTimeSeconds();
            t.Db.Transactions.Add(new Transaction
            {
                Id = 100 + m,
                OwnerUserId = TestDb.DefaultOwner,
                AccountId = 1,
                Amount = -20000,
                Name = "Example Savings Plan",
                Timestamp = ts,
                TransferPeerId = 200 + m,
            });
            t.Db.Transactions.Add(new Transaction
            {
                Id = 200 + m,
                OwnerUserId = TestDb.DefaultOwner,
                AccountId = 2,
                Amount = 20000,
                Name = "Example Savings Plan",
                Timestamp = ts,
                TransferPeerId = 100 + m,
            });
        }
        await t.Db.SaveChangesAsync();

        Assert.Empty(await new RecurringPaymentService(t.Factory, t.Hierarchy, t.Projects).GetAsync(1, today));

        // A category the user gives the paired bookings wins: they count as that category again.
        t.AddGroup(300);
        t.AddLeaf(301, 300, "Media");
        await t.Db.SaveChangesAsync();
        await t.Db.Transactions.Where(r => r.AccountId == 1).ExecuteUpdateAsync(u => u.SetProperty(r => r.CategoryId, 301L));
        Assert.Single(await new RecurringPaymentService(t.Factory, t.Hierarchy, t.Projects).GetAsync(1, today));
    }

    [Fact]
    public void The_data_fix_runs_before_the_fixes_that_wait_for_secrets()
    {
        var names = DataFixList.All.Select(f => f.Name).ToList();
        Assert.True(names.IndexOf("2026-11-transfer-pairs") is var i && i >= 0 && i < names.IndexOf("2026-11-background-defaults"));
    }

    [Fact]
    public async Task The_data_fix_pairs_every_owners_history()
    {
        await using var t = await SeedAsync();
        Add(t, 10, 1, -5000, -400, iban: SavingsIban);
        Add(t, 11, 2, 5000, -399, iban: GiroIban);
        await t.Db.SaveChangesAsync();

        var fix = DataFixList.All.Single(f => f.Name == "2026-11-transfer-pairs");
        await using (var db = new BudgetDbContext(new DbContextOptionsBuilder<BudgetDbContext>().UseSqlite(t.Connection).Options))
            await fix.RunAsync(new DataFixContext(db, null!, DateTime.UtcNow, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, default));

        Assert.Equal(11, (await RowsAsync(t))[10].TransferPeerId);
    }
}
