using OpenBudget.Connectors.Receipts;
using OpenBudget.Models;

namespace OpenBudget.Connectors.Sync;

/// <summary>One split part of a pending booking: amount in cents, category, note and the receipt items it carries.</summary>
public sealed record PendingSplitPart(long Cents, long? CategoryId, string? Note, string? OrderItems = null);

/// <summary>A receipt linked to a pending booking (<see cref="ReceiptInvoice.TransactionId"/>) and its split signature.</summary>
public sealed record PendingReceiptLink(long InvoiceId, string? SplitSignature);

/// <summary>
/// What the user set on a pending booking, taken before a sync replaces the pending rows, and the receipts linked to it.
/// <see cref="ReceiptSplit"/>: the parts are exactly the split a linked receipt wrote, not the user's.
/// <see cref="SuggestedFrom"/>: the category is an unconfirmed suggestion (not the user's) of the pending row with that id.
/// </summary>
public sealed record PendingEdit(
    long Cents,
    long Timestamp,
    string? Name,
    string? Currency,
    string? BankReference,
    string? EndToEndReference,
    long? CategoryId,
    bool ManualCategory,
    string? Note,
    bool Checked,
    IReadOnlyList<PendingSplitPart> Parts,
    IReadOnlyList<long>? Receipts = null,
    bool ReceiptSplit = false,
    long? SuggestedFrom = null);

/// <summary>A booking a <see cref="PendingEdit"/> may move to.</summary>
public sealed record CarryCandidate(long Cents, long Timestamp, string? Name, string? BankReference, string? EndToEndReference);

/// <summary>
/// Keeps the user's edits of pending bookings across syncs. A sync replaces all pending rows; before that the edits are
/// taken (<see cref="Snapshot"/>) and afterwards each is moved to the new booking that is most likely the same one
/// (<see cref="Match"/>): same sign, amount within max(1.00, 15 %), payee alike, date from 3 days before to 7 days after.
/// A shared bank reference or end-to-end id matches first; the rest pair up closest amount first, then closest date,
/// each booking once. A split whose booking amount changed is scaled to the new amount (<see cref="Scale"/>) and flagged.
/// The receipts linked to a pending booking move with it; a split such a receipt wrote moves too but stays the receipt's:
/// it is scaled without a flag and the receipt brings it up to date for the new booking afterwards.
/// </summary>
public static class PendingCarryOver
{
    private const long DaySeconds = 86400;

    /// <summary>
    /// The edits of the pending rows; rows the user never touched and no receipt is linked to are left out.
    /// <paramref name="receipts"/>: the receipts linked to pending rows, by row id.
    /// </summary>
    public static List<PendingEdit> Snapshot(IReadOnlyList<Transaction> pendingRows, IReadOnlyDictionary<long, List<PendingReceiptLink>>? receipts = null)
    {
        var children = pendingRows.Where(t => t.Splitted == SplitState.Child && t.SplitParentId != null)
            .GroupBy(t => t.SplitParentId!.Value).ToDictionary(g => g.Key, g => g.OrderBy(c => c.Id).ToList());
        var edits = new List<PendingEdit>();
        foreach (var t in pendingRows.Where(t => t.Splitted != SplitState.Child))
        {
            var manual = t.CategorySetBy == TransactionCategorySource.Manual;
            var suggested = t.CategorySetBy == TransactionCategorySource.Suggested && t.CategoryId != null;
            var parts = t.Splitted == SplitState.Parent && children.TryGetValue(t.Id, out var c)
                ? c.Select(p => new PendingSplitPart(p.Amount, p.CategoryId, p.Note, p.OrderItems)).ToList()
                : [];
            var links = receipts?.GetValueOrDefault(t.Id) ?? [];
            if (!manual && !suggested && string.IsNullOrEmpty(t.Note) && !t.Checked && parts.Count == 0 && links.Count == 0) continue;
            var signature = parts.Count == 0 ? null : ReceiptCategorizer.Signature(parts.Select(p => (p.Cents, p.CategoryId)));
            edits.Add(new PendingEdit(t.SplitOriginalAmount ?? t.Amount, t.Timestamp, t.Name, t.Currency, t.BankReference, t.EndToEndReference,
                manual || suggested ? t.CategoryId : null, manual, t.Note, t.Checked, parts,
                links.Select(l => l.InvoiceId).ToList(), signature != null && links.Any(l => l.SplitSignature == signature),
                suggested ? t.Id : null));
        }
        return edits;
    }

    /// <summary>For each edit, the index of the candidate it moves to, or null when none fits.</summary>
    public static int?[] Match(IReadOnlyList<PendingEdit> edits, IReadOnlyList<CarryCandidate> candidates)
    {
        var result = new int?[edits.Count];
        var taken = new bool[candidates.Count];

        for (var e = 0; e < edits.Count; e++)
        {
            for (var c = 0; c < candidates.Count; c++)
            {
                if (taken[c] || !SameReference(edits[e], candidates[c]) || !Fits(edits[e], candidates[c])) continue;
                result[e] = c;
                taken[c] = true;
                break;
            }
        }

        var pairs = new List<(int E, int C, long AmountDiff, long DateDiff)>();
        for (var e = 0; e < edits.Count; e++)
        {
            if (result[e] != null) continue;
            for (var c = 0; c < candidates.Count; c++)
            {
                if (taken[c] || !Fits(edits[e], candidates[c]) || !SimilarPayee(edits[e].Name, candidates[c].Name)) continue;
                pairs.Add((e, c, Math.Abs(candidates[c].Cents - edits[e].Cents), Math.Abs(candidates[c].Timestamp - edits[e].Timestamp)));
            }
        }
        foreach (var (e, c, _, _) in pairs.OrderBy(p => p.AmountDiff).ThenBy(p => p.DateDiff).ThenBy(p => p.E).ThenBy(p => p.C))
        {
            if (result[e] != null || taken[c]) continue;
            result[e] = c;
            taken[c] = true;
        }
        return result;
    }

