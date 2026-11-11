using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Connectors.Sync;

public sealed record ApplyResult(int Inserted, int Adopted, int PendingStored, int AlreadyKnown);

/// <summary>
/// What applying a bank's report to an account would do, without writing: <see cref="Adopted"/> local rows without a bank
/// identity (MoneyMoney's) are the same booking as a bank row and become that row; <see cref="Inserted"/> bank rows are
/// new; <see cref="AlreadyKnown"/> were stored before; <see cref="UnmatchedLocal"/> booked local rows in the compared
/// window have no bank counterpart and stay as they are; <see cref="LocalPendingReplaced"/> pending local rows are
/// replaced by the bank's pending rows (<see cref="PendingStored"/>).
/// </summary>
public sealed record ApplyPreview(int Adopted, int Inserted, int AlreadyKnown, int PendingStored, int UnmatchedLocal, int LocalPendingReplaced);

/// <summary>
/// Writes what a connector reported into an account: deduplicated, so a sync can be repeated at will.
///
/// Rules: pending rows are deleted and re-created on every sync (only booked rows stay). A booked
/// booking is recognised by its bank transaction id; without one, by a stable hash of
/// date + amount + name + purpose plus an occurrence counter, so identical same-day bookings are kept
/// as separate rows (counts are matched, never collapsed). Rows that predate the connector (MoneyMoney
/// imports, no external id) are adopted instead of duplicated, in three passes over all bank rows: amount + purpose +
/// name on the same day, then amount + purpose within a day, then amount alone within a day (texts differ between
/// MoneyMoney and the bank; only rows from <c>since</c> on are candidates). Candidates are taken oldest first, so the
/// outcome is deterministic. An adopted row is the bank's from then on: the bank's texts, dates and details replace
/// MoneyMoney's, while what the user set on it (category, note, check mark, splits) and its id stay.
/// Row ids are allocated in the upper half of the 40-bit rowid space of the account's source, where MoneyMoney imports
/// never write.
/// </summary>
public sealed class BankTransactionStore
{
    /// <summary>First rowid of the range reserved for connector-written rows.</summary>
    public const long ConnectorRowBase = 1L << 39;

