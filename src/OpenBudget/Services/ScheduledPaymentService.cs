using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Receipts;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>A standing order or scheduled transfer as the Scheduled view shows it (amount signed, in currency units); <see cref="WebBankingAsOf"/> is when it was read from the web banking, null for orders read with the account refresh.</summary>
public sealed record ScheduledItem(
    long Id,
    ScheduledPaymentKind Kind,
    DateOnly NextDate,
    DateOnly? LastDate,
    ScheduleUnit? IntervalUnit,
    int? IntervalCount,
    int? ExecutionDay,
    decimal Amount,
    string Currency,
    string? CounterpartyName,
    string? CounterpartyIban,
    string? CounterpartyBic,
    string? Purpose,
    long AccountId,
    string AccountName,
    string AccountIcon,
    string? AccountColor,
    long? ReceiptId = null,
    int? InstalmentNumber = null,
    int? InstalmentCount = null,
    DateTime? WebBankingAsOf = null);

/// <summary>An account of the project whose bank could not deliver its standing orders in the last sync.</summary>
public sealed record ScheduledFetchProblem(long AccountId, string AccountName, string Error);

/// <summary>What the Scheduled view of a project lists.</summary>
public sealed record ScheduledOverview(IReadOnlyList<ScheduledItem> Items, IReadOnlyList<ScheduledFetchProblem> Problems);

/// <summary>
/// The Scheduled view: the standing orders and future-dated transfers the banks reported for the project's accounts,
/// plus the expected instalments of running instalment plans whose payment method is mapped to one of them (see
/// <see cref="ExpectedInstalments"/>) and the next expected charge of each active recurring series the user has not
/// dismissed (see <see cref="RecurringPaymentService"/>), soonest first. Reads through the user-scoped context, so the access rules of
/// <see cref="ScheduledPayment"/> (those of the account's transactions) apply; the instalments are read on a system
/// context, but only for the accounts the user sees.
/// </summary>
public class ScheduledPaymentService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly IDbContextFactory<BudgetDbContext>? _system;
    private readonly RecurringPaymentService? _recurring;

    public ScheduledPaymentService(IDbContextFactory<BudgetDbContext> dbFactory, SystemDbContextFactory? system = null, RecurringPaymentService? recurring = null)
    {
        _dbFactory = dbFactory;
        _system = system;
        _recurring = recurring;
    }

    private const long RecurringIdBase = long.MinValue / 2;

    public async Task<ScheduledOverview> GetAsync(int projectId, DateOnly today, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var accountIds = await db.ProjectAccountAssignments.Where(a => a.ProjectId == projectId).Select(a => a.AccountId).ToListAsync(ct);
        if (accountIds.Count == 0) return new ScheduledOverview([], []);
        var accounts = await db.Accounts.AsNoTracking().Where(a => accountIds.Contains(a.Id))
            .Select(a => new Account { Id = a.Id, Name = a.Name, Icon = a.Icon, Color = a.Color, Type = a.Type, Portfolio = a.Portfolio })
            .ToDictionaryAsync(a => a.Id, ct);
        var visible = accounts.Keys.ToList();
        var rows = await db.ScheduledPayments.AsNoTracking().Where(p => visible.Contains(p.AccountId)).ToListAsync(ct);

        var items = new List<ScheduledItem>();
        foreach (var p in rows)
        {
            // A sync a while ago may have left an execution date in the past: the next one counts.
            var next = p.Occurrences(today, today.AddYears(3)).Select(d => (DateOnly?)d).FirstOrDefault();
            if (next == null) continue;
            var a = accounts[p.AccountId];
            items.Add(new ScheduledItem(
                p.Id, p.Kind, next.Value, p.LastDate, p.IntervalUnit, p.IntervalCount, p.ExecutionDay,
                p.Amount / 100m, p.Currency, p.CounterpartyName, p.CounterpartyIban, p.CounterpartyBic, p.Purpose,
                p.AccountId, a.Name ?? "?", AccountIcons.ForAccount(a), a.Color)
            {
                WebBankingAsOf = p.FromWebBanking ? p.FetchedAt : null,
            });
        }
        if (_system != null)
        {
            await using var sys = await _system.CreateDbContextAsync(ct);
            foreach (var e in await ExpectedInstalments.ForAccountsAsync(sys, visible, today, ct))
            {
                var a = accounts[e.AccountId];
                // Negative ids keep the expected instalments apart from the stored orders.
                items.Add(new ScheduledItem(
                    -(e.ReceiptId * ExpectedInstalments.MaxCount + e.Number), ScheduledPaymentKind.Instalment, e.Date, null, null, null, null,
                    e.Cents / 100m, "EUR", e.Title, null, null, e.FirstItem,
                    e.AccountId, a.Name ?? "?", AccountIcons.ForAccount(a), a.Color,
                    e.ReceiptId, e.Number, e.Count));
            }
        }
        if (_recurring != null)
        {
            var index = 0;
            foreach (var r in await _recurring.ActiveAsync(projectId, today, visible, ct))
            {
                if (r.Occurrences(today, today.AddYears(2)).Select(d => (DateOnly?)d).FirstOrDefault() is not { } next) continue;
                var a = accounts[r.AccountId];
                // Ids below the instalments' range keep the recurring rows apart.
                items.Add(new ScheduledItem(
                    RecurringIdBase - index++, ScheduledPaymentKind.Recurring, next, null,
                    r.Rhythm.Weekly ? ScheduleUnit.Week : ScheduleUnit.Month, r.Rhythm.Count, null,
                    r.Cents / 100m, "EUR", r.Payee, null, null, null,
                    r.AccountId, a.Name ?? "?", AccountIcons.ForAccount(a), a.Color));
            }
        }
        items = items.OrderBy(i => i.NextDate).ThenBy(i => i.CounterpartyName, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.Id).ToList();

        // Bank logins are personal: only their owner sees why a fetch failed.
        var problems = await db.BankLoginAccounts.AsNoTracking()
            .Where(l => l.LinkedAccountId != null && visible.Contains(l.LinkedAccountId.Value) && l.ScheduledError != null)
            .Select(l => new { AccountId = l.LinkedAccountId!.Value, l.ScheduledError })
            .ToListAsync(ct);
        return new ScheduledOverview(
            items,
            problems.Select(p => new ScheduledFetchProblem(p.AccountId, accounts[p.AccountId].Name ?? "?", p.ScheduledError!)).ToList());
    }
}
