using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>Existing accounts a portfolio's accounts can take over (named like the portfolio and its ledger).</summary>
public sealed record PortfolioTakeoverCandidate(long? DepotAccountId, string? DepotName, long? LedgerAccountId, string? LedgerName)
{
    public bool Any => DepotAccountId != null || LedgerAccountId != null;
}

/// <summary>
/// Shows the PortfolioPerformance portfolios of a source as accounts, recomputed from the source's current file
/// (<see cref="IPortfolioFileSource"/>) on every upload and settings change:
/// <list type="bullet">
/// <item>a depot account per shown portfolio, with its positions (<see cref="PortfolioValuation"/>) and a daily value
/// history (an account total per day plus one line per currently held security);</item>
/// <item>a ledger account "&lt;portfolio&gt; (Verrechnung)" with the portfolio's buys, sells, deliveries, dividends,
/// fees and taxes. Rows are recognised again by the PortfolioPerformance transaction uuid
/// (<see cref="Transaction.ExternalId"/> "pp:&lt;uuid&gt;[:fee|:tax]"), so categories, notes and check marks survive;
/// rows whose transaction was deleted in PortfolioPerformance disappear.</item>
/// </list>
/// New accounts live in a source of their own ("&lt;source&gt;-pp", owned by the source's owner), apart from the
/// accounts a MoneyMoney upload replaces. A portfolio whose accounts already exist under the same name (made by the
/// earlier MoneyMoney extension) is not given new accounts until the owner takes those over or declines; taken-over
/// accounts keep their history: bookings that match a PortfolioPerformance transaction are adopted, others (e.g.
/// funding transfers) stay, and value history before the first PortfolioPerformance day is kept.
/// Accounts of a portfolio that is switched off or deleted in PortfolioPerformance are released: taken-over ones keep
/// their data and return to MoneyMoney; ones this sync created are removed when the portfolio is switched off, and become
/// manual when it is deleted in PortfolioPerformance.
/// </summary>
public sealed class PortfolioAccountSync
{
    public const string LedgerSuffix = " (Verrechnung)";
    public const string ExternalIdPrefix = "pp:";

    private readonly SystemDbContextFactory _dbFactory;
    private readonly IPortfolioFileSource _files;
    private readonly ChangeNotificationService _notifier;
    private readonly TimeProvider _clock;
    private readonly ILogger<PortfolioAccountSync> _logger;
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();

