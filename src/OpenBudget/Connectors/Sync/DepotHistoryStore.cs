using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Connectors.Sync;

/// <summary>
/// Builds a connector depot's value history (<c>portfolio_chart</c>) one day at a time: after a successful refresh,
/// the day of the sync gets a total point (no security) with the depot's value and a point per held security with
/// that holding's value, all at noon UTC like the imported history. A later sync on the same day replaces that day's
/// points; earlier days stay. Accounts whose history PortfolioPerformance owns are left alone.
/// Points are in the account's currency; a holding valued in another currency gets no point of its own.
/// </summary>
public static class DepotHistoryStore
{
    /// <summary>Records today's value of <paramref name="account"/> from <paramref name="refresh"/>; the holdings must already be stored.</summary>
    public static async Task RecordAsync(BudgetDbContext db, Account account, AccountRefresh refresh, DateOnly today, CancellationToken ct)
    {
        if (string.Equals(account.Provider, AccountProviders.PortfolioPerformance, StringComparison.Ordinal)) return;
        var depot = refresh.Positions.Count > 0 || account.Type == 7 || account.Portfolio == 1;
        if (!depot) return;

        var currency = account.Currency ?? "EUR";
        var holdings = await db.Holdings.AsNoTracking().Where(h => h.AccountId == account.Id).ToListAsync(ct);
        var inCurrency = holdings
            .Where(h => h.Amount != null && (h.AmountCurrency == null || string.Equals(h.AmountCurrency, currency, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        double? total = refresh.Balance is { } balance
            ? (double)balance
            : holdings.Count > 0 && inCurrency.Count == holdings.Count ? inCurrency.Sum(h => h.Amount!.Value) : null;
        if (total == null) return;

        var ts = BankTransactionStore.ToTimestamp(today) + 43200;  // noon UTC
        await BankTransactionStore.Gate.WaitAsync(ct);
        try
        {
            await db.PortfolioChartPoints.Where(p => p.AccountId == account.Id && p.Timestamp >= ts - 43200 && p.Timestamp < ts + 43200)
                .ExecuteDeleteAsync(ct);
            var space = await RowIds.IdSpaceAsync(db, account.OwnerUserId, ct);
            var lo = RowIds.Namespace(space, BankTransactionStore.ConnectorRowBase);
            var hi = RowIds.Namespace(space + 1, 0);
            var next = (await db.PortfolioChartPoints.Where(p => p.OwnerUserId == account.OwnerUserId && p.Id >= lo && p.Id < hi)
                .MaxAsync(p => (long?)p.Id, ct)) ?? lo;
            foreach (var h in inCurrency)
                db.PortfolioChartPoints.Add(Point(++next, account, currency, ts, h.Amount!.Value, h.Id));
            db.PortfolioChartPoints.Add(Point(next + 1, account, currency, ts, total.Value, null));
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            BankTransactionStore.Gate.Release();
        }
    }

    private static PortfolioChartPoint Point(long id, Account account, string currency, long ts, double value, long? securityId) => new()
    {
        Id = id,
        OwnerUserId = account.OwnerUserId,
        AccountId = account.Id,
        Currency = currency,
        Timestamp = ts,
        Balance = value,
        SecurityId = securityId,
    };
}
