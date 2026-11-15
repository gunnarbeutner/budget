using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Connectors.Sync;

/// <summary>
/// Keeps an account's standing orders and scheduled transfers (<see cref="ScheduledPayment"/>) as the bank last reported
/// them, per source: the account refresh, and the web banking (<see cref="IWebScheduledSource"/>).
/// </summary>
public static class ScheduledPaymentStore
{
    /// <summary>
    /// Replaces the account's refresh rows with <see cref="AccountRefresh.Scheduled"/>. A refresh without that list (the
    /// connector does not report them, or reading them failed) leaves the stored rows alone; rows read from the web
    /// banking always stay. Saves nothing itself.
    /// </summary>
    public static Task ApplyAsync(BudgetDbContext db, Account account, AccountRefresh refresh, DateTime nowUtc, CancellationToken ct) =>
        refresh.Scheduled == null ? Task.CompletedTask : ReplaceAsync(db, account, refresh.Scheduled, fromWeb: false, nowUtc, ct);

    /// <summary>Replaces the account's rows read from the web banking with <paramref name="plans"/>; the refresh rows stay. Saves nothing itself.</summary>
    public static Task ApplyWebAsync(BudgetDbContext db, Account account, IReadOnlyList<RemoteScheduledPayment> plans, DateTime nowUtc, CancellationToken ct) =>
        ReplaceAsync(db, account, plans, fromWeb: true, nowUtc, ct);

    private static async Task ReplaceAsync(
        BudgetDbContext db, Account account, IReadOnlyList<RemoteScheduledPayment> list, bool fromWeb, DateTime nowUtc, CancellationToken ct)
    {
        await db.ScheduledPayments.Where(p => p.AccountId == account.Id && p.FromWebBanking == fromWeb).ExecuteDeleteAsync(ct);
        foreach (var r in list)
        {
            db.ScheduledPayments.Add(new ScheduledPayment
            {
                OwnerUserId = account.OwnerUserId,
                AccountId = account.Id,
                Kind = r.Kind,
                BankOrderId = Clip(r.BankOrderId, 100),
                NextDate = r.NextDate,
                FirstDate = r.FirstDate,
                LastDate = r.LastDate,
                IntervalUnit = r.Kind == ScheduledPaymentKind.Standing ? r.IntervalUnit : null,
                IntervalCount = r.Kind == ScheduledPaymentKind.Standing ? r.IntervalCount : null,
                ExecutionDay = r.Kind == ScheduledPaymentKind.Standing ? r.ExecutionDay : null,
                Amount = BankTransactionStore.ToCents(r.Amount),
                Currency = string.IsNullOrEmpty(r.Currency) ? "EUR" : Clip(r.Currency, 10)!,
                CounterpartyName = Clip(r.CounterpartyName, 300),
                CounterpartyIban = Clip(r.CounterpartyIban?.Replace(" ", string.Empty, StringComparison.Ordinal), 50),
                CounterpartyBic = Clip(r.CounterpartyBic, 20),
                Purpose = Clip(r.Purpose, 1000),
                FetchedAt = nowUtc,
                FromWebBanking = fromWeb,
            });
        }
    }

    private static string? Clip(string? value, int max) => value == null || value.Length <= max ? value : value[..max];
}