    // One writer at a time: id allocation reads the current maximum.
    // Also taken by SplitService, which allocates split-part ids from the same range.
    internal static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Applies a connector's report to the account (see the class remarks).</summary>
    /// <param name="markNew">
    /// Whether inserted booked rows carry the "new" marker. False for an account's first sync and for a switch from
    /// MoneyMoney: those rows are the account's history, not news.
    /// </param>
    public async Task<ApplyResult> ApplyAsync(
        BudgetDbContext db, long accountId, AccountRefresh refresh, DateOnly? since, DateTime nowUtc, CancellationToken ct, bool markNew = true)
    {
        await Gate.WaitAsync(ct);
        try
        {
            return await ApplyCoreAsync(db, accountId, refresh, since, nowUtc, markNew, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>What <see cref="ApplyAsync"/> would do with the same input; reads only.</summary>
    public static async Task<ApplyPreview> PreviewAsync(BudgetDbContext db, long accountId, AccountRefresh refresh, DateOnly? since, CancellationToken ct)
    {
        var remote = refresh.Transactions.Where(t => since == null || t.BookingDate >= since).ToList();
        var lowerBound = LowerBound(since);
        var localPending = await db.Transactions.CountAsync(t => t.AccountId == accountId && !t.Booked, ct);
        var existing = await db.Transactions.AsNoTracking()
            .Where(t => t.AccountId == accountId && t.Booked && t.Timestamp >= lowerBound)
            .ToListAsync(ct);
        var plan = Plan(existing, remote);
        var adopted = plan.Count(p => p.Adopt != null);
        return new ApplyPreview(
            adopted,
            plan.Count(p => p.Remote.Booked && p.Adopt == null && !p.Known),
            plan.Count(p => p.Known),
            plan.Count(p => !p.Remote.Booked),
            Candidates(existing).Count - adopted,
            localPending);
    }

    private static long LowerBound(DateOnly? since) => since is { } s ? ToTimestamp(s) - 86400 : long.MinValue;

    private static async Task<ApplyResult> ApplyCoreAsync(
        BudgetDbContext db, long accountId, AccountRefresh refresh, DateOnly? since, DateTime nowUtc, bool markNew, CancellationToken ct)
    {
        var account = await db.Accounts.FirstAsync(a => a.Id == accountId, ct);
        var sourceId = account.SourceId;
        var remote = refresh.Transactions.Where(t => since == null || t.BookingDate >= since).ToList();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Pending rows are only ever a snapshot: drop them all, re-add what the bank reports now.
        await db.Transactions.Where(t => t.AccountId == accountId && !t.Booked).ExecuteDeleteAsync(ct);

        var lowerBound = LowerBound(since);
        var existing = await db.Transactions
            .Where(t => t.AccountId == accountId && t.Booked && t.Timestamp >= lowerBound)
            .ToListAsync(ct);

        var nextId = (await db.Transactions
            .Where(t => t.SourceId == sourceId && t.Id >= SourceScope.Namespace(sourceId, ConnectorRowBase))
            .MaxAsync(t => (long?)t.Id, ct)) ?? SourceScope.Namespace(sourceId, ConnectorRowBase);

        int inserted = 0, adopted = 0, pending = 0, known = 0;
        var added = new List<Transaction>();

        foreach (var p in Plan(existing, remote))
        {
            var r = p.Remote;
            if (!r.Booked)
            {
                var pendingRow = NewRow(++nextId, sourceId, accountId, r, p.Cents, p.Timestamp, externalId: null, isNew: false);
                db.Transactions.Add(pendingRow);
                added.Add(pendingRow);
                pending++;
            }
            else if (p.Known)
            {
                // Rows stored before a detail field was read keep their data; only empty detail fields are filled in.
                if (existing.Find(t => t.ExternalId == p.ExternalId) is { } stored) FillMissingDetails(stored, r);
                known++;
            }
            else if (p.Adopt is { } match)
            {
                AdoptBankFields(match, r, p.Timestamp, p.ExternalId!);
                adopted++;
            }
            else
            {
                var row = NewRow(++nextId, sourceId, accountId, r, p.Cents, p.Timestamp, p.ExternalId, isNew: markNew);
                db.Transactions.Add(row);
                added.Add(row);
                inserted++;
            }
        }

        // Categorisation rules of the account's projects categorise what arrived (new rows have no category yet;
        // adopted rows keep theirs).
        await CategoryRuleEngine.ApplyToNewAsync(db, accountId, added, ct);

        if (refresh.Positions.Count > 0 || account.Type == 7)
            await ReplaceHoldingsAsync(db, account, refresh.Positions, ct);

        if (refresh.Balance is { } balance)
            account.Balances = $"11:{balance.ToString("F6", CultureInfo.InvariantCulture)},3:{account.Currency ?? "EUR"},";
        account.RefreshTimestamp = new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        account.Error = null;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new ApplyResult(inserted, adopted, pending, known);
    }

    /// <summary>One bank row and what happens to it: stored before (<see cref="Known"/>), adopts a local row, or is new.</summary>
    private sealed record PlannedRow(RemoteTransaction Remote, long Cents, long Timestamp, string? ExternalId, bool Known)
    {
        public Transaction? Adopt { get; set; }
    }

    /// <summary>Local rows a bank row may adopt: booked, no bank identity, not a split part OpenBudget created; oldest first.</summary>
    private static List<Transaction> Candidates(IEnumerable<Transaction> existing) =>
        existing.Where(t => t.ExternalId == null && t.Splitted != SplitState.Child)
            .OrderBy(t => t.Timestamp).ThenBy(t => t.Id).ToList();

    private static List<PlannedRow> Plan(List<Transaction> existing, List<RemoteTransaction> remote)
    {
        var knownIds = existing.Where(t => t.ExternalId != null).Select(t => t.ExternalId).ToHashSet(StringComparer.Ordinal);
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var plan = new List<PlannedRow>(remote.Count);
        foreach (var r in remote)
        {
            var cents = ToCents(r.Amount);
            var ts = ToTimestamp(r.BookingDate);
            if (!r.Booked)
            {
                plan.Add(new PlannedRow(r, cents, ts, null, false));
                continue;
            }
            var key = $"{r.BookingDate:yyyyMMdd}|{cents.ToString(CultureInfo.InvariantCulture)}|{Normalize(r.Name)}|{Normalize(r.Purpose)}";
            var occurrence = occurrences.GetValueOrDefault(key);
            occurrences[key] = occurrence + 1;
            var externalId = !string.IsNullOrEmpty(r.BankTransactionId)
                ? "id:" + r.BankTransactionId
                : $"k:{Hash(key)}#{occurrence.ToString(CultureInfo.InvariantCulture)}";
            plan.Add(new PlannedRow(r, cents, ts, externalId, !knownIds.Add(externalId)));
        }

        // Each pass looks at every still-unmatched bank row before the next, looser pass starts, so a loose match never
        // takes a local row a stricter one would have claimed.
        var legacy = Candidates(existing);
        var open = plan.Where(p => p.Remote.Booked && !p.Known).ToList();
        var passes = new Func<PlannedRow, Transaction, bool>[]
        {
            (p, t) => Normalize(t.Purpose) == Normalize(p.Remote.Purpose) && Normalize(t.Name) == Normalize(p.Remote.Name),
            (p, t) => Normalize(p.Remote.Purpose).Length > 0 && Normalize(t.Purpose) == Normalize(p.Remote.Purpose),
            // Banks word bookings differently from MoneyMoney (Scalable does), so text often never matches. The legacy
            // rows considered here only cover the compared window, where the same account, day and amount identify the
            // booking well enough; each legacy row is adopted once, so identical bookings are still matched by count.
            (_, _) => true,
        };
        foreach (var pass in passes)
        {
            foreach (var p in open.Where(p => p.Adopt == null))
            {
                var match = legacy.Find(t => (t.SplitOriginalAmount ?? t.Amount) == p.Cents && Math.Abs(t.Timestamp - p.Timestamp) < 86400 && pass(p, t));
                if (match == null) continue;
                legacy.Remove(match);
                p.Adopt = match;
            }
        }
        return plan;
    }

    /// <summary>The bank's row wins: its texts, dates and details replace the local row's; the user's fields stay.</summary>
    private static void AdoptBankFields(Transaction t, RemoteTransaction r, long ts, string externalId)
    {
        t.ExternalId = externalId;
        t.Timestamp = ts;
        t.Name = r.Name ?? t.Name;
        t.Purpose = r.Purpose ?? t.Purpose;
        t.Currency = r.Currency;
        t.ValueTimestamp = r.ValueDate is { } v ? ToTimestamp(v) : t.ValueTimestamp;
        t.CounterpartyIban = r.CounterpartyIban ?? t.CounterpartyIban;
        t.CounterpartyBic = r.CounterpartyBic ?? t.CounterpartyBic;
        t.BookingText = r.BookingText ?? t.BookingText;
        t.BankReference = r.BankTransactionId ?? t.BankReference;
        t.Booked = true;
        if (r.Sepa is not { } sepa) return;
        t.EndToEndReference = sepa.EndToEndReference ?? t.EndToEndReference;
        t.MandateReference = sepa.MandateReference ?? t.MandateReference;
        t.CreditorId = sepa.CreditorId ?? t.CreditorId;
        t.TransactionCode = sepa.TransactionCode ?? t.TransactionCode;
        t.Primanota = sepa.Primanota ?? t.Primanota;
        t.ReturnReason = sepa.ReturnReason ?? t.ReturnReason;
    }

    private static void FillMissingDetails(Transaction t, RemoteTransaction r)
    {
        t.CounterpartyIban ??= r.CounterpartyIban;
        t.CounterpartyBic ??= r.CounterpartyBic;
        t.BookingText ??= r.BookingText;
        if (r.Sepa is not { } sepa) return;
        t.EndToEndReference ??= sepa.EndToEndReference;
        t.MandateReference ??= sepa.MandateReference;
        t.CreditorId ??= sepa.CreditorId;
        t.TransactionCode ??= sepa.TransactionCode;
        t.Primanota ??= sepa.Primanota;
        t.ReturnReason ??= sepa.ReturnReason;
    }

    private static Transaction NewRow(long id, int sourceId, long accountId, RemoteTransaction r, long cents, long ts, string? externalId, bool isNew) => new()
    {
        Id = id,
        SourceId = sourceId,
        AccountId = accountId,
        Amount = cents,
        Currency = r.Currency,
        Timestamp = ts,
        Name = r.Name,
        Purpose = r.Purpose,
        Booked = r.Booked,
        Splitted = 0,
        Checkmark = 0,
        ExternalId = externalId,
        // A booked row a connector inserts in a regular sync is "unread" until the user views or marks it; pending rows
        // are only a snapshot that is rebuilt on every sync, so they never carry the marker.
        IsNew = isNew && r.Booked,
        ValueTimestamp = r.ValueDate is { } v ? ToTimestamp(v) : null,
        CounterpartyIban = r.CounterpartyIban,
        CounterpartyBic = r.CounterpartyBic,
        BookingText = r.BookingText,
        BankReference = r.BankTransactionId,
        EndToEndReference = r.Sepa?.EndToEndReference,
        MandateReference = r.Sepa?.MandateReference,
        CreditorId = r.Sepa?.CreditorId,
        TransactionCode = r.Sepa?.TransactionCode,
        Primanota = r.Sepa?.Primanota,
        ReturnReason = r.Sepa?.ReturnReason,
    };

    private static async Task ReplaceHoldingsAsync(BudgetDbContext db, Account account, IReadOnlyList<RemotePosition> positions, CancellationToken ct)
    {
        await db.Holdings.Where(h => h.AccountId == account.Id).ExecuteDeleteAsync(ct);
        var baseId = SourceScope.Namespace(account.SourceId, ConnectorRowBase);
        var next = (await db.Holdings.Where(h => h.SourceId == account.SourceId && h.Id >= baseId).MaxAsync(h => (long?)h.Id, ct)) ?? baseId;
        foreach (var p in positions)
        {
            db.Holdings.Add(new Holding
            {
                Id = ++next,
                SourceId = account.SourceId,
                AccountId = account.Id,
                Name = p.Name,
                Isin = p.Isin,
                Wkn = p.Wkn,
                Symbol = p.Symbol,
                Quantity = (double)p.Quantity,
                Amount = (double?)p.Value,
                AmountCurrency = p.PriceCurrency ?? account.Currency,
                Price = (double?)p.Price,
                PriceCurrency = p.PriceCurrency,
                Purchase = (double?)p.PurchasePrice,
                AssetClass = 6,
            });
        }
    }

    public static long ToCents(decimal amount) => (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);

    public static long ToTimestamp(DateOnly date) =>
        new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();

    private static string Normalize(string? text) =>
        string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private static string Hash(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];
}
