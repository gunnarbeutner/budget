using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// A detected series as the Subscriptions tab shows it, with its account and category, whether the user dismissed it,
/// the day the user cancelled it (null = not cancelled) and the charges booked after that day plus
/// <see cref="RecurringPaymentService.CancellationGraceDays"/>.
/// </summary>
public sealed record RecurringEntry(
    RecurringSeries Series,
    string AccountName,
    string AccountIcon,
    string? AccountColor,
    string? CategoryName,
    bool Dismissed,
    DateOnly? CancelledOn = null,
    IReadOnlyList<RecurringBooking>? ChargedAfterCancellation = null)
{
    public bool Cancelled => CancelledOn != null;
}

/// <summary>
/// The detected series per user, project and day, kept until data that feeds them changes (bookings, accounts, projects,
/// bank syncs). Detection reads a few years of bookings, so the views that use it (Recurring, Scheduled, Cashflow) share
/// one result.
/// </summary>
public sealed class RecurringPaymentCache : IDisposable
{
    private readonly ConcurrentDictionary<(string User, int Project, DateOnly Today), List<RecurringSeries>> _entries = new();
    private readonly ChangeNotificationService _notifier;

    public RecurringPaymentCache(ChangeNotificationService notifier)
    {
        _notifier = notifier;
        _notifier.Changed += OnChanged;
    }

    private void OnChanged(DataChange change, Guid? origin)
    {
        if (change.Area is DataArea.ImportedData or DataArea.BankSync or DataArea.Projects or DataArea.TrackingItems) _entries.Clear();
    }

    /// <summary>Forgets every result (data written behind the app's back, such as a test seed).</summary>
    public void Clear() => _entries.Clear();

    internal bool TryGet((string, int, DateOnly) key, out List<RecurringSeries> series) => _entries.TryGetValue(key, out series!);

    internal void Set((string, int, DateOnly) key, List<RecurringSeries> series) => _entries[key] = series;

    public void Dispose() => _notifier.Changed -= OnChanged;
}

/// <summary>
/// Recurring charges of a project, detected from the bookings of its accounts (<see cref="RecurringPaymentDetector"/>):
/// on an Exclusive account all of them, on a Shared account those in the project's categories and uncategorised ones. Reads through the
/// user-scoped context, so only bookings the user may see count. Series the user dismissed or cancelled are flagged
/// (<see cref="RecurringEntry.Dismissed"/>, <see cref="RecurringEntry.CancelledOn"/>) or, for Scheduled and Cashflow,
/// left out (<see cref="ActiveAsync"/>; a series cancelled for a later day is expected until that day).
/// </summary>
public class RecurringPaymentService
{
    /// <summary>Days after a cancellation in which a charge is still taken as one already in flight, not as a warning.</summary>
    public const int CancellationGraceDays = 3;

    /// <summary>How far back bookings are read: enough for three yearly charges.</summary>
    public const int HistoryYears = 4;

    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly CategoryHierarchyService _hierarchy;
    private readonly ProjectService _projects;
    private readonly AccessScopeProvider? _access;
    private readonly RecurringPaymentCache? _cache;
    private readonly ChangeNotificationService? _notifier;
    private readonly ChangeOrigin? _origin;

    public RecurringPaymentService(IDbContextFactory<BudgetDbContext> dbFactory, CategoryHierarchyService hierarchy, ProjectService projects,
        AccessScopeProvider? access = null, RecurringPaymentCache? cache = null, ChangeNotificationService? notifier = null, ChangeOrigin? origin = null)
    {
        _dbFactory = dbFactory;
        _hierarchy = hierarchy;
        _projects = projects;
        _access = access;
        _cache = cache;
        _notifier = notifier;
        _origin = origin;
    }