    public PortfolioAccountSync(
        SystemDbContextFactory dbFactory, IPortfolioFileSource files, ChangeNotificationService notifier,
        TimeProvider clock, ILogger<PortfolioAccountSync> logger)
    {
        _dbFactory = dbFactory;
        _files = files;
        _notifier = notifier;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Whether a portfolio is shown as accounts when the owner has not decided: not when it is linked to a bank depot or retired.</summary>
    public static bool DefaultShow(bool linkedToBankDepot, bool retired) => !linkedToBankDepot && !retired;

    /// <summary>Accounts of <paramref name="ownerUserId"/> that the portfolio's accounts could take over (MoneyMoney-owned, same name).</summary>
    public static async Task<PortfolioTakeoverCandidate> FindTakeoverCandidateAsync(
        BudgetDbContext db, string? ownerUserId, string portfolioName, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ownerUserId)) return new PortfolioTakeoverCandidate(null, null, null, null);
        var ledgerName = portfolioName + LedgerSuffix;
        var owned = db.ImportSources.IgnoreQueryFilters().Where(s => s.OwnerUserId == ownerUserId).Select(s => s.Id);
        var accounts = await db.Accounts.IgnoreQueryFilters().AsNoTracking()
            .Where(a => owned.Contains(a.SourceId) && a.Provider == AccountProviders.MoneyMoney && (a.Name == portfolioName || a.Name == ledgerName))
            .OrderBy(a => a.Id)
            .ToListAsync(ct);
        var depot = accounts.FirstOrDefault(a => a.Name == portfolioName && (a.Portfolio == 1 || a.Type == 7));
        var ledger = accounts.FirstOrDefault(a => a.Name == ledgerName && a.Portfolio != 1 && a.Type != 7);
        return new PortfolioTakeoverCandidate(depot?.Id, depot?.Name, ledger?.Id, ledger?.Name);
    }

    /// <summary>Recomputes every portfolio account of the source from its current file. Does nothing without a file.</summary>
    public async Task SyncAsync(int sourceId, CancellationToken ct = default)
    {
        var gate = _locks.GetOrAdd(sourceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await SyncCoreAsync(sourceId, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task SyncCoreAsync(int sourceId, CancellationToken ct)
    {
        PpValuation valuation;
        await using (var file = await _files.OpenLatestAsync(sourceId, ct))
        {
            if (file == null) return;
            var client = PortfolioPerformanceFile.ReadClient(file);
            var now = _clock.GetUtcNow();
            valuation = PortfolioValuation.Compute(client, DateOnly.FromDateTime(now.UtcDateTime));
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var source = await db.ImportSources.FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return;

        var settings = await db.PpPortfolioSettings.Where(s => s.SourceId == sourceId).ToListAsync(ct);
        var linked = (await db.PpPortfolioLinks.Where(l => l.SourceId == sourceId).Select(l => l.PpPortfolioUuid).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var touchedSources = new HashSet<int>();
        var nowSeconds = _clock.GetUtcNow().ToUnixTimeSeconds();

        foreach (var portfolio in valuation.Portfolios)
        {
            var setting = settings.FirstOrDefault(s => s.PpPortfolioUuid == portfolio.Uuid);
            var show = setting?.ShowAsAccount ?? DefaultShow(linked.Contains(portfolio.Uuid), portfolio.Retired);
            if (!show)
            {
                if (setting != null) await ReleaseAsync(db, setting, touchedSources, removeCreated: true, ct);
                continue;
            }

            if (setting == null)
            {
                setting = new PpPortfolioSetting { SourceId = sourceId, PpPortfolioUuid = portfolio.Uuid };
                db.PpPortfolioSettings.Add(setting);
                settings.Add(setting);
            }

            // Accounts removed in the meantime (e.g. deleted by the owner) are made again.
            if (setting.DepotAccountId is { } d && !await db.Accounts.AnyAsync(a => a.Id == d, ct)) setting.DepotAccountId = null;
            if (setting.LedgerAccountId is { } l && !await db.Accounts.AnyAsync(a => a.Id == l, ct)) setting.LedgerAccountId = null;

            if (setting.DepotAccountId == null && setting.LedgerAccountId == null && !setting.TakeoverDeclined
                && (await FindTakeoverCandidateAsync(db, source.OwnerUserId, portfolio.Name, ct)).Any)
            {
                // Waits for the owner to take the existing accounts over or decline.
                continue;
            }

            var created = new List<long>();
            if (setting.DepotAccountId == null)
            {
                var account = await CreateAccountAsync(db, source, portfolio.Name, valuation.BaseCurrency, depot: true, ct);
                setting.DepotAccountId = account.Id;
                created.Add(account.Id);
            }
            if (setting.LedgerAccountId == null)
            {
                var account = await CreateAccountAsync(db, source, portfolio.Name + LedgerSuffix, valuation.BaseCurrency, depot: false, ct);
                setting.LedgerAccountId = account.Id;
                created.Add(account.Id);
            }
            await db.SaveChangesAsync(ct);
            if (created.Count > 0) await AssignProjectAsync(db, source.OwnerUserId, setting.ProjectId, created, ct);

            var depotAccount = await db.Accounts.FirstAsync(a => a.Id == setting.DepotAccountId, ct);
            var ledgerAccount = await db.Accounts.FirstAsync(a => a.Id == setting.LedgerAccountId, ct);
            depotAccount.Provider = AccountProviders.PortfolioPerformance;
            ledgerAccount.Provider = AccountProviders.PortfolioPerformance;

            await BankTransactionStore.Gate.WaitAsync(ct);
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await WriteDepotAsync(db, depotAccount, portfolio, valuation.BaseCurrency, setting.TakenOver, ct);
                await WriteLedgerAsync(db, ledgerAccount, portfolio, valuation.BaseCurrency, setting.TakenOver,
                    markNew: !created.Contains(ledgerAccount.Id), ct);
                depotAccount.RefreshTimestamp = nowSeconds;
                depotAccount.Error = null;
                ledgerAccount.RefreshTimestamp = nowSeconds;
                ledgerAccount.Error = null;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            finally
            {
                BankTransactionStore.Gate.Release();
            }
            touchedSources.Add(depotAccount.SourceId);
            touchedSources.Add(ledgerAccount.SourceId);
        }

        // Portfolios deleted in PortfolioPerformance: their accounts stop being updated.
        var present = valuation.Portfolios.Select(p => p.Uuid).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in settings.Where(s => !present.Contains(s.PpPortfolioUuid)))
            await ReleaseAsync(db, gone, touchedSources, removeCreated: false, ct);

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("PortfolioPerformance accounts of source {SourceId} recomputed ({Portfolios} portfolios)", sourceId, valuation.Portfolios.Count);
        foreach (var s in touchedSources) _notifier.Publish(new DataChange(DataArea.ImportedData, SourceId: s));
    }

    /// <summary>
    /// Releases a portfolio's accounts: one taken over from MoneyMoney (it lives in the portfolio's own source) returns to
    /// MoneyMoney with its data. One this sync created (in the "-pp" source) is removed with its bookings, positions and
    /// value history when the owner switched the portfolio off (<paramref name="removeCreated"/>; it is created afresh if
    /// the portfolio is shown again), and stays as a manual account when the portfolio disappeared from the file.
    /// </summary>
    private static async Task ReleaseAsync(BudgetDbContext db, PpPortfolioSetting setting, HashSet<int> touched, bool removeCreated, CancellationToken ct)
    {
        var ids = new[] { setting.DepotAccountId, setting.LedgerAccountId }.OfType<long>().ToList();
        foreach (var account in await db.Accounts.Where(a => ids.Contains(a.Id) && a.Provider == AccountProviders.PortfolioPerformance).ToListAsync(ct))
        {
            touched.Add(account.SourceId);
            if (account.SourceId == setting.SourceId)
            {
                account.Provider = AccountProviders.MoneyMoney;
                continue;
            }
            if (!removeCreated)
            {
                account.Provider = AccountProviders.Manual;
                continue;
            }
            await RemoveCreatedAccountAsync(db, account.Id, ct);
            db.Accounts.Remove(account);
            if (setting.DepotAccountId == account.Id) setting.DepotAccountId = null;
            if (setting.LedgerAccountId == account.Id) setting.LedgerAccountId = null;
        }
    }

    /// <summary>Removes an account this sync created with everything that hangs off it.</summary>
    private static async Task RemoveCreatedAccountAsync(BudgetDbContext db, long id, CancellationToken ct)
    {
        var bookings = db.Transactions.Where(t => t.AccountId == id);
        await bookings.ExecuteDeleteAsync(ct);
        await db.Holdings.Where(h => h.AccountId == id).ExecuteDeleteAsync(ct);
        await db.PortfolioChartPoints.Where(p => p.AccountId == id).ExecuteDeleteAsync(ct);
        await db.PpPortfolioLinks.Where(p => p.AccountId == id).ExecuteDeleteAsync(ct);
        await db.ProjectAccountAssignments.Where(p => p.AccountId == id).ExecuteDeleteAsync(ct);
        await db.BankLoginAccounts.Where(l => l.LinkedAccountId == id)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.LinkedAccountId, (long?)null), ct);
    }

    private static async Task<Account> CreateAccountAsync(BudgetDbContext db, ImportSource source, string name, string currency, bool depot, CancellationToken ct)
    {
        var target = await AccountsSourceAsync(db, source, ct);
        var maxId = await db.Accounts.Where(a => a.SourceId == target.Id).MaxAsync(a => (long?)a.Id, ct);
        var tracked = db.Accounts.Local.Where(a => a.SourceId == target.Id).Max(a => (long?)a.Id);
        var account = new Account
        {
            Id = Math.Max(maxId ?? 0, tracked ?? 0) is > 0 and var m ? m + 1 : SourceScope.Namespace(target.Id, 1),
            SourceId = target.Id,
            Name = name,
            Currency = currency,
            Type = depot ? 7 : 0,
            Portfolio = depot ? 1 : 0,
            Provider = AccountProviders.PortfolioPerformance,
            Balances = $"11:{0.0.ToString("F6", CultureInfo.InvariantCulture)},3:{currency},",
        };
        db.Accounts.Add(account);
        return account;
    }

    /// <summary>The owner's source for created portfolio accounts ("&lt;source&gt;-pp", or the next free variant).</summary>
    private static async Task<ImportSource> AccountsSourceAsync(BudgetDbContext db, ImportSource source, CancellationToken ct)
    {
        var baseName = $"{source.Name}-pp";
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            var name = attempt == 1 ? baseName : $"{baseName}-{attempt}";
            var existing = db.ImportSources.Local.FirstOrDefault(s => s.Name == name)
                ?? await db.ImportSources.FirstOrDefaultAsync(s => s.Name == name, ct);
            if (existing == null)
            {
                var created = new ImportSource { Name = name, OwnerUserId = source.OwnerUserId };
                db.ImportSources.Add(created);
                await db.SaveChangesAsync(ct);
                return created;
            }
            if (existing.OwnerUserId == source.OwnerUserId) return existing;
        }
        throw new InvalidOperationException($"No free source name for the portfolio accounts of '{source.Name}'.");
    }

    /// <summary>New accounts land in the chosen project, or else in the owner's default project for new accounts.</summary>
    private static async Task AssignProjectAsync(BudgetDbContext db, string? ownerUserId, int? projectId, List<long> accountIds, CancellationToken ct)
    {
        if (projectId is { } pid && await db.Projects.AnyAsync(p => p.Id == pid && p.OwnerUserId == ownerUserId, ct))
        {
            foreach (var id in accountIds)
                db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = pid, AccountId = id, Scope = AccountScope.Exclusive, InCashflow = false });
            await db.SaveChangesAsync(ct);
            return;
        }
        await DefaultProjectAssigner.AssignNewAccountsAsync(db, ownerUserId, accountIds, ct);
    }

    private static async Task<long> NextIdAsync(IQueryable<long> ids, int sourceId, CancellationToken ct)
    {
        var lo = SourceScope.Namespace(sourceId, BankTransactionStore.ConnectorRowBase);
        var hi = SourceScope.Namespace(sourceId + 1, 0);
        return (await ids.Where(i => i >= lo && i < hi).MaxAsync(i => (long?)i, ct)) ?? lo;
    }

    private static async Task WriteDepotAsync(
        BudgetDbContext db, Account account, PpPortfolioValuation portfolio, string baseCurrency, bool takenOver, CancellationToken ct)
    {
        await db.Holdings.Where(h => h.AccountId == account.Id).ExecuteDeleteAsync(ct);
        var nextHolding = await NextIdAsync(db.Holdings.Where(h => h.SourceId == account.SourceId).Select(h => h.Id), account.SourceId, ct);
        var holdingBySecurity = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var p in portfolio.Positions)
        {
            var currency = p.Security.Currency ?? baseCurrency;
            var id = ++nextHolding;
            holdingBySecurity[p.Security.Uuid] = id;
            db.Holdings.Add(new Holding
            {
                Id = id,
                SourceId = account.SourceId,
                AccountId = account.Id,
                Name = p.Security.Name,
                Isin = p.Security.Isin,
                Wkn = p.Security.Wkn,
                Symbol = p.Security.Ticker,
                PpSecurityUuid = p.Security.Uuid,
                Quantity = p.Shares,
                Amount = p.Amount,
                AmountCurrency = currency,
                Price = p.Price,
                PriceCurrency = currency,
                Purchase = p.PurchasePrice,
            });
        }
        var total = portfolio.Positions.Sum(p => p.Amount);
        account.Balances = $"11:{total.ToString("F6", CultureInfo.InvariantCulture)},3:{account.Currency ?? baseCurrency},";

        // Value history: one total line (no security) per day plus a line per currently held security.
        var days = portfolio.History.SelectMany(h => h.Values.Keys).Distinct().Order().ToList();
        var chart = db.PortfolioChartPoints.Where(p => p.AccountId == account.Id);
        if (takenOver)
        {
            // Earlier history of a taken-over account stays; from the first day PortfolioPerformance covers, it decides.
            if (days.Count == 0) return;
            var from = days[0] * 86400;
            chart = chart.Where(p => p.Timestamp >= from);
        }
        await chart.ExecuteDeleteAsync(ct);
        var nextPoint = await NextIdAsync(db.PortfolioChartPoints.Where(p => p.SourceId == account.SourceId).Select(p => p.Id), account.SourceId, ct);
        foreach (var day in days)
        {
            var ts = day * 86400 + 43200;  // noon UTC
            double sum = 0;
            foreach (var series in portfolio.History)
            {
                if (!series.Values.TryGetValue(day, out var value)) continue;
                sum += value;
                if (holdingBySecurity.TryGetValue(series.SecurityUuid, out var holdingId))
                    db.PortfolioChartPoints.Add(Point(++nextPoint, account, baseCurrency, ts, value, holdingId));
            }
            db.PortfolioChartPoints.Add(Point(++nextPoint, account, baseCurrency, ts, sum, null));
        }
    }

    private static PortfolioChartPoint Point(long id, Account account, string currency, long ts, double value, long? securityId) => new()
    {
        Id = id,
        SourceId = account.SourceId,
        AccountId = account.Id,
        Currency = currency,
        Timestamp = ts,
        Balance = value,
        SecurityId = securityId,
    };

    private static async Task WriteLedgerAsync(
        BudgetDbContext db, Account account, PpPortfolioValuation portfolio, string currency, bool takenOver, bool markNew, CancellationToken ct)
    {
        var existing = await db.Transactions.Where(t => t.AccountId == account.Id).ToListAsync(ct);
        var byKey = existing.Where(t => t.ExternalId != null && t.ExternalId.StartsWith(ExternalIdPrefix, StringComparison.Ordinal))
            .GroupBy(t => t.ExternalId!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        // Bookings of a taken-over ledger from before (no external id); split parts are OpenBudget's own and stay.
        var legacy = takenOver ? existing.Where(t => t.ExternalId == null && t.Splitted != SplitState.Child).ToList() : [];
        var nextId = await NextIdAsync(db.Transactions.Where(t => t.SourceId == account.SourceId).Select(t => t.Id), account.SourceId, ct);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var added = new List<Transaction>();

        foreach (var e in portfolio.Ledger)
        {
            var externalId = ExternalIdPrefix + e.Key;
            if (!seen.Add(externalId)) continue;
            var cents = (long)Math.Round(e.Amount * 100, MidpointRounding.AwayFromZero);
            var purpose = e.Purpose;
            if (!byKey.TryGetValue(externalId, out var row))
            {
                row = Adopt(legacy, cents, e.Timestamp, e.Type, purpose);
                if (row != null)
                {
                    legacy.Remove(row);
                    row.ExternalId = externalId;
                }
            }
            if (row == null)
            {
                row = new Transaction
                {
                    Id = ++nextId,
                    SourceId = account.SourceId,
                    AccountId = account.Id,
                    ExternalId = externalId,
                    Splitted = 0,
                    Checkmark = 0,
                    Booked = true,
                    IsNew = markNew,
                };
                db.Transactions.Add(row);
                added.Add(row);
            }
            row.Amount = cents;
            row.Currency = currency;
            row.Timestamp = e.Timestamp;
            row.Name = e.Type;
            row.Purpose = purpose;
            row.BookingText = e.Type;
        }

        // Transactions deleted in PortfolioPerformance disappear (with their split parts).
        var stale = byKey.Where(kv => !seen.Contains(kv.Key)).Select(kv => kv.Value).ToList();
        if (stale.Count > 0)
        {
            var staleIds = stale.Select(t => t.Id).ToList();
            db.Transactions.RemoveRange(stale);
            await db.Transactions.Where(t => t.SplitParentId != null && staleIds.Contains(t.SplitParentId.Value)).ExecuteDeleteAsync(ct);
        }

        await CategoryRuleEngine.ApplyToNewAsync(db, account.Id, added, ct);
        await db.SaveChangesAsync(ct);

        // The balance is the reference cash account's, as PortfolioPerformance shows it (deposits and transfers move
        // it without a ledger line); a portfolio without one sums its ledger.
        var balance = portfolio.CashBalance is { } cash
            ? (decimal)Math.Round(cash, 2, MidpointRounding.AwayFromZero)
            : (account.InitialBalance + (await db.Transactions.Where(t => t.AccountId == account.Id && t.Splitted != SplitState.Parent)
                .SumAsync(t => (long?)t.Amount, ct) ?? 0)) / 100m;
        account.Balances = $"11:{balance.ToString("F6", CultureInfo.InvariantCulture)},3:{account.Currency ?? currency},";
    }

    /// <summary>A booking of the taken-over ledger that is this PortfolioPerformance transaction: same amount within a day, by type and purpose, else by type.</summary>
    private static Transaction? Adopt(List<Transaction> legacy, long cents, long ts, string type, string purpose)
    {
        bool Near(Transaction t) => t.Amount == cents && Math.Abs(t.Timestamp - ts) < 86400;
        return legacy.Find(t => Near(t) && t.Name == type && t.Purpose == purpose)
            ?? legacy.Find(t => Near(t) && t.Name == type);
    }
}
