using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>A booking the pairing looks at: booked, not split, on an account of the owner that is no depot.</summary>
public sealed record TransferCandidate(
    long Id, long AccountId, long Amount, string Currency, DateOnly Date, string? Name, string? Purpose, string? CounterpartyIban);

/// <summary>An account as the pairing sees it.</summary>
public sealed record TransferAccount(long Id, string? Iban, string? Name);

/// <summary>
/// Pairs the two sides of a move between an owner's own accounts (<see cref="Transaction.TransferPeerId"/>). Two
/// booked bookings pair when they lie on different accounts of the same owner, carry opposite amounts of exactly the
/// same size in the same currency, are at most <see cref="MaxDaysApart"/> days apart, nothing contradicts the pairing
/// and something supports it:
/// <list type="bullet">
/// <item>Contradiction: a booking's counterparty IBAN differs from the other account's IBAN, or (when the other account
/// has none) names a different account of the owner.</item>
/// <item>Support, strongest first: a booking's counterparty IBAN is the other account's IBAN; or its payee or purpose
/// names the other account (its IBAN, or its name as a whole word of at least <see cref="MinNameLength"/> letters).</item>
/// </list>
/// A booking pairs only with its one best-supported candidate, and only when that candidate's best is this booking as
/// well; anything ambiguous stays unpaired. Pending bookings never pair: the bank replaces them on booking. Bookings the
/// user unpaired (<see cref="TransferPairState.Declined"/>) are never paired again, and pairs the user made
/// (<see cref="TransferPairState.Manual"/>) are kept as long as both bookings exist.
/// </summary>
public static class TransferPairing
{
    /// <summary>The most days the two sides of a transfer may be booked apart.</summary>
    public const int MaxDaysApart = 4;

    /// <summary>The shortest account name that counts as a reference in a payee or purpose.</summary>
    public const int MinNameLength = 4;

    private enum Support { None = 0, Text = 1, Iban = 2 }

    /// <summary>
    /// The pairs (outgoing id, incoming id) among <paramref name="rows"/>; see the class remarks. Bookings with a
    /// supported candidate that stay unpaired because the choice is ambiguous go to <paramref name="ambiguous"/>.
    /// </summary>
    public static List<(long Out, long In)> Match(
        IReadOnlyList<TransferCandidate> rows, IReadOnlyDictionary<long, TransferAccount> accounts, ICollection<long>? ambiguous = null)
    {
        var ownIbans = accounts.Values.Select(a => Iban.Normalize(a.Iban)).Where(i => i.Length > 0).ToHashSet(StringComparer.Ordinal);
        var incoming = rows.Where(r => r.Amount > 0).ToLookup(r => (r.Amount, r.Currency));
        var edges = new List<(TransferCandidate Out, TransferCandidate In, Support Support)>();
        foreach (var o in rows.Where(r => r.Amount < 0))
        {
            foreach (var i in incoming[(-o.Amount, o.Currency)])
            {
                if (i.AccountId == o.AccountId || Math.Abs(i.Date.DayNumber - o.Date.DayNumber) > MaxDaysApart) continue;
                if (!accounts.TryGetValue(o.AccountId, out var oa) || !accounts.TryGetValue(i.AccountId, out var ia)) continue;
                if (Evaluate(o, ia, ownIbans) is not { } fromOut || Evaluate(i, oa, ownIbans) is not { } fromIn) continue;
                var support = fromOut > fromIn ? fromOut : fromIn;
                if (support != Support.None) edges.Add((o, i, support));
            }
        }

        // Each side's best support and how many candidates reach it.
        var best = new Dictionary<long, (Support Support, int Count)>();
        void Note(long id, Support s)
        {
            var b = best.GetValueOrDefault(id);
            best[id] = s > b.Support ? (s, 1) : s == b.Support ? (s, b.Count + 1) : b;
        }
        foreach (var e in edges)
        {
            Note(e.Out.Id, e.Support);
            Note(e.In.Id, e.Support);
        }
        var pairs = edges
            .Where(e => best[e.Out.Id] == (e.Support, 1) && best[e.In.Id] == (e.Support, 1))
            .Select(e => (Out: e.Out.Id, In: e.In.Id))
            .ToList();
        if (ambiguous != null)
        {
            var paired = pairs.SelectMany(p => new[] { p.Out, p.In }).ToHashSet();
            foreach (var id in best.Keys.Where(id => !paired.Contains(id))) ambiguous.Add(id);
        }
        return pairs;
    }

    /// <summary>
    /// What <paramref name="row"/> says about being the other side's counterpart on <paramref name="other"/>: null when
    /// it contradicts it.
    /// </summary>
    private static Support? Evaluate(TransferCandidate row, TransferAccount other, HashSet<string> ownIbans)
    {
        var counterparty = Iban.Normalize(row.CounterpartyIban);
        var otherIban = Iban.Normalize(other.Iban);
        if (counterparty.Length > 0)
        {
            if (otherIban.Length > 0) return counterparty == otherIban ? Support.Iban : null;
            if (ownIbans.Contains(counterparty)) return null;
        }
        return RefersTo(row, other, otherIban) ? Support.Text : Support.None;
    }

