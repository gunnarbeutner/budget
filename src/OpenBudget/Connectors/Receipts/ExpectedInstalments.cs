using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Connectors.Receipts;

/// <summary>
/// An instalment of a running Amazon plan still to come: the full-price receipt, the account the plan's payment method is
/// mapped to, the expected day, the amount (negative cents), its number in the plan, the first item's title and the
/// category its instalments were booked to (null: uncategorised).
/// </summary>
public sealed record ExpectedInstalment(long ReceiptId, long AccountId, DateOnly Date, long Cents, string OrderNumber, int Number, int Count,
    string? FirstItem, long? CategoryId);

/// <summary>
/// The instalments still to come of orders paid in instalments, worked out from the receipts on every read (see
/// <see cref="InstalmentProgress"/>): an instalment that arrives or a cancelled plan drops out by itself.
/// </summary>
public static class ExpectedInstalments
{
    /// <summary>Longest first item title shown next to the order.</summary>
    public const int MaxItemTitle = 40;

    /// <summary>More instalments than a plan can have (an instalment's key: receipt id × this + its number).</summary>
    public const int MaxCount = 1000;

    /// <summary>
    /// The expected instalments on the given accounts, soonest first. <paramref name="db"/> must see all receipts (a
    /// system context): who may see an instalment is up to the caller's choice of accounts. An estimate already past is
    /// expected today.
    /// </summary>
    public static async Task<List<ExpectedInstalment>> ForAccountsAsync(BudgetDbContext db, IReadOnlyCollection<long> accountIds, DateOnly today, CancellationToken ct = default)
    {
        var result = new List<ExpectedInstalment>();
        if (accountIds.Count == 0) return result;
        var ids = accountIds.ToList();
        var mapping = (await db.BankLoginAccounts.AsNoTracking().Where(a => a.EnrichAccountId != null && ids.Contains(a.EnrichAccountId.Value))
                .Select(a => new { a.BankLoginId, a.RemoteId, Account = a.EnrichAccountId!.Value }).ToListAsync(ct))
            .GroupBy(a => (a.BankLoginId, a.RemoteId)).ToDictionary(g => g.Key, g => g.First().Account);
        if (mapping.Count == 0) return result;
        var logins = mapping.Keys.Select(k => (int?)k.BankLoginId).Distinct().ToList();
        var fulls = (await db.ReceiptInvoices.AsNoTracking()
                .Where(r => r.Shop == ReceiptShops.Amazon && r.State == ReceiptMatchState.Instalments && r.PaymentCents > 0 && r.OrderNumber != null && logins.Contains(r.BankLoginId))
                .Select(r => new { r.Id, r.OrderNumber, r.BankLoginId, r.PaymentCents, r.State, r.TransactionId, r.PaymentMethodId })
                .ToListAsync(ct))
            .Where(r => mapping.ContainsKey((r.BankLoginId!.Value, r.PaymentMethodId ?? ""))).ToList();
        if (fulls.Count == 0) return result;
        var (plans, _) = await ReceiptService.InstalmentPlansAsync(db, fulls.Select(r => (r.Id, r.OrderNumber, r.BankLoginId, r.PaymentCents, r.State, r.TransactionId)).ToList(), ct);
        var running = fulls.Where(f => plans.TryGetValue(f.Id, out var p) && p.Running && p.Due.Count > 0).ToList();
        if (running.Count == 0) return result;

        var runningIds = running.Select(r => r.Id).ToList();
        var firstItems = (await db.ReceiptItems.AsNoTracking().Where(i => runningIds.Contains(i.ReceiptInvoiceId) && i.Kind == ReceiptItemKind.Product)
                .Select(i => new { i.ReceiptInvoiceId, i.Position, i.Name }).ToListAsync(ct))
            .GroupBy(i => i.ReceiptInvoiceId).ToDictionary(g => g.Key, g => g.OrderBy(i => i.Position).First().Name);
        var bookingIds = running.SelectMany(r => plans[r.Id].Charges).Where(c => c.TransactionId != null).Select(c => c.TransactionId!.Value).Distinct().ToList();
        var bookings = await db.Transactions.AsNoTracking().Where(t => bookingIds.Contains(t.Id))
            .Select(t => new { t.Id, t.CategoryId, Cents = t.SplitOriginalAmount ?? t.Amount }).ToDictionaryAsync(t => t.Id, ct);

        foreach (var full in running)
        {
            var plan = plans[full.Id];
            // The booking category of the matched instalments: the one they share, else the one with the most money.
            var category = plan.Charges.Where(c => c.TransactionId is { } t && bookings.ContainsKey(t)).Select(c => bookings[c.TransactionId!.Value])
                .Where(b => b.CategoryId != null).GroupBy(b => b.CategoryId).OrderByDescending(g => g.Sum(b => Math.Abs(b.Cents))).Select(g => g.Key).FirstOrDefault();
            var item = firstItems.GetValueOrDefault(full.Id);
            if (item is { Length: > MaxItemTitle }) item = null;
            var account = mapping[(full.BankLoginId!.Value, full.PaymentMethodId ?? "")];
            foreach (var due in plan.Due)
            {
                var date = DateOnly.FromDateTime(due.Date);
                result.Add(new ExpectedInstalment(full.Id, account, date < today ? today : date, -due.Cents, full.OrderNumber!, due.Number, plan.Count, item, category));
            }
        }
        return result.OrderBy(r => r.Date).ThenBy(r => r.ReceiptId).ToList();
    }
}