    /// <summary>All series of the project, most expensive per month first, with the dismissed and cancelled ones flagged.</summary>
    public async Task<List<RecurringEntry>> GetAsync(int projectId, DateOnly today, CancellationToken ct = default)
    {
        var series = await DetectAsync(projectId, today, ct);
        if (series.Count == 0) return [];
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var marks = await MarksAsync(db, ct);
        var accountIds = series.Select(s => s.AccountId).Distinct().ToList();
        var accounts = await db.Accounts.AsNoTracking().Where(a => accountIds.Contains(a.Id))
            .Select(a => new Account { Id = a.Id, Name = a.Name, Icon = a.Icon, Color = a.Color, Type = a.Type, Portfolio = a.Portfolio }).ToDictionaryAsync(a => a.Id, ct);
        var categoryIds = series.Where(s => s.CategoryId != null).Select(s => s.CategoryId!.Value).Distinct().ToList();
        var categories = await db.Categories.AsNoTracking().Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        return series.Where(s => accounts.ContainsKey(s.AccountId)).Select(s =>
        {
            var a = accounts[s.AccountId];
            var hasMark = marks.TryGetValue(s.Key, out var cancelledOn);
            return new RecurringEntry(s, a.Name ?? "?", AccountIcons.ForAccount(a), a.Color,
                s.CategoryId is { } c ? categories.GetValueOrDefault(c) : null, hasMark && cancelledOn == null, cancelledOn,
                cancelledOn is { } on ? ChargedAfter(s, on) : []);
        }).ToList();
    }

    /// <summary>The series' charges booked after <paramref name="cancelledOn"/> plus the grace days.</summary>
    public static IReadOnlyList<RecurringBooking> ChargedAfter(RecurringSeries series, DateOnly cancelledOn) =>
        series.Bookings.Where(b => b.Date > cancelledOn.AddDays(CancellationGraceDays)).OrderBy(b => b.Date).ToList();

    /// <summary>
    /// The active series of the project the user has neither dismissed nor cancelled, optionally only those on
    /// <paramref name="accountIds"/>. A series cancelled for a day after <paramref name="today"/> stays, ending that day
    /// (<see cref="RecurringSeries.EndsOn"/>).
    /// </summary>
    public async Task<List<RecurringSeries>> ActiveAsync(int projectId, DateOnly today, IReadOnlyCollection<long>? accountIds = null, CancellationToken ct = default)
    {
        var series = await DetectAsync(projectId, today, ct);
        series = series.Where(s => s.Status == RecurringStatus.Active && (accountIds == null || accountIds.Contains(s.AccountId))).ToList();
        if (series.Count == 0) return series;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var marks = await MarksAsync(db, ct);
        var result = new List<RecurringSeries>();
        foreach (var s in series)
        {
            if (!marks.TryGetValue(s.Key, out var cancelledOn)) result.Add(s);
            else if (cancelledOn is { } on && on > today) result.Add(s with { EndsOn = on });
        }
        return result;
    }

    /// <summary>Marks a series "not a subscription" for the current user, or takes that back (a cancellation stays).</summary>
    public async Task SetDismissedAsync(string seriesKey, bool dismissed, CancellationToken ct = default)
    {
        var userId = await RequireUserAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.RecurringDismissals.FirstOrDefaultAsync(d => d.SeriesKey == seriesKey, ct);
        if (dismissed)
        {
            if (row == null) db.RecurringDismissals.Add(new RecurringDismissal { OwnerUserId = userId, SeriesKey = seriesKey });
            else row.CancelledOn = null;
        }
        else if (row is { CancelledOn: null }) db.RecurringDismissals.Remove(row);
        await SaveAndPublishAsync(db, userId, ct);
    }