    private static bool RefersTo(TransferCandidate row, TransferAccount other, string otherIban)
    {
        var text = $"{row.Name} {row.Purpose}";
        if (otherIban.Length > 0 && Iban.Normalize(text).Contains(otherIban, StringComparison.Ordinal)) return true;
        var name = other.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Count(char.IsLetter) < MinNameLength) return false;
        for (var at = text.IndexOf(name, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(name, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = at + name.Length;
            if ((at == 0 || !char.IsLetterOrDigit(text[at - 1])) && (end == text.Length || !char.IsLetterOrDigit(text[end]))) return true;
        }
        return false;
    }

    /// <summary>
    /// Brings the pairs of <paramref name="ownerId"/>'s bookings (null: every owner) up to date: drops pairs whose other
    /// side is gone or no longer fits, then pairs what <see cref="Match"/> finds. Saves; returns the number of new pairs.
    /// </summary>
    public static async Task<int> PairAsync(BudgetDbContext db, string? ownerId, CancellationToken ct)
    {
        var owners = ownerId != null
            ? [ownerId]
            : await db.Transactions.Where(t => t.AccountId != null).Select(t => t.OwnerUserId).Distinct().ToListAsync(ct);
        var paired = 0;
        foreach (var owner in owners)
        {
            await DropBrokenPairsAsync(db, owner, ct);
            paired += await PairOwnerAsync(db, owner, ct);
        }
        return paired;
    }

    private static async Task DropBrokenPairsAsync(BudgetDbContext db, string owner, CancellationToken ct)
    {
        var rows = await db.Transactions.Where(t => t.OwnerUserId == owner && t.TransferPeerId != null).ToListAsync(ct);
        if (rows.Count == 0) return;
        var byId = rows.ToDictionary(r => r.Id);
        foreach (var r in rows)
        {
            var fits = byId.TryGetValue(r.TransferPeerId!.Value, out var peer) && peer.TransferPeerId == r.Id
                && peer.AccountId != r.AccountId && IsWhole(r) && IsWhole(peer)
                && (r.TransferState == TransferPairState.Manual || (r.Booked && peer.Booked && r.Amount == -peer.Amount && r.Amount != 0));
            if (fits) continue;
            r.TransferPeerId = null;
            if (r.TransferState == TransferPairState.Manual) r.TransferState = TransferPairState.Auto;
        }
        await db.SaveChangesAsync(ct);
    }

    private static bool IsWhole(Transaction t) => t.Splitted is null or SplitState.Normal;

    private static async Task<int> PairOwnerAsync(BudgetDbContext db, string owner, CancellationToken ct)
    {
        var accounts = (await db.Accounts.AsNoTracking().Where(a => a.OwnerUserId == owner).WhereNotDepot()
                .Select(a => new { a.Id, a.Iban, a.Name, a.Currency }).ToListAsync(ct))
            .ToDictionary(a => a.Id);
        if (accounts.Count < 2) return 0;
        var ids = accounts.Keys.ToList();
        var rows = await db.Transactions.AsNoTracking()
            .Where(t => t.OwnerUserId == owner && t.AccountId != null && ids.Contains(t.AccountId.Value) && t.Booked && t.Amount != 0
                && (t.Splitted == null || t.Splitted == SplitState.Normal)
                && t.TransferPeerId == null && t.TransferState == TransferPairState.Auto)
            .Select(t => new { t.Id, AccountId = t.AccountId!.Value, t.Amount, t.Currency, t.Timestamp, t.Name, t.Purpose, t.CounterpartyIban })
            .ToListAsync(ct);
        var candidates = rows.Select(r => new TransferCandidate(r.Id, r.AccountId, r.Amount,
            (r.Currency ?? accounts[r.AccountId].Currency ?? "").Trim().ToUpperInvariant(),
            DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(r.Timestamp).UtcDateTime),
            r.Name, r.Purpose, r.CounterpartyIban)).ToList();
        var pairs = Match(candidates, accounts.ToDictionary(a => a.Key, a => new TransferAccount(a.Key, a.Value.Iban, a.Value.Name)));
        foreach (var chunk in pairs.Chunk(250))
        {
            var chunkIds = chunk.SelectMany(p => new[] { p.Out, p.In }).ToList();
            var tracked = await db.Transactions.Where(t => chunkIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
            foreach (var (o, i) in chunk)
            {
                tracked[o].TransferPeerId = i;
                tracked[i].TransferPeerId = o;
            }
        }
        if (pairs.Count > 0) await db.SaveChangesAsync(ct);
        return pairs.Count;
    }
}
