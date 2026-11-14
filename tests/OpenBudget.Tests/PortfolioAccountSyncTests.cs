using OpenBudget.Connectors.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBudget.Models;
using OpenBudget.PortfolioPerformance.Proto;
using OpenBudget.Services;
using Xunit;
using static OpenBudget.Tests.PortfolioFixture;
using PType = OpenBudget.PortfolioPerformance.Proto.PTransaction.Types.Type;

namespace OpenBudget.Tests;

/// <summary>
/// PortfolioPerformance portfolios as accounts: created from an upload, recomputed idempotently, switched off and on,
/// and taking over the accounts the earlier MoneyMoney extension made.
/// </summary>
public sealed class PortfolioAccountSyncTests : IAsyncDisposable
{
    private const string Owner = TestDb.DefaultOwner;
    private const string Source = Owner;

    private readonly TestDb _t = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ob-pp-{Guid.NewGuid():N}");
    private readonly UploadedPortfolioFileSource _files;
    private readonly PortfolioAccountSync _sync;
    private readonly PortfolioPerformanceImportService _import;

    public PortfolioAccountSyncTests()
    {
        _files = new UploadedPortfolioFileSource(Path.Combine(_dir, "files"));
        _sync = new PortfolioAccountSync(_t.System, _files, _t.Notifier, new FixedClock(), NullLogger<PortfolioAccountSync>.Instance);
        _import = new PortfolioPerformanceImportService(_t.System, _t.Notifier, NullLogger<PortfolioPerformanceImportService>.Instance, _files, _sync);
    }

