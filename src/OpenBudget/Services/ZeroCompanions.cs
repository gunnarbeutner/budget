using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Bookings of exactly 0.00 are bank info lines (redeemed bonus points, a zero deposit) and need no category of their
/// own. When one sits next to a real booking — its main booking — it carries the main's category: same account, booking
/// day at most one day apart, a non-zero amount; among several, the one sharing a merchant word (e.g. "amazon") with
/// the zero booking, and exactly one must remain. Categories chosen by hand, and categories the zero booking got
/// elsewhere (import, rule), are left alone; only uncategorized companions and those that already follow are updated.
/// </summary>
public static class ZeroCompanions
{
    /// <summary>Booking days at most one day apart, with room for a daylight-saving shift.</summary>
    private const long MaxDistanceSeconds = 36 * 3600;

    private const long WindowSeconds = 2 * 86400;

    /// <summary>The words of a payee/purpose that can name a merchant: runs of letters and digits, four or more long, compared ignoring case.</summary>
    public static HashSet<string> Tokens(string? name, string? purpose)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in new[] { name, purpose })
        {
            if (string.IsNullOrEmpty(text)) continue;
            var start = -1;
            for (var i = 0; i <= text.Length; i++)
            {
                var word = i < text.Length && char.IsLetterOrDigit(text[i]);
                if (word && start < 0) start = i;
                else if (!word && start >= 0)
                {
                    if (i - start >= 4) set.Add(text[start..i]);
                    start = -1;
                }
            }
        }
        return set;
    }

    /// <summary>Whether <paramref name="t"/> could be the main booking of <paramref name="zero"/>.</summary>
    public static bool IsCandidate(Transaction zero, Transaction t) =>
        t.Id != zero.Id && t.Amount != 0 && t.AccountId == zero.AccountId && zero.AccountId != null
        && t.Splitted is null or SplitState.Normal or SplitState.Remainder
        && Math.Abs(t.Timestamp - zero.Timestamp) <= MaxDistanceSeconds;

    /// <summary>The main booking of a zero booking among <paramref name="others"/>, or null when there is none or no single best one.</summary>
    public static Transaction? FindMain(Transaction zero, IEnumerable<Transaction> others)
    {
        if (zero.Amount != 0) return null;
        var candidates = others.Where(t => IsCandidate(zero, t)).ToList();
        if (candidates.Count <= 1) return candidates.FirstOrDefault();
        var words = Tokens(zero.Name, zero.Purpose);
        var sharing = candidates.Where(c => Tokens(c.Name, c.Purpose).Overlaps(words)).ToList();
        return sharing.Count == 1 ? sharing[0] : null;
    }

    /// <summary>
    /// Gives the zero bookings of the accounts (booked between the timestamps; null: any time) their main booking's
    /// category, or takes it away when the main lost it. Tracks the changes without saving; returns how many changed.
    /// </summary>
    public static async Task<int> FollowAsync(BudgetDbContext db, IReadOnlyCollection<long>? accountIds, long? from, long? to, CancellationToken ct)
    {
        var lo = from - WindowSeconds;
        var hi = to + WindowSeconds;
        var zeros = await db.Transactions
            // an empty category follows even when it was cleared by hand; only a category the user chose stays
            .Where(t => t.Amount == 0 && t.AccountId != null
                && (t.CategoryId == null || t.CategorySetBy == TransactionCategorySource.Follow))
            .Where(t => accountIds == null || accountIds.Contains(t.AccountId!.Value))
            .Where(t => lo == null || t.Timestamp >= lo)
            .Where(t => hi == null || t.Timestamp <= hi)
            .ToListAsync(ct);
        var changed = 0;
        foreach (var zero in zeros)
        {
            var account = zero.AccountId;
            var start = zero.Timestamp - MaxDistanceSeconds;
            var end = zero.Timestamp + MaxDistanceSeconds;
            var nearby = await db.Transactions
                .Where(t => t.AccountId == account && t.Amount != 0 && t.Timestamp >= start && t.Timestamp <= end)
                .ToListAsync(ct);
            var main = FindMain(zero, nearby);
            var category = main?.CategoryId;
            var setBy = category == null ? TransactionCategorySource.Import : TransactionCategorySource.Follow;
            if (zero.CategoryId == category && zero.CategorySetBy == setBy) continue;
            zero.CategoryId = category;
            zero.CategorySetBy = setBy;
            zero.RuleId = null;
            changed++;
        }
        return changed;
    }

    /// <summary>The main booking of the zero booking <paramref name="id"/>, or null.</summary>
    public static async Task<Transaction?> FindMainAsync(BudgetDbContext db, long id, CancellationToken ct)
    {
        var zero = await db.Transactions.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id && t.Amount == 0, ct);
        if (zero?.AccountId is not { } account) return null;
        var start = zero.Timestamp - MaxDistanceSeconds;
        var end = zero.Timestamp + MaxDistanceSeconds;
        var nearby = await db.Transactions.AsNoTracking()
            .Where(t => t.AccountId == account && t.Amount != 0 && t.Timestamp >= start && t.Timestamp <= end)
            .ToListAsync(ct);
        return FindMain(zero, nearby);
    }
}
