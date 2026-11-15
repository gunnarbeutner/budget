using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>A detected series as the Recurring view shows it, with its account and category and whether the user dismissed it.</summary>
public sealed record RecurringEntry(
    RecurringSeries Series,
    string AccountName,
    string AccountIcon,
    string? AccountColor,
    string? CategoryName,
    bool Dismissed);

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
/// user-scoped context, so only bookings the user may see count. Series the user dismissed are flagged
/// (<see cref="RecurringEntry.Dismissed"/>) or, for Scheduled and Cashflow, left out (<see cref="ActiveAsync"/>).
/// </summary>
public class RecurringPaymentService
{
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

    /// <summary>All series of the project, most expensive per month first, with the dismissed ones flagged.</summary>
    public async Task<List<RecurringEntry>> GetAsync(int projectId, DateOnly today, CancellationToken ct = default)
    {
        var series = await DetectAsync(projectId, today, ct);
        if (series.Count == 0) return [];
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var dismissed = await DismissedKeysAsync(db, ct);
        var accountIds = series.Select(s => s.AccountId).Distinct().ToList();
        var accounts = await db.Accounts.AsNoTracking().Where(a => accountIds.Contains(a.Id))
            .Select(a => new { a.Id, a.Name, a.Icon, a.Color, a.Type, a.Portfolio }).ToDictionaryAsync(a => a.Id, ct);
        var categoryIds = series.Where(s => s.CategoryId != null).Select(s => s.CategoryId!.Value).Distinct().ToList();
        var categories = await db.Categories.AsNoTracking().Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        return series.Where(s => accounts.ContainsKey(s.AccountId)).Select(s =>
        {
            var a = accounts[s.AccountId];
            return new RecurringEntry(s, a.Name ?? "?", AccountIcons.ForAccount(a.Icon, a.Type, a.Portfolio == 1), a.Color,
                s.CategoryId is { } c ? categories.GetValueOrDefault(c) : null, dismissed.Contains(s.Key));
        }).ToList();
    }

    /// <summary>The active series of the project the user has not dismissed, optionally only those on <paramref name="accountIds"/>.</summary>
    public async Task<List<RecurringSeries>> ActiveAsync(int projectId, DateOnly today, IReadOnlyCollection<long>? accountIds = null, CancellationToken ct = default)
    {
        var series = await DetectAsync(projectId, today, ct);
        series = series.Where(s => s.Status == RecurringStatus.Active && (accountIds == null || accountIds.Contains(s.AccountId))).ToList();
        if (series.Count == 0) return series;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var dismissed = await DismissedKeysAsync(db, ct);
        return series.Where(s => !dismissed.Contains(s.Key)).ToList();
    }

    /// <summary>Marks a series "not a subscription" for the current user, or takes that back.</summary>
    public async Task SetDismissedAsync(string seriesKey, bool dismissed, CancellationToken ct = default)
    {
        var userId = (_access == null ? null : (await _access.GetAsync()).UserId) ?? throw new AccessDeniedException("Sign in to dismiss a series.");
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.RecurringDismissals.FirstOrDefaultAsync(d => d.SeriesKey == seriesKey, ct);
        if (dismissed == (row != null)) return;
        if (row != null) db.RecurringDismissals.Remove(row);
        else db.RecurringDismissals.Add(new RecurringDismissal { OwnerUserId = userId, SeriesKey = seriesKey });
        await db.SaveChangesAsync(ct);
        // Scheduled and Cashflow of the user's other sessions follow.
        _notifier?.Publish(new DataChange(DataArea.ImportedData, UserId: userId), _origin?.Id);
    }

    private static async Task<HashSet<string>> DismissedKeysAsync(BudgetDbContext db, CancellationToken ct) =>
        (await db.RecurringDismissals.AsNoTracking().Select(d => d.SeriesKey).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

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
                .Select(t => new { t.Id, AccountId = t.AccountId!.Value, t.Timestamp, t.Amount, t.Name, t.Purpose, t.CategoryId, t.CounterpartyIban })
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
            var bookings = rows
                .Where(t => !shared.Contains(t.AccountId) || t.CategoryId is not { } c || projectLeaves?.Contains(c) == true)
                .Select(t => new RecurringBooking(t.Id, t.AccountId, DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(t.Timestamp).UtcDateTime),
                    t.Amount, t.Name, t.Purpose, t.CategoryId, t.CounterpartyIban))
                .ToList();
            var standing = await db.ScheduledPayments.AsNoTracking()
                .Where(p => accountIds.Contains(p.AccountId) && p.Kind == ScheduledPaymentKind.Standing)
                .Select(p => new StandingOrderKey(p.AccountId, p.Amount, p.CounterpartyName, p.CounterpartyIban)).ToListAsync(ct);
            // Money moved between the household's accounts is no subscription.
            var ownIbans = (await db.Accounts.AsNoTracking().Where(a => a.Iban != null).Select(a => a.Iban!).ToListAsync(ct))
                .Select(RecurringPaymentDetector.NormalizeIban).ToHashSet(StringComparer.Ordinal);
            result = RecurringPaymentDetector.Detect(bookings, standing, ownIbans, today);
        }
        _cache?.Set(key, result);
        return result;
    }
}