    /// <summary>Records that the user cancelled a series on <paramref name="cancelledOn"/>, or (null) takes that back.</summary>
    public async Task SetCancelledAsync(string seriesKey, DateOnly? cancelledOn, CancellationToken ct = default)
    {
        var userId = await RequireUserAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.RecurringDismissals.FirstOrDefaultAsync(d => d.SeriesKey == seriesKey, ct);
        if (cancelledOn is { } on)
        {
            if (row == null) db.RecurringDismissals.Add(new RecurringDismissal { OwnerUserId = userId, SeriesKey = seriesKey, CancelledOn = on });
            else row.CancelledOn = on;
        }
        else if (row is { CancelledOn: not null }) db.RecurringDismissals.Remove(row);
        await SaveAndPublishAsync(db, userId, ct);
    }

    private async Task<string> RequireUserAsync() =>
        (_access == null ? null : (await _access.GetAsync()).UserId) ?? throw new AccessDeniedException("Sign in to mark a series.");

    private async Task SaveAndPublishAsync(BudgetDbContext db, string userId, CancellationToken ct)
    {
        if (!db.ChangeTracker.HasChanges()) return;
        await db.SaveChangesAsync(ct);
        // Scheduled and Cashflow of the user's other sessions follow.
        _notifier?.Publish(new DataChange(DataArea.ImportedData, UserId: userId), _origin?.Id);
    }

