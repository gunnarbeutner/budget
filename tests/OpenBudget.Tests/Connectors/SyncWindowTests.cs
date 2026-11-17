using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.FakeBank;
using OpenBudget.Connectors.Sync;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>How far back a sync compares an account's bookings.</summary>
public sealed class SyncWindowTests
{
    [Fact]
    public async Task Bookings_that_miss_the_banks_balance_are_compared_again_from_the_floor()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice);
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, false, false));
        await w.LinkAllAsync(BankWorld.Alice, id);
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, true, false));
        var accountId = (await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId)).LinkedAccountId!.Value;

        await using var db = await w.T.System.CreateDbContextAsync();
        var account = await db.Accounts.SingleAsync(a => a.Id == accountId);
        var link = await db.BankLoginAccounts.SingleAsync(l => l.LinkedAccountId == accountId);
        var newest = await db.Transactions.Where(t => t.AccountId == accountId && t.Booked).MaxAsync(t => t.Timestamp);
        var newestDay = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(newest).UtcDateTime);
        link.SyncFloorUtc = newestDay.AddDays(-60).ToDateTime(TimeOnly.MinValue);
        var booked = await db.Transactions.Where(t => t.AccountId == accountId && t.Booked && t.Splitted != OpenBudget.Models.SplitState.Child).SumAsync(t => t.Amount);
        account.InitialBalance = (long)Math.Round(account.CurrentBalance!.Value * 100m) - booked;

        Assert.Equal(newestDay.AddDays(-7), await BankSyncRunner.ComputeSinceAsync(db, link, account, CancellationToken.None));

        account.InitialBalance -= 1234;
        Assert.Equal(newestDay.AddDays(-60), await BankSyncRunner.ComputeSinceAsync(db, link, account, CancellationToken.None));
    }
}
