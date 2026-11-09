using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Connectors.Sync;

public sealed record ApplyResult(int Inserted, int Adopted, int PendingStored, int AlreadyKnown);

/// <summary>
/// Writes what a connector reported into an account: deduplicated, so a sync can be repeated at will.
///
/// Rules: pending rows are deleted and re-created on every sync (only booked rows stay). A booked
/// booking is recognised by its bank transaction id; without one, by a stable hash of
/// date + amount + name + purpose plus an occurrence counter, so identical same-day bookings are kept
/// as separate rows (counts are matched, never collapsed). Rows that predate the connector (MoneyMoney
/// imports, no external id) are adopted instead of duplicated: first by amount + purpose + name on
/// the same day, then by amount + purpose within a day, then by amount alone within a day (texts differ
/// between MoneyMoney and the bank; only rows around the cutover are candidates). Row ids are allocated in the upper half of the
/// 40-bit rowid space of the account's source, where MoneyMoney imports never write.
/// </summary>
public sealed class BankTransactionStore
{
    /// <summary>First rowid of the range reserved for connector-written rows.</summary>
    public const long ConnectorRowBase = 1L << 39;

    // One writer at a time: id allocation reads the current maximum.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<ApplyResult> ApplyAsync(
        BudgetDbContext db, long accountId, AccountRefresh refresh, DateOnly? since, DateTime nowUtc, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            return await ApplyCoreAsync(db, accountId, refresh, since, nowUtc, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<ApplyResult> ApplyCoreAsync(
        BudgetDbContext db, long accountId, AccountRefresh refresh, DateOnly? since, DateTime nowUtc, CancellationToken ct)
    {
        var account = await db.Accounts.FirstAsync(a => a.Id == accountId, ct);
        var sourceId = account.SourceId;
        var remote = refresh.Transactions.Where(t => since == null || t.BookingDate >= since).ToList();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Pending rows are only ever a snapshot: drop them all, re-add what the bank reports now.
        await db.Transactions.Where(t => t.AccountId == accountId && !t.Booked).ExecuteDeleteAsync(ct);

        var lowerBound = since is { } s ? ToTimestamp(s) - 86400 : long.MinValue;
        var existing = await db.Transactions
            .Where(t => t.AccountId == accountId && t.Booked && t.Timestamp >= lowerBound)
            .ToListAsync(ct);
        var knownIds = existing.Where(t => t.ExternalId != null).Select(t => t.ExternalId).ToHashSet(StringComparer.Ordinal);
        var legacy = existing.Where(t => t.ExternalId == null).ToList();

        var nextId = (await db.Transactions
            .Where(t => t.SourceId == sourceId && t.Id >= SourceScope.Namespace(sourceId, ConnectorRowBase))
            .MaxAsync(t => (long?)t.Id, ct)) ?? SourceScope.Namespace(sourceId, ConnectorRowBase);

        int inserted = 0, adopted = 0, pending = 0, known = 0;
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var r in remote)
        {
            var cents = ToCents(r.Amount);
            var ts = ToTimestamp(r.BookingDate);

            if (!r.Booked)
            {
                db.Transactions.Add(NewRow(++nextId, sourceId, accountId, r, cents, ts, externalId: null));
                pending++;
                continue;
            }

            var key = $"{r.BookingDate:yyyyMMdd}|{cents.ToString(CultureInfo.InvariantCulture)}|{Normalize(r.Name)}|{Normalize(r.Purpose)}";
            var occurrence = occurrences.GetValueOrDefault(key);
            occurrences[key] = occurrence + 1;
            var externalId = !string.IsNullOrEmpty(r.BankTransactionId)
                ? "id:" + r.BankTransactionId
                : $"k:{Hash(key)}#{occurrence.ToString(CultureInfo.InvariantCulture)}";

            if (knownIds.Contains(externalId))
            {
                known++;
                continue;
            }

            var match = FindLegacyMatch(legacy, cents, ts, r);
            if (match != null)
            {
                legacy.Remove(match);
                match.ExternalId = externalId;
                knownIds.Add(externalId);
                adopted++;
                continue;
            }

            db.Transactions.Add(NewRow(++nextId, sourceId, accountId, r, cents, ts, externalId));
            knownIds.Add(externalId);
            inserted++;
        }

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

    private static Transaction? FindLegacyMatch(List<Transaction> legacy, long cents, long ts, RemoteTransaction r)
    {
        var purpose = Normalize(r.Purpose);
        var name = Normalize(r.Name);
        var exact = legacy.Find(t => t.Amount == cents && Math.Abs(t.Timestamp - ts) < 86400
            && Normalize(t.Purpose) == purpose && Normalize(t.Name) == name);
        if (exact != null) return exact;
        var byPurpose = purpose.Length == 0
            ? null
            : legacy.Find(t => t.Amount == cents && Math.Abs(t.Timestamp - ts) < 86400 && Normalize(t.Purpose) == purpose);
        if (byPurpose != null) return byPurpose;
        // Banks word bookings differently from MoneyMoney (Scalable does), so text often never matches. The legacy
        // rows considered here only cover the cutover window, where the same account, day and amount identify the
        // booking well enough; each legacy row is adopted once, so identical bookings are still matched by count.
        return legacy.Find(t => t.Amount == cents && Math.Abs(t.Timestamp - ts) < 86400);
    }

    private static Transaction NewRow(long id, int sourceId, long accountId, RemoteTransaction r, long cents, long ts, string? externalId) => new()
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