    /// <summary>
    /// Re-derives the key of every stored mark from its series' first booking with the current
    /// <see cref="RecurringPaymentDetector.SeriesKey(long, string?, string?, long)"/>, so marks follow when the payee
    /// normalisation changes. A mark whose booking is gone stays as it is; of two marks that end on one key, the
    /// cancellation wins over a dismissal. Expects an unscoped context.
    /// </summary>
    public static async Task RekeyMarksAsync(BudgetDbContext db, CancellationToken ct)
    {
        var rows = await db.RecurringDismissals.IgnoreQueryFilters().ToListAsync(ct);
        var parsed = rows.Select(r => (Row: r, Colon: r.SeriesKey.IndexOf(':', StringComparison.Ordinal), Last: r.SeriesKey.LastIndexOf(':', StringComparison.Ordinal)))
            .Where(x => x.Colon > 0 && x.Last > x.Colon && long.TryParse(x.Row.SeriesKey.AsSpan(x.Last + 1), System.Globalization.CultureInfo.InvariantCulture, out _))
            .Select(x => (x.Row, Account: x.Row.SeriesKey[..x.Colon], BookingId: long.Parse(x.Row.SeriesKey.AsSpan(x.Last + 1), System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();
        var ids = parsed.Select(x => x.BookingId).Distinct().ToList();
        var bookings = await db.Transactions.IgnoreQueryFilters().AsNoTracking().Where(t => ids.Contains(t.Id))
            .Select(t => new { t.Id, t.Name, t.Purpose }).ToDictionaryAsync(t => t.Id, ct);
        var kept = rows.ToDictionary(r => (r.OwnerUserId, r.SeriesKey));
        foreach (var (row, account, bookingId) in parsed)
        {
            if (!bookings.TryGetValue(bookingId, out var b)) continue;
            var key = RecurringPaymentDetector.SeriesKey(account, b.Name, b.Purpose, bookingId);
            if (key == row.SeriesKey) continue;
            db.RecurringDismissals.Remove(row);
            kept.Remove((row.OwnerUserId, row.SeriesKey));
            if (kept.TryGetValue((row.OwnerUserId, key), out var existing))
            {
                existing.CancelledOn ??= row.CancelledOn;
                continue;
            }
            var moved = new RecurringDismissal { OwnerUserId = row.OwnerUserId, SeriesKey = key, CreatedAt = row.CreatedAt, CancelledOn = row.CancelledOn };
            db.RecurringDismissals.Add(moved);
            kept[(row.OwnerUserId, key)] = moved;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The user's marked series: key → the cancellation day, null for a dismissal.</summary>
    private static async Task<Dictionary<string, DateOnly?>> MarksAsync(BudgetDbContext db, CancellationToken ct) =>
        (await db.RecurringDismissals.AsNoTracking().Select(d => new { d.SeriesKey, d.CancelledOn }).ToListAsync(ct))
            .GroupBy(d => d.SeriesKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().CancelledOn, StringComparer.Ordinal);

    private async Task<List<RecurringSeries>> DetectAsync(int projectId, DateOnly today, CancellationToken ct)
    {
        var userId = _access == null ? string.Empty : (await _access.GetAsync()).UserId ?? string.Empty;
        var key = (userId, projectId, today);
        if (_cache != null && _cache.TryGet(key, out var cached)) return cached;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var assignments = await db.ProjectAccountAssignments.AsNoTracking().Where(a => a.ProjectId == projectId)
            .Select(a => new { a.AccountId, a.Scope }).ToListAsync(ct);
        var accountIds = assignments.Select(a => a.AccountId).ToList();
        var result = new List<RecurringSeries>();
        if (accountIds.Count > 0)
        {
            var since = new DateTimeOffset(today.AddYears(-HistoryYears).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
            var rows = await db.Transactions.AsNoTracking()
                .Where(t => t.AccountId != null && accountIds.Contains(t.AccountId.Value) && t.Booked && t.Splitted != SplitState.Parent
                    && t.Amount < 0 && t.Timestamp >= since)
                .Select(t => new { t.Id, AccountId = t.AccountId!.Value, t.Timestamp, t.Amount, t.Name, t.Purpose, t.CategoryId, t.CounterpartyIban, t.TransferPeerId })
                .ToListAsync(ct);

            var shared = assignments.Where(a => a.Scope == AccountScope.Shared).Select(a => a.AccountId).ToHashSet();
            HashSet<long>? projectLeaves = null;
            if (shared.Count > 0)
            {
                var ownerId = await db.Projects.Where(p => p.Id == projectId).Select(p => p.OwnerUserId).FirstOrDefaultAsync(ct);
                var tracked = (await _projects.GetProjectTrackingItemsAsync(projectId)).ToTrackedKeys().ToList();
                var hierarchy = await _hierarchy.GetAllCategoriesWithChildCountsAsync(ownerId ?? string.Empty);
                projectLeaves = CategoryHierarchyService.BuildItemToLeafCategoryIds(tracked, hierarchy).Values.SelectMany(s => s).ToHashSet();
            }
            // Bookings in a transfer category (moves between accounts, card statements, the PayPal side of a payment its
            // bank side shows too), and uncategorized ones paired as transfers, are no subscription. A paired booking the
            // user gave another category (the bank side of a wallet payment) counts as that category.
            var transfers = await CategoryHierarchyService.TransferCategoryIdsAsync(db, null, ct);
            var bookings = rows
                .Where(t => t.CategoryId is { } tc ? !transfers.Contains(tc) : t.TransferPeerId == null)
                .Where(t => !shared.Contains(t.AccountId) || t.CategoryId is not { } c || projectLeaves?.Contains(c) == true)
                .Select(t => new RecurringBooking(t.Id, t.AccountId, DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(t.Timestamp).UtcDateTime),
                    t.Amount, t.Name, t.Purpose, t.CategoryId, t.CounterpartyIban))
                .ToList();
            var standing = await db.ScheduledPayments.AsNoTracking()
                .Where(p => accountIds.Contains(p.AccountId) && p.Kind == ScheduledPaymentKind.Standing)
                .Select(p => new StandingOrderKey(p.AccountId, p.Amount, p.CounterpartyName, p.CounterpartyIban)).ToListAsync(ct);
            // Money moved between the household's accounts is no subscription.
            var ownIbans = (await db.Accounts.AsNoTracking().Where(a => a.Iban != null).Select(a => a.Iban!).ToListAsync(ct))
                .Select(Iban.Normalize).ToHashSet(StringComparer.Ordinal);
            result = RecurringPaymentDetector.Detect(bookings, standing, ownIbans, today);
        }
        _cache?.Set(key, result);
        return result;
    }
}
