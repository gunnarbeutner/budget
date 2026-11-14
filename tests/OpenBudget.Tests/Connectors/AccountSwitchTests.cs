#pragma warning disable CA2000 // test objects live for the test
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.FakeBank;
using OpenBudget.Connectors.Sync;
using OpenBudget.Importers.MoneyMoney;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>Linking an imported account to its bank connection: cut-over, dedupe, carry-over, new markers, one-time import.</summary>
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

    /// <summary>Imports the history, links the giro and syncs it once.</summary>
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
    public async Task Linking_makes_the_account_the_connectors_and_its_first_sync_continues_the_imported_history()
    {
        await using var w = await BankWorld.CreateAsync();
        await w.ImportMoneyMoneyAsync(Snapshot(w));
        Assert.Equal(AccountProviders.Manual, (await w.AccountAsync(Giro)).Provider);
        long rentId;
        await using (var db = await w.T.System.CreateDbContextAsync())
        {
            var rent = await db.Transactions.SingleAsync(t => t.AccountId == Giro && t.Purpose == "MIETE OKTOBER");
            rent.Note = "October rent";
            rentId = rent.Id;
            await db.SaveChangesAsync();
        }
        var loginId = await w.AddLoginAsync(BankWorld.Alice);
        await w.SyncAsync(BankWorld.Alice, loginId, new SyncRequest(loginId, false, false));
        var linkId = (await w.RemoteAsync(BankWorld.Alice, loginId, FakeBankConnector.GiroId)).Id;

        await w.For(BankWorld.Alice).LinkAsync(linkId, Giro);
        Assert.Equal("fake", (await w.AccountAsync(Giro)).Provider);
        Assert.NotNull((await w.RemoteAsync(BankWorld.Alice, loginId, FakeBankConnector.GiroId)).SwitchedAt);
        Assert.True((await w.SyncAsync(BankWorld.Alice, loginId, new SyncRequest(loginId, true, false))).Succeeded);

        var rows = await w.TransactionsAsync(Giro);
        Assert.All(rows, t => Assert.False(t.IsNew));
        Assert.All(rows.Where(t => t.ExternalId != null).GroupBy(t => t.ExternalId, StringComparer.Ordinal), g => Assert.Single(g));
        Assert.Single(rows, t => t.ExternalId == "id:G-2026-11-FOOD");
        Assert.Equal("October rent", rows.Single(t => t.Id == rentId).Note);
        Assert.Contains(rows, t => t.Name == "VERMIETER" && t.Purpose == "MIETE SEPTEMBER");
        Assert.Equal(EditBlock.None, TransactionEditRules.Check(null, BankWorld.Alice, Giro, "fake"));
    }

    [Fact]
    public async Task Later_syncs_of_a_linked_imported_account_mark_only_arrivals_new()
    {
        await using var w = await BankWorld.CreateAsync();
        var (loginId, _) = await LinkedAndFetchedAsync(w);

        // A booking that arrives with a later sync is new.
        await using (var db = await w.T.System.CreateDbContextAsync())
            await db.Transactions.Where(t => t.AccountId == Giro && t.ExternalId == "id:G-2026-11-FOOD").ExecuteDeleteAsync();
        Assert.True((await w.SyncAsync(BankWorld.Alice, loginId, new SyncRequest(loginId, true, false))).Succeeded);
        var rows = await w.TransactionsAsync(Giro);
        Assert.True(Assert.Single(rows, t => t.ExternalId == "id:G-2026-11-FOOD").IsNew);
        Assert.Single(rows, t => t.IsNew);
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
    public async Task Unlinking_leaves_an_imported_account_manual_with_its_history()
    {
        await using var w = await BankWorld.CreateAsync();
        var (_, linkId) = await LinkedAndFetchedAsync(w);
        var rows = (await w.TransactionsAsync(Giro)).Count;

        await w.For(BankWorld.Alice).UnlinkAsync(linkId);
        Assert.Equal(AccountProviders.Manual, (await w.AccountAsync(Giro)).Provider);
        Assert.Equal(rows, (await w.TransactionsAsync(Giro)).Count);
    }

    [Fact]
    public async Task The_import_runs_once()
    {
        await using var w = await BankWorld.CreateAsync();
        await w.ImportMoneyMoneyAsync(Snapshot(w));
        var rows = (await w.TransactionsAsync(Giro)).Count;

        await Assert.ThrowsAsync<MoneyMoneyAlreadyImportedException>(() => w.ImportMoneyMoneyAsync(w.BuildSnapshot([("Other", null)], [Mm(-1, Day(11, 5), "Something", "Else")])));
        Assert.Equal(rows, (await w.TransactionsAsync(Giro)).Count);
        Assert.Equal("MM Giro", (await w.AccountAsync(Giro)).Name);
    }
}