    public async ValueTask DisposeAsync()
    {
        await _t.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2024, 1, 10, 12, 0, 0, TimeSpan.Zero);
    }

    private PortfolioAccountSettingsService Settings() => new(_t.ForUser(Owner).Factory, _sync, _files);

    private async Task UploadAsync(PClient client)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.portfolio");
        await File.WriteAllBytesAsync(path, Pack(client));
        await _import.ImportFromFileAsync(Owner, path, CancellationToken.None);
    }

    private async Task<PpPortfolioSetting> SettingAsync(string uuid)
    {
        await using var db = await _t.System.CreateDbContextAsync();
        return await db.PpPortfolioSettings.AsNoTracking().SingleAsync(s => s.PpPortfolioUuid == uuid);
    }

    private async Task<Account> AccountAsync(long id)
    {
        await using var db = await _t.System.CreateDbContextAsync();
        return await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == id);
    }

    private async Task<List<Transaction>> LedgerAsync(long accountId)
    {
        await using var db = await _t.System.CreateDbContextAsync();
        return await db.Transactions.AsNoTracking().Where(t => t.AccountId == accountId).OrderBy(t => t.Id).ToListAsync();
    }

    [Fact]
    public async Task An_upload_creates_a_depot_and_a_ledger_account_per_shown_portfolio()
    {
        // Broker is linked to a bank depot (off by default), Old is retired (off by default).
        _t.AddAccount(500, "Bank depot").Portfolio = 1;
        _t.Db.PpPortfolioLinks.Add(new PpPortfolioLink { OwnerUserId = Source, PpPortfolioUuid = "pf-b", AccountId = 500 });
        var project = _t.AddProject(7, "Household");
        project.OwnerUserId = Owner;
        project.IsDefaultForNewAccounts = true;
        await _t.Db.SaveChangesAsync();

        await UploadAsync(Client());

        await using var db = await _t.System.CreateDbContextAsync();
        Assert.Equal(["pf-a"], await db.PpPortfolioSettings.Select(s => s.PpPortfolioUuid).ToListAsync());
        var setting = await SettingAsync("pf-a");
        var depot = await AccountAsync(setting.DepotAccountId!.Value);
        var ledger = await AccountAsync(setting.LedgerAccountId!.Value);

        Assert.Equal(Owner, depot.OwnerUserId);
        Assert.True(depot.Id > RowIds.Namespace(1, BankTransactionStore.ConnectorRowBase));
        Assert.Equal(("Vault", 7L, 1L, AccountProviders.PortfolioPerformance), (depot.Name, depot.Type!.Value, depot.Portfolio!.Value, depot.Provider));
        Assert.Equal(("Vault (Verrechnung)", AccountProviders.PortfolioPerformance), (ledger.Name, ledger.Provider));
        Assert.Equal(1020m, depot.CurrentBalance);

        var holdings = await db.Holdings.Where(h => h.AccountId == depot.Id).OrderByDescending(h => h.Amount).ToListAsync();
        Assert.Equal(["Gold", "Example ETF", "Real estate"], holdings.Select(h => h.Name));
        Assert.Equal((10.0, 62.0, 620.0, 53.0), (holdings[0].Quantity, holdings[0].Price!.Value, holdings[0].Amount!.Value, holdings[0].Purchase!.Value));
        Assert.Equal(("IE00EXAMPLE1", "USD"), (holdings[1].Isin, holdings[1].PriceCurrency));
        Assert.Equal("sec-re", holdings[2].PpSecurityUuid);

        // Value history: a total per day (Jan 2..10) plus a line per held security with history (Gold 9 days, ETF 8).
        var chart = await db.PortfolioChartPoints.Where(p => p.AccountId == depot.Id).ToListAsync();
        Assert.Equal(26, chart.Count);
        var totals = chart.Where(p => p.SecurityId == null).OrderBy(p => p.Timestamp).ToList();
        Assert.Equal(Day(1, 2) * 86400 + 43200, totals[0].Timestamp);
        Assert.Equal([500.0, 950, 1225, 1300, 1120, 1000, 1000, 1020, 1020], totals.Select(p => p.Balance!.Value));
        Assert.All(chart.Where(p => p.SecurityId != null), p => Assert.Contains(holdings, h => h.Id == p.SecurityId));

        var rows = await LedgerAsync(ledger.Id);
        Assert.Equal(11, rows.Count);
        var buy = rows.Single(r => r.ExternalId == "pp:t-buy1");
        Assert.Equal((-50000L, "Kauf", "Kauf: Gold (10)"), (buy.Amount, buy.Name, buy.Purpose));
        Assert.Equal(-300L, rows.Single(r => r.ExternalId == "pp:t-sell:tax").Amount);
        Assert.Equal(672m, ledger.CurrentBalance);   // the reference cash account: 1000 deposited − 510 + 175 + 8 − 1
        Assert.All(rows, r => Assert.False(r.IsNew));   // the first fill is not "unread"

        // New accounts land in the default project for new accounts.
        Assert.Equal(2, await db.ProjectAccountAssignments.CountAsync(a => a.ProjectId == 7 && (a.AccountId == depot.Id || a.AccountId == ledger.Id)));
    }

    [Fact]
    public async Task Reuploads_keep_ids_and_user_edits_and_drop_what_was_deleted()
    {
        await UploadAsync(Client());
        var setting = await SettingAsync("pf-a");
        var ledgerId = setting.LedgerAccountId!.Value;
        var before = await LedgerAsync(ledgerId);
        _t.AddGroup(9000);
        _t.AddLeaf(9001, 9000, "Precious metals");
        var buy = _t.Db.Transactions.Single(t => t.ExternalId == "pp:t-buy1");
        buy.CategoryId = 9001;
        buy.CategorySetBy = TransactionCategorySource.Manual;
        buy.Note = "bar from the dealer";
        await _t.Db.SaveChangesAsync();

        await UploadAsync(Client());
        var again = await LedgerAsync(ledgerId);
        Assert.Equal(before.Select(t => (t.Id, t.ExternalId)), again.Select(t => (t.Id, t.ExternalId)));
        var kept = again.Single(t => t.ExternalId == "pp:t-buy1");
        Assert.Equal((9001L, "bar from the dealer"), (kept.CategoryId!.Value, kept.Note));
        Assert.Equal(setting.DepotAccountId, (await SettingAsync("pf-a")).DepotAccountId);
        await using (var db = await _t.System.CreateDbContextAsync())
        {
            Assert.Equal(3, await db.Holdings.CountAsync(h => h.AccountId == setting.DepotAccountId));
            Assert.Equal(26, await db.PortfolioChartPoints.CountAsync(p => p.AccountId == setting.DepotAccountId));
        }

        // The second purchase is deleted in PortfolioPerformance; a new dividend arrives.
        var client = Client();
        client.Transactions.Remove(client.Transactions.Single(t => t.Uuid == "t-buy2"));
        client.Transactions.Add(Tx("t-div2", OpenBudget.PortfolioPerformance.Proto.PTransaction.Types.Type.Dividend, "2024-01-09", 4m, "sec-gold", account: "acc-1"));
        await UploadAsync(client);

        var last = await LedgerAsync(ledgerId);
        Assert.DoesNotContain(last, t => t.ExternalId == "pp:t-buy2");
        Assert.Equal(kept.Id, last.Single(t => t.ExternalId == "pp:t-buy1").Id);
        var fresh = last.Single(t => t.ExternalId == "pp:t-div2");
        Assert.True(fresh.IsNew);
        Assert.Equal(400L, fresh.Amount);
        await using (var db = await _t.System.CreateDbContextAsync())
        {
            var gold = await db.Holdings.SingleAsync(h => h.AccountId == setting.DepotAccountId && h.Name == "Gold");
            Assert.Equal(5.0, gold.Quantity, 9);
        }
    }

    [Fact]
    public async Task Switching_a_portfolio_off_removes_the_accounts_it_created_and_on_again_creates_them_afresh()
    {
        await UploadAsync(Client());
        var setting = await SettingAsync("pf-a");
        var settings = Settings();

        await settings.SetShowAsync(Source, "pf-a", false);
        var off = await SettingAsync("pf-a");
        Assert.Equal((null, null), (off.DepotAccountId, off.LedgerAccountId));
        await using (var db = await _t.System.CreateDbContextAsync())
        {
            Assert.False(await db.Accounts.AnyAsync(a => a.Id == setting.DepotAccountId || a.Id == setting.LedgerAccountId));
            Assert.False(await db.Transactions.AnyAsync(t => t.AccountId == setting.LedgerAccountId));
            Assert.False(await db.Holdings.AnyAsync(h => h.AccountId == setting.DepotAccountId));
        }

        await settings.SetShowAsync(Source, "pf-a", true);
        var on = await SettingAsync("pf-a");
        Assert.Equal(AccountProviders.PortfolioPerformance, (await AccountAsync(on.DepotAccountId!.Value)).Provider);
        Assert.Equal(11, (await LedgerAsync(on.LedgerAccountId!.Value)).Count);

        // Broker is off by default (linked to nothing here, but switched on explicitly it gets accounts).
        await settings.SetShowAsync(Source, "pf-b", true);
        var broker = await SettingAsync("pf-b");
        Assert.Equal("Broker", (await AccountAsync(broker.DepotAccountId!.Value)).Name);
    }

    [Fact]
    public async Task Existing_accounts_of_the_same_name_wait_for_a_takeover_that_keeps_their_history()
    {
        // What the MoneyMoney extension left: a depot and a ledger with a trade, a funding transfer and value history.
        var depot = _t.AddAccount(10, "Vault");
        depot.Portfolio = 1;
        depot.Type = 7;
        _t.AddAccount(11, "Vault (Verrechnung)").Type = 0;
        _t.AddGroup(9000);
        _t.AddLeaf(9001, 9000, "Investments");
        var buyTs = new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        _t.Db.Transactions.Add(new Transaction { Id = 100, OwnerUserId = Source, AccountId = 11, Amount = -50000, Timestamp = buyTs, Name = "Kauf", Purpose = "Kauf: Gold (10)", CategoryId = 9001 });
        _t.Db.Transactions.Add(new Transaction { Id = 101, OwnerUserId = Source, AccountId = 11, Amount = 100000, Timestamp = buyTs - 86400, Name = "Transfer from giro" });
        _t.Db.PortfolioChartPoints.Add(new PortfolioChartPoint { Id = 200, OwnerUserId = Source, AccountId = 10, Timestamp = Day(12, 1) - 366 * 86400 + 43200, Balance = 42 });
        _t.Db.PortfolioChartPoints.Add(new PortfolioChartPoint { Id = 201, OwnerUserId = Source, AccountId = 10, Timestamp = Day(1, 5) * 86400 + 43200, Balance = 1 });
        _t.Db.Holdings.Add(new Holding { Id = 300, OwnerUserId = Source, AccountId = 10, Name = "Gold", Quantity = 9 });
        await _t.Db.SaveChangesAsync();

        await UploadAsync(Client());

        // Nothing is created while the takeover is undecided.
        Assert.Null((await SettingAsync("pf-a")).DepotAccountId);
        var row = (await Settings().GetAsync(Source))!.Portfolios.Single(p => p.Uuid == "pf-a");
        Assert.Equal(("Vault", "Vault (Verrechnung)"), (row.PendingTakeover!.DepotName, row.PendingTakeover.LedgerName));

        await Settings().TakeOverAsync(Source, "pf-a");

        var setting = await SettingAsync("pf-a");
        Assert.Equal((10L, 11L, true), (setting.DepotAccountId!.Value, setting.LedgerAccountId!.Value, setting.TakenOver));
        Assert.Equal(AccountProviders.PortfolioPerformance, (await AccountAsync(10)).Provider);
        Assert.Equal(AccountProviders.PortfolioPerformance, (await AccountAsync(11)).Provider);

        var rows = await LedgerAsync(11);
        Assert.Equal(12, rows.Count);                                     // 11 from PortfolioPerformance + the funding transfer
        var adopted = rows.Single(t => t.Id == 100);                      // the old booking is the PortfolioPerformance purchase
        Assert.Equal(("pp:t-buy1", 9001L), (adopted.ExternalId, adopted.CategoryId!.Value));
        Assert.Contains(rows, t => t.Id == 101 && t.ExternalId == null);
        Assert.Equal(672m, (await AccountAsync(11)).CurrentBalance);     // the reference cash account's balance, whatever the old rows

        await using var db = await _t.System.CreateDbContextAsync();
        var chart = await db.PortfolioChartPoints.Where(p => p.AccountId == 10).ToListAsync();
        Assert.Contains(chart, p => p.Id == 200);                         // history before PortfolioPerformance's first day stays
        Assert.DoesNotContain(chart, p => p.Id == 201);                   // from then on PortfolioPerformance decides
        Assert.Equal(27, chart.Count);
        Assert.Equal(3, await db.Holdings.CountAsync(h => h.AccountId == 10));
        Assert.False(await db.Holdings.AnyAsync(h => h.Id == 300));
        Assert.Equal(1, await db.Accounts.CountAsync(a => a.Name == "Vault"));   // no second account was made
    }

    [Fact]
    public async Task Declining_the_takeover_creates_new_accounts_and_leaves_the_old_ones_alone()
    {
        _t.AddAccount(10, "Vault").Portfolio = 1;
        await _t.Db.SaveChangesAsync();
        await UploadAsync(Client());

        await Settings().DeclineTakeoverAsync(Source, "pf-a");

        var setting = await SettingAsync("pf-a");
        Assert.NotEqual(10L, setting.DepotAccountId!.Value);
        Assert.False(setting.TakenOver);
        Assert.Equal(AccountProviders.MoneyMoney, (await AccountAsync(10)).Provider);
    }

    [Fact]
    public async Task A_portfolio_deleted_in_PortfolioPerformance_leaves_its_accounts_as_manual_ones()
    {
        await UploadAsync(Client());
        var setting = await SettingAsync("pf-a");
        var client = Client();
        client.Portfolios.Remove(client.Portfolios.Single(p => p.Uuid == "pf-a"));
        await UploadAsync(client);
        Assert.Equal(AccountProviders.Manual, (await AccountAsync(setting.DepotAccountId!.Value)).Provider);
    }
    [Fact]
    public async Task The_balance_of_depot_and_ledger_follows_the_holdings_and_the_reference_cash_over_time()
    {
        // 3 shares delivered in on Jan 2 (no cash), one sold on Jan 6 into the reference account, the proceeds removed on Jan 8.
        var c = new PClient { Version = 1, BaseCurrency = "EUR" };
        var house = new PSecurity { Uuid = "sec-flat", Name = "Flat", CurrencyCode = "EUR" };
        house.Prices.Add(new PHistoricalPrice { Date = Day(1, 2), Close = 100_00000000 });
        house.Prices.Add(new PHistoricalPrice { Date = Day(1, 5), Close = 110_00000000 });
        c.Securities.Add(house);
        c.Accounts.Add(new PAccount { Uuid = "acc-1", Name = "Cash", CurrencyCode = "EUR" });
        c.Portfolios.Add(new PPortfolio { Uuid = "pf-a", Name = "Property", ReferenceAccount = "acc-1" });
        c.Transactions.Add(Tx("t-in", PType.InboundDelivery, "2024-01-02", 300m, "sec-flat", 3m, "pf-a"));
        c.Transactions.Add(Tx("t-sell", PType.Sale, "2024-01-06", 110m, "sec-flat", 1m, "pf-a", account: "acc-1"));
        c.Transactions.Add(Tx("t-out", PType.Removal, "2024-01-08", 110m, account: "acc-1"));
        await UploadAsync(c);

        var setting = await SettingAsync("pf-a");
        var depot = await AccountAsync(setting.DepotAccountId!.Value);
        var ledger = await AccountAsync(setting.LedgerAccountId!.Value);
        Assert.Equal((220m, 0m), (depot.CurrentBalance, ledger.CurrentBalance));

        // The ledger's history is the reference cash: 0 from its first booking, 110 after the sale, 0 after the removal.
        var history = await _t.TransactionQueries.GetDepotValueHistoryAsync(depot.OwnerUserId, [depot.Id, ledger.Id]);
        Assert.Equal([(new DateTime(2024, 1, 2), 0m), (new DateTime(2024, 1, 6), 110m), (new DateTime(2024, 1, 8), 0m)], history[ledger.Id]);

        // The group's daily balance as the Transactions chart builds it: both accounts take their stored history.
        var today = new DateTime(2024, 1, 10);
        var series = new[] { depot, ledger }.Select(a => new DepotValueSeries(history[a.Id], a.CurrentBalance!.Value)).ToList();
        var chart = BrowseChart.BuildChartSeries(
            earliest: new DateTime(2024, 1, 1), rightEdge: new DateTime(2024, 1, 1), granularity: "D",
            anchorYear: 2024, fromMonth: 1, toMonth: 1,
            requestInvertY: false, isAccountDaily: true,
            raw: new List<(DateTime, decimal)>(), currentBalanceForDaily: 0m,
            depots: series, today: today, lastDay: today);
        Assert.Equal([0m, 300m, 300m, 300m, 330m, 330m, 330m, 220m, 220m, 220m], chart.Bars.Select(b => b.Value));
    }
}
