using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>A standing order or scheduled transfer as the Scheduled view shows it (amount signed, in currency units).</summary>
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
    string? AccountColor);

/// <summary>An account of the project whose bank could not deliver its standing orders in the last sync.</summary>
public sealed record ScheduledFetchProblem(long AccountId, string AccountName, string Error);

/// <summary>What the Scheduled view of a project lists.</summary>
public sealed record ScheduledOverview(IReadOnlyList<ScheduledItem> Items, IReadOnlyList<ScheduledFetchProblem> Problems);

/// <summary>
/// The Scheduled view: the standing orders and future-dated transfers the banks reported for the project's accounts,
/// soonest first. Reads through the user-scoped context, so the access rules of <see cref="ScheduledPayment"/> (those
/// of the account's transactions) apply.
/// </summary>
public class ScheduledPaymentService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;

    public ScheduledPaymentService(IDbContextFactory<BudgetDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<ScheduledOverview> GetAsync(int projectId, DateOnly today, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var accountIds = await db.ProjectAccountAssignments.Where(a => a.ProjectId == projectId).Select(a => a.AccountId).ToListAsync(ct);
        if (accountIds.Count == 0) return new ScheduledOverview([], []);
        var accounts = await db.Accounts.AsNoTracking().Where(a => accountIds.Contains(a.Id))
            .Select(a => new { a.Id, a.Name, a.Icon, a.Color, a.Type, a.Portfolio })
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
                p.AccountId, a.Name ?? "?", AccountIcons.ForAccount(a.Icon, a.Type, a.Portfolio == 1), a.Color));
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