    private static bool SameReference(PendingEdit e, CarryCandidate c) =>
        (Usable(e.BankReference) && string.Equals(e.BankReference, c.BankReference, StringComparison.Ordinal))
        || (Usable(e.EndToEndReference) && string.Equals(e.EndToEndReference, c.EndToEndReference, StringComparison.Ordinal));

    private static bool Usable(string? reference) =>
        !string.IsNullOrWhiteSpace(reference) && !string.Equals(reference, "NOTPROVIDED", StringComparison.OrdinalIgnoreCase);

    private static bool Fits(PendingEdit e, CarryCandidate c)
    {
        if (Math.Sign(e.Cents) != Math.Sign(c.Cents)) return false;
        var tolerance = Math.Max(100, Math.Abs(e.Cents) * 15 / 100);
        if (Math.Abs(c.Cents - e.Cents) > tolerance) return false;
        var shift = c.Timestamp - e.Timestamp;
        return shift >= -3 * DaySeconds && shift <= 7 * DaySeconds;
    }

    /// <summary>Legal forms and filler words that say nothing about who the payee is.</summary>
    private static readonly HashSet<string> GenericTokens = new(StringComparer.Ordinal)
    {
        "GMBH", "AG", "KG", "CO", "OHG", "UG", "EV", "E", "V", "SE", "LTD", "INC", "UND", "THE", "DER", "DIE", "DAS",
        "WWW", "DE", "COM", "ZAHLUNG", "KARTENZAHLUNG", "LASTSCHRIFT", "UEBERWEISUNG", "ÜBERWEISUNG", "SEPA", "BANK",
        "PAY", "PAYMENT", "ONLINE", "SHOP", "STORE", "MARKT",
    };

    /// <summary>Equal once normalised, or sharing a word of four or more characters that is not generic or a number.</summary>
    public static bool SimilarPayee(string? a, string? b)
    {
        var ta = Tokens(a);
        var tb = Tokens(b);
        if (ta.Count == 0 || tb.Count == 0) return ta.Count == tb.Count;
        if (string.Join(' ', ta) == string.Join(' ', tb)) return true;
        return ta.Any(t => t.Length >= 4 && !GenericTokens.Contains(t) && !t.All(char.IsDigit) && tb.Contains(t));
    }

    private static List<string> Tokens(string? text) =>
        new string((text ?? "").ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToList();

    /// <summary>
    /// The parts scaled from their old total to <paramref name="newTotal"/> (cents, rounded); the rounding difference
    /// goes to the largest part.
    /// </summary>
    public static long[] Scale(IReadOnlyList<long> parts, long newTotal)
    {
        var oldTotal = parts.Sum();
        if (oldTotal == 0 || oldTotal == newTotal) return [.. parts];
        var scaled = parts.Select(p => (long)Math.Round((decimal)p * newTotal / oldTotal, MidpointRounding.AwayFromZero)).ToArray();
        var largest = 0;
        for (var i = 1; i < parts.Count; i++)
            if (Math.Abs(parts[i]) > Math.Abs(parts[largest])) largest = i;
        scaled[largest] += newTotal - scaled.Sum();
        return scaled;
    }

    /// <summary>
    /// Puts <paramref name="edit"/> on <paramref name="target"/> (a new, unsaved row): category (when chosen by hand),
    /// note, check mark and split. Returns the split parts to add; ids come from <paramref name="nextId"/>. A receipt's
    /// split (<see cref="PendingEdit.ReceiptSplit"/>) is not flagged when the amount changed: its receipt re-splits it.
    /// </summary>
    public static List<Transaction> Apply(PendingEdit edit, Transaction target, Func<long> nextId)
    {
        if (edit.ManualCategory)
        {
            target.CategoryId = edit.CategoryId;
            target.CategorySetBy = TransactionCategorySource.Manual;
            target.RuleId = null;
        }
        else if (edit.SuggestedFrom != null && target.CategoryId == null)
        {
            // A suggestion stays one (a rule that matched the new row wins).
            target.CategoryId = edit.CategoryId;
            target.CategorySetBy = TransactionCategorySource.Suggested;
        }
        if (!string.IsNullOrEmpty(edit.Note)) target.Note = edit.Note;
        if (edit.Checked) target.Checked = true;
        if (edit.Parts.Count == 0) return [];

        var amounts = Scale(edit.Parts.Select(p => p.Cents).ToList(), target.Amount);
        var changed = !edit.ReceiptSplit && edit.Parts.Sum(p => p.Cents) != target.Amount;
        target.Splitted = SplitState.Parent;
        target.SplitOriginalAmount = target.Amount;
        target.SplitCheck = changed;
        return edit.Parts.Select((p, i) => new Transaction
        {
            Id = nextId(),
            OwnerUserId = target.OwnerUserId,
            AccountId = target.AccountId,
            Amount = amounts[i],
            Currency = target.Currency,
            Timestamp = target.Timestamp,
            ValueTimestamp = target.ValueTimestamp,
            Name = target.Name,
            Purpose = target.Purpose,
            BookingText = target.BookingText,
            Booked = target.Booked,
            Checked = target.Checked,
            Checkmark = 0,
            Splitted = SplitState.Child,
            SplitParentId = target.Id,
            CategoryId = p.CategoryId,
            CategorySetBy = TransactionCategorySource.Manual,
            Note = p.Note,
            OrderItems = p.OrderItems,
            IsNew = target.IsNew || changed,
            SplitCheck = changed,
        }).ToList();
    }
}
