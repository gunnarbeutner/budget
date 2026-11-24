using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.OneDrive;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Connectors.Receipts;

/// <summary>The signed-in user's receipt settings and mailbox state.</summary>
public sealed record ReceiptSettingsView(
    bool MicrosoftAvailable, CloudConnectionView? Connection, DateTime? MailCheckedAt, DateTime? MailAttemptAt, string? MailLastError, int MailUnreadable = 0);

/// <summary>A stored receipt as the list shows it.</summary>
public sealed record ReceiptView(
    long Id, string Shop, string? OrderNumber, DateTime? OrderDate, DateTime? DeliveryDate, long PaymentCents, int Items,
    ReceiptMatchState State, long? TransactionId, DateTime? BookingDate, bool Split, string? Store = null, string? OtherOrderNumbers = null)
{
    /// <summary>The shop and where or what: "REWE B-…", "Amazon 304-…, 305-…" or "Lidl Teststraße 1, Musterstadt".</summary>
    public string Title => ReceiptService.Title(Shop, Store, OrderNumber, OtherOrderNumbers);

    /// <summary>The order's page at the shop, when the shop has one.</summary>
    public Uri? ShopUrl => ReceiptService.ShopUrl(Shop, OrderNumber);
}

/// <summary>A receipt line in a booking's details: name, quantity, line total and the category it went to.</summary>
public sealed record ReceiptLineView(long Id, string Name, string Quantity, long TotalCents, ReceiptItemKind Kind, long? CategoryId, string? Source);

/// <summary>The lines of one category: the category (null: the booking's own) and its share of the booking.</summary>
public sealed record ReceiptGroupView(long? CategoryId, long AmountCents, IReadOnlyList<ReceiptLineView> Lines);

/// <summary>
/// A receipt with its lines grouped by category (as the booking's split parts, when it has a booking): its shop, order,
/// the booking (null when it has none), whether the receipt split the booking, and its totals (positive cents: the
/// lines' sum, the bonus or coupons as zero or negative, and what was paid).
/// </summary>
public sealed record BookingReceiptView(long ReceiptId, long? TransactionId, string Shop, string? OrderNumber, DateTime? DeliveryDate, long BonusCents,
    IReadOnlyList<ReceiptGroupView> Groups, bool SplitByReceipt, string? Store = null, long TotalCents = 0, long PaymentCents = 0, string? InvoiceNumber = null,
    bool PaidInInstalments = false, long? BookingCategoryId = null, bool Instalment = false, string? OtherOrderNumbers = null,
    InstalmentProgress? Plan = null)
{
    /// <summary>The number of lines.</summary>
    public int ItemCount => Groups.Sum(g => g.Lines.Count);

    /// <summary>See <see cref="ReceiptView.Title"/>.</summary>
    public string Title => ReceiptService.Title(Shop, Store, OrderNumber, OtherOrderNumbers);

    /// <summary>The order's page at the shop, when the shop has one.</summary>
    public Uri? ShopUrl => ReceiptService.ShopUrl(Shop, OrderNumber);

    /// <summary>Each order's number and page at the shop (several when one payment covered several orders).</summary>
    public IReadOnlyList<(string OrderNumber, Uri Url)> ShopUrls() =>
        [.. ReceiptInvoice.OrderNumbersOf(OrderNumber, OtherOrderNumbers)
            .Select(o => (OrderNumber: o, Url: ReceiptService.ShopUrl(Shop, o)))
            .Where(x => x.Url != null).Select(x => (x.OrderNumber, x.Url!))];
}

/// <summary>Where a receipt stands, as the receipts page filters it.</summary>
public enum ReceiptReviewState
{
    /// <summary>Matched and its booking split by the receipt.</summary>
    Split,
    Matched,

    /// <summary>Not matched yet, but recent: its booking may still come.</summary>
    Waiting,
    Ambiguous,
    Unmatched,

    /// <summary>Paid in cash (or otherwise without a booking to look for).</summary>
    NoBooking,

    /// <summary>The full price of an order paid in instalments: the instalments have the bookings, and all of them are matched.</summary>
    Instalments,

    /// <summary>The full price of an order paid in instalments whose instalments are not all charged and matched yet.</summary>
    InstalmentsPending,

    /// <summary>
    /// An order paid in instalments that was returned or cancelled: the remaining instalments never come. Both its full
    /// price and the refunds of the full price without a booking (the plan's reversal, no money moved) need nothing more.
    /// </summary>
    InstalmentsCancelled,

    /// <summary>The owner said its booking is not in the app.</summary>
    Dismissed,
}

/// <summary>The status chips of the receipt list (see <see cref="ReceiptService.GroupOf"/>).</summary>
public enum ReceiptReviewGroup { Review, Waiting, Done, NoBooking }

/// <summary>A receipt of any source as the receipts list shows it; <see cref="IsOwn"/>: the signed-in user's own (theirs to assign).</summary>
public sealed record ReceiptReviewRow(
    long Id, string Source, string Shop, string Title, DateTime? Date, long PaymentCents, int Items, ReceiptReviewState State,
    long? TransactionId, DateTime? BookingDate, bool IsOwn = true, InstalmentProgress? Instalments = null, string? OwnerName = null);

/// <summary>
/// An order still waiting for its charge (see <see cref="ReceiptService.ExpectedChargesAsync"/>): the receipt, the account
/// it is expected on, the estimated day, the amount (negative cents), the receipt's title and its first item's name.
/// </summary>
public sealed record ExpectedReceiptCharge(long ReceiptId, long AccountId, DateOnly Date, long Cents, string Title, string? FirstItem);

/// <summary>A booking that may be a receipt's: id, date, signed amount, payee and purpose.</summary>
public sealed record ReceiptCandidate(long TransactionId, DateTime Date, long AmountCents, string? Name, string? Purpose);

/// <summary>An item memory entry: the normalised product name, its category and the project it belongs to.</summary>
public sealed record ItemMemoryView(long Id, string Name, long CategoryId, DateTime UpdatedAt, int ProjectId = 0, string? ProjectName = null);

/// <summary>A project's receipt item category: the row id, the category, its description and extra keywords.</summary>
public sealed record ProjectReceiptCategoryView(long Id, long CategoryId, string? Description, string? Keywords);

/// <summary>
/// A project's receipt item categories and, per receipt source the owner narrowed, the categories of the list that apply
/// to it (sources not in <see cref="Narrowed"/> use the whole list).
/// </summary>
public sealed record ProjectReceiptSettingsView(int ProjectId, string ProjectName, IReadOnlyList<ProjectReceiptCategoryView> Categories, IReadOnlyDictionary<string, IReadOnlySet<long>> Narrowed);

/// <summary>A project a receipt source's bookings land in.</summary>
public sealed record ReceiptProjectLink(int ProjectId, string Name);

/// <summary>A receipt source's receipts: how many, how many found their booking, and the latest still waiting for one.</summary>
public sealed record ReceiptSourceSummary(int Count, int Matched, int OpenCount, IReadOnlyList<ReceiptView> Open);

/// <summary>
/// The owner's side of receipts: settings (the non-food category), the list of read receipts, a booking's receipt
/// lines, and moving a line to another category (which the item memory keeps for later receipts and which re-splits the
/// booking). The receipt's owner sees and manages their receipts (owner-only query filters). A receipt on another
/// user's booking (a payment method mapped to a shared account) is also shown to whoever sees that booking, and
/// its lines may be moved by whoever may edit the booking; categories, item memory and project receipt categories are
/// then the booking owner's. Bookings are rewritten on a system context.
/// </summary>
public sealed class ReceiptService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly SystemDbContextFactory _system;
    private readonly AccessScopeProvider _access;
    private readonly OneDriveService _oneDrive;
    private readonly ReceiptStore _store;
    private readonly MailReceiptSync _mail;
    private readonly ChangeNotificationService _notifier;
    private readonly ChangeOrigin _origin;
    private readonly TimeProvider _clock;

    private readonly ConnectorRegistry _registry;

    public ReceiptService(
        IDbContextFactory<BudgetDbContext> dbFactory, SystemDbContextFactory system, AccessScopeProvider access, OneDriveService oneDrive,
        ReceiptStore store, MailReceiptSync mail, ChangeNotificationService notifier, ChangeOrigin origin, TimeProvider clock, ConnectorRegistry registry)
    {
        _registry = registry;
        _dbFactory = dbFactory;
        _system = system;
        _access = access;
        _oneDrive = oneDrive;
        _store = store;
        _mail = mail;
        _notifier = notifier;
        _origin = origin;
        _clock = clock;
    }

    /// <summary>A receipt's heading: the shop's name and its store or order number.</summary>
    public static Uri? ShopUrl(string shop, string? orderNumber) =>
        !string.IsNullOrEmpty(orderNumber) && ReceiptSourceProfiles.Default.For(shop)?.OrderUrl is { } url ? url(orderNumber) : null;

    public static string Title(string shop, string? store, string? orderNumber, string? otherOrderNumbers = null) =>
        ReceiptSourceProfiles.Default.For(shop)?.DisplayName(store, orderNumber, otherOrderNumbers) ?? (shop + " " + (store ?? orderNumber)).Trim();

    private async Task<string> UserAsync() => (await _access.GetAsync()).UserId ?? throw new AccessDeniedException("Not signed in.");

    public async Task<ReceiptSettingsView> GetSettingsAsync(CancellationToken ct = default)
    {
        var user = await UserAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var s = await db.ReceiptSettings.AsNoTracking().FirstOrDefaultAsync(x => x.OwnerUserId == user, ct);
        var connection = _oneDrive.IsEnabled ? await _oneDrive.GetConnectionAsync(user, ct) : null;
        return new ReceiptSettingsView(_oneDrive.IsEnabled, connection, s?.MailCheckedAt, s?.MailAttemptAt, s?.MailLastError, s?.MailUnreadable ?? 0);
    }

    /// <summary>The signed-in user's projects with their receipt item categories, in the projects' order.</summary>
    public async Task<List<ProjectReceiptSettingsView>> ListProjectSettingsAsync(CancellationToken ct = default)
    {
        var user = await UserAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var projects = await db.Projects.AsNoTracking().Where(p => p.OwnerUserId == user).OrderBy(p => p.SortOrder).ThenBy(p => p.Id)
            .Select(p => new { p.Id, p.Name }).ToListAsync(ct);
        var result = new List<ProjectReceiptSettingsView>();
        foreach (var p in projects) result.Add(await SettingsAsync(db, user, p.Id, p.Name, ct));
        return result;
    }

    /// <summary>One of the signed-in user's projects with its receipt item categories.</summary>
    public async Task<ProjectReceiptSettingsView> GetProjectSettingsAsync(int projectId, CancellationToken ct = default)
    {
        var user = await UserAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var name = await db.Projects.AsNoTracking().Where(p => p.Id == projectId && p.OwnerUserId == user).Select(p => p.Name).FirstOrDefaultAsync(ct)
            ?? throw new AccessDeniedException("Project not found.");
        return await SettingsAsync(db, user, projectId, name, ct);
    }

    private static async Task<ProjectReceiptSettingsView> SettingsAsync(BudgetDbContext db, string user, int projectId, string name, CancellationToken ct)
    {
        var categories = await db.ProjectReceiptCategories.AsNoTracking().Where(c => c.OwnerUserId == user && c.ProjectId == projectId).OrderBy(c => c.Id)
            .Select(c => new ProjectReceiptCategoryView(c.Id, c.CategoryId, c.Description, c.Keywords)).ToListAsync(ct);
        var narrowed = (await db.ProjectReceiptSources.AsNoTracking().Where(s => s.OwnerUserId == user && s.ProjectId == projectId).ToListAsync(ct))
            .ToDictionary(s => s.Source, s => (IReadOnlySet<long>)ReceiptProjects.ParseIds(s.CategoryIds), StringComparer.Ordinal);
        return new ProjectReceiptSettingsView(projectId, name, categories, narrowed);
    }

    /// <summary>Adds a category to a project's receipt item categories (nothing when it is there already).</summary>
    public Task AddProjectCategoryAsync(int projectId, long categoryId, string? description = null, string? keywords = null, CancellationToken ct = default) =>
        ChangeProjectAsync(projectId, categoryId, async (db, user) =>
        {
            if (await db.ProjectReceiptCategories.AnyAsync(c => c.ProjectId == projectId && c.CategoryId == categoryId, ct)) return;
            db.ProjectReceiptCategories.Add(new ProjectReceiptCategory
            {
                OwnerUserId = user,
                ProjectId = projectId,
                CategoryId = categoryId,
                Description = Clean(description, 200),
                Keywords = Clean(keywords, 500),
            });
        }, ct);

    /// <summary>Changes a project receipt category's description and extra keywords.</summary>
    public async Task UpdateProjectCategoryAsync(long id, string? description, string? keywords, CancellationToken ct = default)
    {
        var projectId = await ProjectOfRowAsync(id, ct);
        await ChangeProjectAsync(projectId, null, async (db, user) =>
        {
            var row = await db.ProjectReceiptCategories.FirstAsync(c => c.Id == id && c.OwnerUserId == user, ct);
            row.Description = Clean(description, 200);
            row.Keywords = Clean(keywords, 500);
        }, ct);
    }

    /// <summary>Removes a category from its project's receipt item categories (and from the project's source narrowings).</summary>
    public async Task RemoveProjectCategoryAsync(long id, CancellationToken ct = default)
    {
        var projectId = await ProjectOfRowAsync(id, ct);
        await ChangeProjectAsync(projectId, null, async (db, user) =>
        {
            var row = await db.ProjectReceiptCategories.FirstAsync(c => c.Id == id && c.OwnerUserId == user, ct);
            db.ProjectReceiptCategories.Remove(row);
            foreach (var n in await db.ProjectReceiptSources.Where(s => s.ProjectId == projectId && s.OwnerUserId == user).ToListAsync(ct))
            {
                var ids = ReceiptProjects.ParseIds(n.CategoryIds);
                if (ids.Remove(row.CategoryId)) n.CategoryIds = ReceiptProjects.FormatIds(ids);
            }
        }, ct);
    }

    /// <summary>
    /// Narrows a project's receipt item categories for one source to <paramref name="categoryIds"/> (of the project's
    /// list; empty: none apply); null lets the whole list apply again.
    /// </summary>
    public async Task SetSourceNarrowingAsync(int projectId, string source, IReadOnlyCollection<long>? categoryIds, CancellationToken ct = default)
    {
        if (!ReceiptSources.All.Contains(source, StringComparer.Ordinal)) throw new ArgumentException("Unknown receipt source.", nameof(source));
        await ChangeProjectAsync(projectId, null, async (db, user) =>
        {
            var row = await db.ProjectReceiptSources.FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Source == source && s.OwnerUserId == user, ct);
            if (categoryIds == null)
            {
                if (row != null) db.ProjectReceiptSources.Remove(row);
                return;
            }
            if (row == null)
            {
                row = new ProjectReceiptSource { OwnerUserId = user, ProjectId = projectId, Source = source };
                db.ProjectReceiptSources.Add(row);
            }
            var listed = await db.ProjectReceiptCategories.Where(c => c.ProjectId == projectId && c.OwnerUserId == user).Select(c => c.CategoryId).ToListAsync(ct);
            row.CategoryIds = ReceiptProjects.FormatIds(categoryIds.Where(listed.Contains));
        }, ct);
    }

    /// <summary>
    /// The signed-in user's projects a receipt source's bookings land in: the projects of the accounts of its matched
    /// bookings (for a source with payment methods also of the accounts they are mapped to), in the projects' order.
    /// </summary>
    public async Task<List<ReceiptProjectLink>> ProjectsOfSourceAsync(string source, CancellationToken ct = default)
    {
        var user = await UserAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var accounts = await SourceAccountsAsync(db, user, source, ct);
        var rows = await db.ProjectAccountAssignments.AsNoTracking()
            .Where(a => accounts.Contains(a.AccountId) && a.Project.OwnerUserId == user)
            .Select(a => new { a.ProjectId, a.Project.Name, a.Project.SortOrder }).ToListAsync(ct);
        return rows.DistinctBy(p => p.ProjectId).OrderBy(p => p.SortOrder).ThenBy(p => p.ProjectId)
            .Select(p => new ReceiptProjectLink(p.ProjectId, p.Name)).ToList();
    }

    /// <summary>
    /// The accounts a receipt source's bookings land on: the accounts of the user's matched bookings of the source and the
    /// accounts the source is mapped to (for a source with payment methods: theirs, on the logins of its connectors).
    /// </summary>
    private async Task<HashSet<long>> SourceAccountsAsync(BudgetDbContext db, string user, string source, CancellationToken ct)
    {
        var shops = ReceiptSourceProfiles.Default.ShopsOf(source);
        var bookings = db.ReceiptInvoices.Where(r => r.OwnerUserId == user && shops.Contains(r.Shop) && r.TransactionId != null).Select(r => r.TransactionId);
        var accounts = (await db.Transactions.AsNoTracking().Where(t => bookings.Contains(t.Id) && t.AccountId != null).Select(t => t.AccountId!.Value).Distinct().ToListAsync(ct)).ToHashSet();
        accounts.UnionWith(await db.ReceiptSourceAccounts.AsNoTracking().Where(m => m.OwnerUserId == user && m.Source == source).Select(m => m.AccountId).ToListAsync(ct));
        if (shops.Any(ReceiptSourceProfiles.Default.PerPaymentMethod))
        {
            var types = _registry.GetAvailable().Where(r => r.ReceiptSource == source).Select(r => r.ConnectorType).ToList();
            var logins = db.BankLogins.Where(l => l.OwnerUserId == user && types.Contains(l.ConnectorType)).Select(l => l.Id);
            accounts.UnionWith(await db.BankLoginAccounts.AsNoTracking().Where(a => logins.Contains(a.BankLoginId) && a.EnrichAccountId != null)
                .Select(a => a.EnrichAccountId!.Value).ToListAsync(ct));
        }
        return accounts;
    }

    // ---- the account a source's purchases are paid from ----

    /// <summary>Sources mapped as a whole (see <see cref="ReceiptSourceProfiles.MappedSources"/>).</summary>
    public static readonly IReadOnlyList<string> MappedSources = ReceiptSourceProfiles.Default.MappedSources;

    /// <summary>The accounts the signed-in user mapped a receipt source to (see <see cref="ReceiptSourceAccount"/>).</summary>
    public async Task<List<long>> GetSourceAccountsAsync(string source, CancellationToken ct = default)
    {
        var user = await UserAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.ReceiptSourceAccounts.AsNoTracking().Where(m => m.OwnerUserId == user && m.Source == source).OrderBy(m => m.Id).Select(m => m.AccountId).ToListAsync(ct);
    }

    /// <summary>
    /// Maps a receipt source to one more account whose bookings the user may edit (<paramref name="add"/>), or removes one,
    /// then matches the user's receipts again.
    /// </summary>
    public async Task SetSourceAccountAsync(string source, long accountId, bool add, CancellationToken ct = default)
    {
        if (!MappedSources.Contains(source, StringComparer.Ordinal)) throw new InvalidOperationException("Unknown receipt source.");
        var scope = await _access.GetAsync();
        var user = scope.UserId ?? throw new AccessDeniedException("Not signed in.");
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var row = await db.ReceiptSourceAccounts.FirstOrDefaultAsync(m => m.OwnerUserId == user && m.Source == source && m.AccountId == accountId, ct);
            if (add && row == null)
            {
                var account = await db.Accounts.AsNoTracking().Where(a => a.Id == accountId).Select(a => new { a.OwnerUserId }).FirstOrDefaultAsync(ct);
                if (account == null || !scope.CanEditTransactionsOf(account.OwnerUserId, accountId)) throw new AccessDeniedException("Account not found.");
                db.ReceiptSourceAccounts.Add(new ReceiptSourceAccount { OwnerUserId = user, Source = source, AccountId = accountId });
            }
            else if (!add && row != null) db.ReceiptSourceAccounts.Remove(row);
            else return;
            await db.SaveChangesAsync(ct);
        }
        await using (var sys = await _system.CreateDbContextAsync(ct))
            await _store.MatchAsync(sys, user, ct);
        _notifier.Publish(new DataChange(DataArea.BankSync), _origin.Id);
        _notifier.Publish(new DataChange(DataArea.ImportedData, OwnerUserId: user), _origin.Id);
    }

    /// <summary>
    /// The accounts each open receipt (no booking) is paid from, as its owner mapped it: its payment method's
    /// account, or the accounts of a mapped source. Receipts of an unmapped source are absent.
    /// </summary>
    private static async Task<Dictionary<long, HashSet<long>>> MappedAccountsAsync(BudgetDbContext sys, IReadOnlyList<InvoiceRow> receipts, CancellationToken ct)
    {
        var result = new Dictionary<long, HashSet<long>>();
        var owners = receipts.Select(r => r.OwnerUserId).Distinct(StringComparer.Ordinal).ToList();
        var sources = (await sys.ReceiptSourceAccounts.AsNoTracking().Where(m => owners.Contains(m.OwnerUserId)).ToListAsync(ct))
            .GroupBy(m => (m.OwnerUserId, m.Source)).ToDictionary(g => g.Key, g => g.Select(m => m.AccountId).ToHashSet());
        var logins = receipts.Where(r => r.BankLoginId != null).Select(r => r.BankLoginId!.Value).Distinct().ToList();
        var instruments = (await sys.BankLoginAccounts.AsNoTracking().Where(a => logins.Contains(a.BankLoginId) && a.EnrichAccountId != null)
                .Select(a => new { a.BankLoginId, a.RemoteId, Account = a.EnrichAccountId!.Value }).ToListAsync(ct))
            .GroupBy(a => (a.BankLoginId, a.RemoteId)).ToDictionary(g => g.Key, g => g.First().Account);
        foreach (var r in receipts)
        {
            if (ReceiptSourceProfiles.Default.PerPaymentMethod(r.Shop))
            {
                if (r.BankLoginId is { } l && instruments.TryGetValue((l, r.PaymentMethodId ?? ""), out var a)) result[r.Id] = [a];
            }
            else if (sources.TryGetValue((r.OwnerUserId, ReceiptSources.Of(r.Shop)), out var mapped)) result[r.Id] = mapped;
        }
        return result;
    }

    private async Task<int> ProjectOfRowAsync(long id, CancellationToken ct)
    {
        var user = await UserAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.ProjectReceiptCategories.AsNoTracking().Where(c => c.Id == id && c.OwnerUserId == user).Select(c => (int?)c.ProjectId).FirstOrDefaultAsync(ct)
            ?? throw new AccessDeniedException("Receipt category not found.");
    }

    private async Task ChangeProjectAsync(int projectId, long? categoryId, Func<BudgetDbContext, string, Task> change, CancellationToken ct)
    {
        var user = await UserAsync();
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            if (!await db.Projects.AnyAsync(p => p.Id == projectId && p.OwnerUserId == user, ct)) throw new AccessDeniedException("Project not found.");
            if (categoryId is { } c && !await db.Categories.AnyAsync(x => x.Id == c && x.OwnerUserId == user, ct))
                throw new AccessDeniedException("Category not found.");
            await change(db, user);
            await db.SaveChangesAsync(ct);
        }
        await using (var sys = await _system.CreateDbContextAsync(ct))
            await _store.MatchAsync(sys, user, ct);
        Publish(user);
    }

    private static string? Clean(string? text, int max) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim().Length <= max ? text.Trim() : text.Trim()[..max];

    /// <summary>Reads the signed-in user's mailbox now.</summary>
    public async Task<MailCheckResult> CheckMailNowAsync(CancellationToken ct = default) => await _mail.CheckNowAsync(await UserAsync(), ct);

    /// <summary>Whether the signed-in user's mailbox is being read right now.</summary>
    public bool IsReadingMail(string userId) => _mail.IsReading(userId);

    /// <summary>Stops reading the signed-in user's mailbox.</summary>
    public async Task StopMailAsync(CancellationToken ct = default) => await _oneDrive.StopMailAccessAsync(await UserAsync(), ct);

    /// <summary>The user's receipts, newest first.</summary>
    public async Task<List<ReceiptView>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        var user = await UserAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ReceiptInvoices.AsNoTracking().Where(r => r.OwnerUserId == user)
            .OrderByDescending(r => r.DeliveryDate ?? r.OrderDate).ThenByDescending(r => r.Id).Take(take)
            .Select(r => new { r.Id, r.Shop, r.Store, r.OrderNumber, r.OtherOrderNumbers, r.OrderDate, r.DeliveryDate, r.PaymentCents, Items = r.Items.Count, r.State, r.TransactionId, r.SplitSignature })
            .ToListAsync(ct);
        var txIds = rows.Where(r => r.TransactionId != null).Select(r => r.TransactionId!.Value).ToList();
        var dates = await db.Transactions.AsNoTracking().Where(t => txIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Timestamp, ct);
        return rows.Select(r => new ReceiptView(r.Id, r.Shop, r.OrderNumber, r.OrderDate, r.DeliveryDate, r.PaymentCents, r.Items, r.State, r.TransactionId,
            r.TransactionId is { } t && dates.TryGetValue(t, out var ts) ? DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime : null, r.SplitSignature != null, r.Store, r.OtherOrderNumbers)).ToList();
    }

    /// <summary>The user's receipts of one shop (a receipt source's login card): counts and the latest without a booking.</summary>
    public Task<ReceiptSourceSummary> GetShopSummaryAsync(string shop, int take = 10, CancellationToken ct = default) =>
        GetShopsSummaryAsync([shop], take, ct);

    /// <summary>Like <see cref="GetShopSummaryAsync"/> over several shops (the mailbox's shops).</summary>
    public async Task<ReceiptSourceSummary> GetShopsSummaryAsync(IReadOnlyList<string> shops, int take = 10, CancellationToken ct = default)
    {
        var user = await UserAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var list = shops.ToList();
        var q = db.ReceiptInvoices.AsNoTracking().Where(r => r.OwnerUserId == user && list.Contains(r.Shop));
        var count = await q.CountAsync(ct);
        var matched = await q.CountAsync(r => r.TransactionId != null, ct);
        // a receipt paid in cash has no booking to wait for
        var open = q.Where(r => r.TransactionId == null && r.PaymentCents > 0 && r.State != ReceiptMatchState.Dismissed && r.State != ReceiptMatchState.Skipped && r.State != ReceiptMatchState.Instalments);
        var openCount = await open.CountAsync(ct);
        var latest = await open.OrderByDescending(r => r.OrderDate).ThenByDescending(r => r.Id).Take(take)
            .Select(r => new ReceiptView(r.Id, r.Shop, r.OrderNumber, r.OrderDate, r.DeliveryDate, r.PaymentCents, r.Items.Count, r.State, null, null, false, r.Store, r.OtherOrderNumbers))
            .ToListAsync(ct);
        return new ReceiptSourceSummary(count, matched, openCount, latest);
    }

    /// <summary>The receipt of a booking (given by the booking or one of its parts) the user sees, or null; whoever owns the receipt.</summary>
    public async Task<BookingReceiptView?> GetForBookingAsync(long transactionId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.Transactions.AsNoTracking().Where(t => t.Id == transactionId).Select(t => new { t.Id, t.SplitParentId, t.Splitted }).FirstOrDefaultAsync(ct);
        if (row == null) return null;
        var bookingId = row.Splitted == SplitState.Child && row.SplitParentId is { } p ? p : row.Id;
        await using var sys = await _system.CreateDbContextAsync(ct);
        var invoice = await sys.ReceiptInvoices.AsNoTracking().Include(r => r.Items).OrderBy(r => r.Id).FirstOrDefaultAsync(r => r.TransactionId == bookingId, ct);
        return invoice == null ? null : await ViewAsync(sys, invoice, ct);
    }

    /// <summary>
    /// A receipt by its id, or null; for the receipt's owner, for whoever sees its booking, and (without a booking) for
    /// whoever sees an account it is mapped to.
    /// </summary>
    public async Task<BookingReceiptView?> GetReceiptAsync(long receiptId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var invoice = await db.ReceiptInvoices.AsNoTracking().Include(r => r.Items).FirstOrDefaultAsync(r => r.Id == receiptId, ct);
        if (invoice != null) return await ViewAsync(db, invoice, ct);
        await using var sys = await _system.CreateDbContextAsync(ct);
        invoice = await sys.ReceiptInvoices.AsNoTracking().Include(r => r.Items).FirstOrDefaultAsync(r => r.Id == receiptId, ct);
        if (invoice == null) return null;
        if (invoice.TransactionId is { } t) return await db.Transactions.AnyAsync(x => x.Id == t, ct) ? await ViewAsync(sys, invoice, ct) : null;
        // Without a booking: for whoever sees an account its payment method or source is mapped to.
        var row = await Project(sys.ReceiptInvoices.AsNoTracking().Where(r => r.Id == receiptId)).FirstAsync(ct);
        if (!(await MappedAccountsAsync(sys, [row], ct)).TryGetValue(receiptId, out var mapped)) return null;
        var ids = mapped.ToList();
        return await db.Accounts.AnyAsync(a => ids.Contains(a.Id), ct) ? await ViewAsync(sys, invoice, ct) : null;
    }

    private static async Task<BookingReceiptView> ViewAsync(BudgetDbContext db, ReceiptInvoice invoice, CancellationToken ct)
    {
        var booking = invoice.TransactionId is { } id ? await db.Transactions.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct) : null;
        var items = invoice.Items.OrderBy(i => i.Position).ToList();
        var lines = items.Select(i => new ReceiptLine(i.Id, i.Name, i.VatCode, i.TotalCents, i.NoDiscount, i.Kind)).ToList();
        var bookingCategory = booking?.CategoryId;
        var categories = items.ToDictionary(i => i.Id, i => new LineCategory(i.CategoryId, i.CategorySource));
        var cents = booking != null ? booking.SplitOriginalAmount ?? booking.Amount : -invoice.PaymentCents;
        var parts = ReceiptCategorizer.Parts(lines, categories, invoice.BonusCents, cents, bookingCategory);
        var groups = parts.Select(part => new ReceiptGroupView(part.CategoryId, part.AmountCents, part.LineIds
                .Select(i => items.First(x => x.Id == i))
                .Select(i => new ReceiptLineView(i.Id, i.Name, i.Quantity, i.TotalCents, i.Kind, i.CategoryId, i.CategorySource)).ToList()))
            .ToList();
        return new BookingReceiptView(invoice.Id, booking?.Id, invoice.Shop, invoice.OrderNumber, invoice.DeliveryDate ?? invoice.OrderDate, invoice.BonusCents, groups,
            invoice.SplitSignature != null, invoice.Store, invoice.TotalCents, invoice.PaymentCents, invoice.InvoiceNumber,
            invoice.State == ReceiptMatchState.Instalments, bookingCategory,
            OtherOrderNumbers: invoice.OtherOrderNumbers,
            // One instalment of an order paid in instalments: the rest of the price is in the other instalments.
            Instalment: invoice.State != ReceiptMatchState.Instalments && invoice.OrderNumber != null && await db.ReceiptInvoices.AnyAsync(r =>
                r.OrderNumber == invoice.OrderNumber && r.BankLoginId == invoice.BankLoginId && r.State == ReceiptMatchState.Instalments, ct),
            // The full price of an order paid in instalments: its instalments and those still due.
            Plan: invoice.State == ReceiptMatchState.Instalments && invoice.PaymentCents > 0
                ? (await InstalmentPlansAsync(db, [(invoice.Id, invoice.OrderNumber, invoice.BankLoginId, invoice.PaymentCents, invoice.State, invoice.TransactionId)], ct))
                    .Plans.GetValueOrDefault(invoice.Id)
                : null);
    }

    // ---- review ----

    /// <summary>How many days an unmatched receipt counts as waiting for its booking.</summary>
    public const int WaitingDays = 14;

    /// <summary>
    /// All of the user's receipts (mailbox, receipt sources, order charges), newest first, optionally only one source and one state.
    /// </summary>
    public async Task<List<ReceiptReviewRow>> ListAllAsync(string? source = null, ReceiptReviewState? state = null, int take = 300, CancellationToken ct = default)
    {
        var user = await UserAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var invoices = await Project(db.ReceiptInvoices.AsNoTracking().Where(r => r.OwnerUserId == user)
            .OrderByDescending(r => r.DeliveryDate ?? r.OrderDate).ThenByDescending(r => r.Id).Take(take * 2)).ToListAsync(ct);
        return await RowsAsync(db, user, invoices, source, state, take, ct);
    }

    /// <summary>
    /// The status chip a receipt falls under. "Done": nothing left to do — matched or split, the full price of an order paid
    /// in instalments whose plan is complete or cancelled (and the refunds reversing a cancelled plan), or nothing to pay (whatever its state). "No booking": it cost money but its booking is not in the app
    /// (dismissed, or paid without a booking to look for). "Review": ambiguous or unmatched.
    /// </summary>
    public static ReceiptReviewGroup GroupOf(ReceiptReviewState state, long paymentCents) => state switch
    {
        _ when paymentCents == 0 => ReceiptReviewGroup.Done,
        ReceiptReviewState.Split or ReceiptReviewState.Matched or ReceiptReviewState.Instalments or ReceiptReviewState.InstalmentsCancelled => ReceiptReviewGroup.Done,
        ReceiptReviewState.Waiting or ReceiptReviewState.InstalmentsPending => ReceiptReviewGroup.Waiting,
        ReceiptReviewState.Ambiguous or ReceiptReviewState.Unmatched => ReceiptReviewGroup.Review,
        _ => ReceiptReviewGroup.NoBooking,
    };

    /// <summary>Whether a receipt needs the user (the "Review" chip, a project's receipts badge).</summary>
    public static bool NeedsReview(ReceiptReviewRow row) => GroupOf(row.State, row.PaymentCents) == ReceiptReviewGroup.Review;

    /// <summary>
    /// A project's receipts, newest first: receipts linked to bookings on the project's accounts that the user may see (their
    /// own receipts, and other users' receipts on bookings visible to the user), plus receipts without a booking in any
    /// state (waiting, ambiguous, unmatched, dismissed, paid without a booking to look for, nothing to pay, the full
    /// price of an order paid in instalments): of any owner when their payment method or
    /// their source (see <see cref="ReceiptSourceAccount"/>) is mapped to one of the project's accounts, and the user's own
    /// of unmapped sources whose bookings land on the project's accounts (see <see cref="SourceAccountsAsync"/>). Other
    /// users' receipts are read-only (<see cref="ReceiptReviewRow.IsOwn"/>). Requires view access to the project.
    /// </summary>
    public async Task<List<ReceiptReviewRow>> ListProjectAsync(int projectId, string? source = null, int take = 5000, CancellationToken ct = default)
    {
        var scope = await _access.GetAsync();
        var user = scope.UserId ?? throw new AccessDeniedException("Not signed in.");
        if (!scope.CanView(projectId)) throw new AccessDeniedException("Project not found.");
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var accounts = (await db.ProjectAccountAssignments.AsNoTracking().Where(a => a.ProjectId == projectId).Select(a => a.AccountId).ToListAsync(ct)).ToHashSet();
        if (accounts.Count == 0) return [];

        await using var sys = await _system.CreateDbContextAsync(ct);
        var linked = await Project(sys.ReceiptInvoices.AsNoTracking().Where(r => r.TransactionId != null
            && sys.Transactions.Any(t => t.Id == r.TransactionId && t.AccountId != null && accounts.Contains(t.AccountId.Value)))).ToListAsync(ct);
        var foreignTx = linked.Where(r => r.OwnerUserId != user).Select(r => r.TransactionId!.Value).Distinct().ToList();
        var visibleTx = (await db.Transactions.AsNoTracking().Where(t => foreignTx.Contains(t.Id)).Select(t => t.Id).ToListAsync(ct)).ToHashSet();
        var invoices = linked.Where(r => r.OwnerUserId == user || visibleTx.Contains(r.TransactionId!.Value)).ToList();

        // Open receipts of any owner whose payment method or source is mapped to one of the project's accounts; the
        // user's own open receipts of unmapped sources where the source's bookings land.
        var openAll = await Project(sys.ReceiptInvoices.AsNoTracking().Where(r => r.TransactionId == null)).ToListAsync(ct);
        var mappedAccounts = await MappedAccountsAsync(sys, openAll, ct);
        var unmappedSources = new List<string>();
        foreach (var s in ReceiptService.MappedSources)
            if ((await SourceAccountsAsync(db, user, s, ct)).Overlaps(accounts)) unmappedSources.Add(s);
        invoices.AddRange(openAll.Where(r => mappedAccounts.TryGetValue(r.Id, out var mapped)
            ? mapped.Overlaps(accounts)
            : r.OwnerUserId == user && !ReceiptSourceProfiles.Default.PerPaymentMethod(r.Shop) && unmappedSources.Contains(ReceiptSources.Of(r.Shop))));
        return await RowsAsync(sys, user, invoices, source, null, take, ct);
    }

    /// <summary>
    /// The charges a project's waiting receipts (see <see cref="ListProjectAsync"/>) are expected to bring, soonest first.
    /// Each is expected on the project account its payment method or source is mapped to, or where the owner's bookings of
    /// the source land; of several, the one with the most of the shop's matched bookings. The day is the receipt's
    /// delivery (else order) day plus the median delay between those days and the bookings of the owner's matched
    /// receipts of the shop in the last year; an estimate already past is expected today.
    /// </summary>
    public async Task<List<ExpectedReceiptCharge>> ExpectedChargesAsync(int projectId, DateOnly today, CancellationToken ct = default)
    {
        var waiting = (await ListProjectAsync(projectId, ct: ct)).Where(r => r.State == ReceiptReviewState.Waiting && r.PaymentCents > 0).ToDictionary(r => r.Id);
        if (waiting.Count == 0) return [];
        var user = await UserAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var sys = await _system.CreateDbContextAsync(ct);
        var projectAccounts = (await db.ProjectAccountAssignments.AsNoTracking().Where(a => a.ProjectId == projectId).Select(a => a.AccountId).ToListAsync(ct)).ToHashSet();
        var ids = waiting.Keys.ToList();
        var invoices = await Project(sys.ReceiptInvoices.AsNoTracking().Where(r => ids.Contains(r.Id))).ToListAsync(ct);
        var mapped = await MappedAccountsAsync(sys, invoices, ct);

        var owners = invoices.Select(i => i.OwnerUserId).Distinct(StringComparer.Ordinal).ToList();
        var shops = invoices.Select(i => i.Shop).Distinct(StringComparer.Ordinal).ToList();
        var since = today.AddYears(-1).ToDateTime(TimeOnly.MinValue);
        var history = await sys.ReceiptInvoices.AsNoTracking()
            .Where(r => owners.Contains(r.OwnerUserId) && shops.Contains(r.Shop) && r.TransactionId != null && (r.DeliveryDate ?? r.OrderDate) >= since)
            .Join(sys.Transactions, r => r.TransactionId, t => t.Id, (r, t) => new { r.OwnerUserId, r.Shop, Day = r.DeliveryDate ?? r.OrderDate, t.Timestamp, t.AccountId })
            .ToListAsync(ct);
        var byShop = history.GroupBy(h => (h.OwnerUserId, h.Shop)).ToDictionary(g => g.Key, g => g.ToList());
        var firstItems = (await sys.ReceiptItems.AsNoTracking().Where(i => ids.Contains(i.ReceiptInvoiceId) && i.Kind == ReceiptItemKind.Product)
                .Select(i => new { i.ReceiptInvoiceId, i.Position, i.Name }).ToListAsync(ct))
            .GroupBy(i => i.ReceiptInvoiceId).ToDictionary(g => g.Key, g => g.OrderBy(i => i.Position).First().Name);

        var result = new List<ExpectedReceiptCharge>();
        var sourceAccounts = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        foreach (var inv in invoices)
        {
            if ((inv.DeliveryDate ?? inv.OrderDate) is not { } day) continue;
            HashSet<long> candidates;
            if (mapped.TryGetValue(inv.Id, out var m)) candidates = m;
            else if (inv.OwnerUserId == user)
            {
                var source = ReceiptSources.Of(inv.Shop);
                if (!sourceAccounts.TryGetValue(source, out var s)) sourceAccounts[source] = s = await SourceAccountsAsync(db, user, source, ct);
                candidates = s;
            }
            else continue;
            var past = byShop.GetValueOrDefault((inv.OwnerUserId, inv.Shop)) ?? [];
            var account = candidates.Where(projectAccounts.Contains)
                .OrderByDescending(a => past.Count(h => h.AccountId == a)).ThenBy(a => a).Select(a => (long?)a).FirstOrDefault();
            if (account == null) continue;
            var delays = past.Where(h => h.Day != null)
                .Select(h => Math.Max(0, DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(h.Timestamp).UtcDateTime).DayNumber - DateOnly.FromDateTime(h.Day!.Value).DayNumber))
                .Order().ToList();
            var delay = delays.Count == 0 ? 0 : delays[delays.Count / 2];
            var date = DateOnly.FromDateTime(day).AddDays(delay);
            var item = firstItems.GetValueOrDefault(inv.Id);
            if (item is { Length: > ExpectedInstalments.MaxItemTitle }) item = null;
            result.Add(new ExpectedReceiptCharge(inv.Id, account.Value, date < today ? today : date, -inv.PaymentCents, waiting[inv.Id].Title, item));
        }
        return result.OrderBy(r => r.Date).ThenBy(r => r.ReceiptId).ToList();
    }

    /// <summary>How many of the user's own receipts in a project (see <see cref="ListProjectAsync"/>) need review; 0 without access.</summary>
    public async Task<int> CountProjectReviewAsync(int projectId, CancellationToken ct = default)
    {
        var scope = await _access.GetAsync();
        if (scope.UserId == null || !scope.CanView(projectId)) return 0;
        return (await ListProjectAsync(projectId, ct: ct)).Count(r => r.IsOwn && NeedsReview(r));
    }

    private sealed record InvoiceRow(long Id, string OwnerUserId, string Shop, string? Store, string? OrderNumber, string? OtherOrderNumbers, DateTime? OrderDate,
        DateTime? DeliveryDate, long PaymentCents, int Items, ReceiptMatchState State, long? TransactionId, string? SplitSignature, int? BankLoginId, string? PaymentMethodId);

    private static IQueryable<InvoiceRow> Project(IQueryable<ReceiptInvoice> q) =>
        q.Select(r => new InvoiceRow(r.Id, r.OwnerUserId, r.Shop, r.Store, r.OrderNumber, r.OtherOrderNumbers, r.OrderDate, r.DeliveryDate, r.PaymentCents,
            r.Items.Count, r.State, r.TransactionId, r.SplitSignature, r.BankLoginId, r.PaymentMethodId));

    private DateOnly Today() => DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

    private static ReceiptReviewState StateOf(InvoiceRow r, DateOnly today)
    {
        var date = r.DeliveryDate ?? r.OrderDate;
        return r.State switch
        {
            ReceiptMatchState.Matched => r.SplitSignature != null ? ReceiptReviewState.Split : ReceiptReviewState.Matched,
            ReceiptMatchState.Ambiguous => ReceiptReviewState.Ambiguous,
            ReceiptMatchState.Dismissed => ReceiptReviewState.Dismissed,
            ReceiptMatchState.Skipped => ReceiptReviewState.NoBooking,
            ReceiptMatchState.Instalments => ReceiptReviewState.Instalments,
            _ when r.PaymentCents == 0 || (r.PaymentCents < 0 && ReceiptSourceProfiles.Default.For(r.Shop)?.SignedAmounts != true) => ReceiptReviewState.NoBooking,
            _ when date is { } d && DateOnly.FromDateTime(d).AddDays(WaitingDays) >= today => ReceiptReviewState.Waiting,
            _ => ReceiptReviewState.Unmatched,
        };
    }

    /// <summary>
    /// The plans of orders paid in instalments among the given receipts and their progress (see
    /// <see cref="InstalmentProgress.Of"/>), by the full-price entry's id, and the refunds without a booking that equal the
    /// full price of a cancelled plan (its reversal: no money moved). The order's instalment charges and refunds are its
    /// other receipts on the same login (a charge of several orders is in no plan, so the order number alone groups them).
    /// Full-price entries of the receipts' orders that are not among them count too, for the refunds' sake.
    /// </summary>
    internal static async Task<(Dictionary<long, InstalmentProgress> Plans, HashSet<long> Reversals)> InstalmentPlansAsync(
        BudgetDbContext db, IReadOnlyCollection<(long Id, string? OrderNumber, int? BankLoginId, long PaymentCents, ReceiptMatchState State, long? TransactionId)> receipts,
        CancellationToken ct)
    {
        var plans = new Dictionary<long, InstalmentProgress>();
        var reversals = new HashSet<long>();
        var relevant = receipts.Where(r => r.OrderNumber != null && (r.State == ReceiptMatchState.Instalments && r.PaymentCents > 0
            || r.PaymentCents < 0 && r.TransactionId == null)).ToList();
        if (relevant.Count == 0) return (plans, reversals);
        var orders = relevant.Select(p => p.OrderNumber!).Distinct(StringComparer.Ordinal).ToList();
        var logins = relevant.Select(p => p.BankLoginId).Distinct().ToList();
        var shops = ReceiptSourceProfiles.Default.InstalmentShops;
        var all = await db.ReceiptInvoices.AsNoTracking()
            .Where(r => shops.Contains(r.Shop) && r.OrderNumber != null && orders.Contains(r.OrderNumber) && logins.Contains(r.BankLoginId))
            .Select(r => new { r.Id, r.OrderNumber, r.BankLoginId, r.PaymentCents, r.State, r.TransactionId, Date = r.DeliveryDate ?? r.OrderDate })
            .ToListAsync(ct);
        foreach (var order in all.GroupBy(r => (r.OrderNumber, r.BankLoginId)))
        {
            var fulls = order.Where(r => r.State == ReceiptMatchState.Instalments && r.PaymentCents > 0).ToList();
            if (fulls.Count == 0) continue;
            var charges = order.Where(r => r.State != ReceiptMatchState.Instalments && r.PaymentCents > 0)
                .Select(r => new InstalmentCharge(r.Id, r.Date, r.PaymentCents, r.State == ReceiptMatchState.Matched, r.TransactionId)).ToList();
            var refunds = order.Where(r => r.PaymentCents < 0).ToList();
            foreach (var full in fulls)
            {
                var progress = InstalmentProgress.Of(full.PaymentCents, charges, refunds.Select(r => -r.PaymentCents));
                plans[full.Id] = progress;
                if (progress.Cancelled)
                    reversals.UnionWith(refunds.Where(r => -r.PaymentCents == full.PaymentCents && r.TransactionId == null
                        && r.State is ReceiptMatchState.Unmatched or ReceiptMatchState.Ambiguous).Select(r => r.Id));
            }
        }
        return (plans, reversals);
    }

    /// <summary>The review rows of the given invoices (one source, one state), newest first, with their bookings' dates.</summary>
    private async Task<List<ReceiptReviewRow>> RowsAsync(BudgetDbContext db, string user, IReadOnlyList<InvoiceRow> invoices, string? source, ReceiptReviewState? state, int take, CancellationToken ct)
    {
        var today = Today();
        var rows = invoices.Where(r => source == null || ReceiptSources.Of(r.Shop) == source)
            .Select(r => new ReceiptReviewRow(r.Id, ReceiptSources.Of(r.Shop), r.Shop, Title(r.Shop, r.Store, r.OrderNumber, r.OtherOrderNumbers),
                r.DeliveryDate ?? r.OrderDate, r.PaymentCents, r.Items, StateOf(r, today), r.TransactionId, null, r.OwnerUserId == user))
            .ToList();
        var (plans, reversals) = await InstalmentPlansAsync(db,
            invoices.Where(r => ReceiptSourceProfiles.Default.InstalmentShops.Contains(r.Shop, StringComparer.Ordinal)).Select(r => (r.Id, r.OrderNumber, r.BankLoginId, r.PaymentCents, r.State, r.TransactionId)).ToList(), ct);
        rows = rows.Select(r => plans.TryGetValue(r.Id, out var p)
                ? r with { State = p.Cancelled ? ReceiptReviewState.InstalmentsCancelled : p.Complete ? ReceiptReviewState.Instalments : ReceiptReviewState.InstalmentsPending, Instalments = p }
                : reversals.Contains(r.Id) ? r with { State = ReceiptReviewState.InstalmentsCancelled } : r).ToList();
        if (state is { } only) rows = rows.Where(r => r.State == only).ToList();
        rows = rows.OrderByDescending(r => r.Date).ThenByDescending(r => r.Id).Take(take).ToList();
        var others = invoices.Where(r => r.OwnerUserId != user).Select(r => r.OwnerUserId).Distinct(StringComparer.Ordinal).ToList();
        if (others.Count > 0)
        {
            var names = await db.Users.AsNoTracking().Where(u => others.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName ?? u.UserName ?? "?", ct);
            var owners = invoices.ToDictionary(r => r.Id, r => r.OwnerUserId);
            rows = rows.Select(r => r.IsOwn ? r : r with { OwnerName = names.GetValueOrDefault(owners[r.Id]) }).ToList();
        }
        var txIds = rows.Where(r => r.TransactionId != null).Select(r => r.TransactionId!.Value).Distinct().ToList();
        var dates = await db.Transactions.AsNoTracking().Where(t => txIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Timestamp, ct);
        return rows.Select(r => r.TransactionId is { } t && dates.TryGetValue(t, out var ts)
            ? r with { BookingDate = DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime } : r).ToList();
    }

    /// <summary>How much later than automatic matching a booking picked by hand may come (a bank booking unusually late).</summary>
    private const int HandPickedSlackDays = 10;

    /// <summary>
    /// Bookings that may be a receipt's: bookings near its date the user may edit (their own and those on accounts shared
    /// with them as editor) not linked to another receipt, closest amount first.
    /// </summary>
    public async Task<List<ReceiptCandidate>> CandidatesAsync(long receiptId, int take = 10, CancellationToken ct = default)
    {
        var user = await UserAsync();
        var editable = (await _access.GetAsync()).EditableSharedAccountIds.ToList();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var invoice = await db.ReceiptInvoices.AsNoTracking().FirstOrDefaultAsync(r => r.Id == receiptId && r.OwnerUserId == user, ct)
            ?? throw new AccessDeniedException("Receipt not found.");
        var day = invoice.DeliveryDate ?? invoice.OrderDate ?? invoice.ReadAt;
        var (min, max) = ReceiptSourceProfiles.Default.QueryRange(DateOnly.FromDateTime(day), DateOnly.FromDateTime(day), HandPickedSlackDays);
        var rows = await db.Transactions.AsNoTracking()
            .Where(t => (t.OwnerUserId == user || (t.AccountId != null && editable.Contains(t.AccountId.Value)))
                && t.Timestamp >= min && t.Timestamp < max && t.Splitted != SplitState.Child)
            .Select(t => new { t.Id, t.Timestamp, Cents = t.SplitOriginalAmount ?? t.Amount, t.Name, t.Purpose })
            .ToListAsync(ct);
        // Linked to a receipt of any owner (the user's own other receipts and those of the users they share with).
        var ids = rows.Select(r => r.Id).ToList();
        await using (var sys = await _system.CreateDbContextAsync(ct))
        {
            var taken = (await sys.ReceiptInvoices.AsNoTracking().Where(r => r.TransactionId != null && ids.Contains(r.TransactionId.Value) && r.Id != receiptId)
                .Select(r => r.TransactionId!.Value).ToListAsync(ct)).ToHashSet();
            rows = rows.Where(r => !taken.Contains(r.Id)).ToList();
        }
        var day0 = new DateTimeOffset(day, TimeSpan.Zero).ToUnixTimeSeconds();
        // a charge was paid by a debit, a refund came back as a credit
        return rows.Where(r => invoice.PaymentCents < 0 ? r.Cents > 0 : r.Cents < 0)
            .OrderBy(r => Math.Abs(-r.Cents - invoice.PaymentCents)).ThenBy(r => Math.Abs(r.Timestamp - day0)).Take(take)
            .Select(r => new ReceiptCandidate(r.Id, DateTimeOffset.FromUnixTimeSeconds(r.Timestamp).UtcDateTime, r.Cents, r.Name, r.Purpose)).ToList();
    }

    /// <summary>
    /// Assigns a receipt to a booking the user may edit by hand (kept whatever its amount; not one linked to another
    /// user's receipt), or with null marks it as having no booking in the app (it is no longer matched); the bookings are
    /// brought up to date.
    /// </summary>
    public async Task AssignAsync(long receiptId, long? transactionId, CancellationToken ct = default)
    {
        var user = await UserAsync();
        var scope = await _access.GetAsync();
        await using (var sys = await _system.CreateDbContextAsync(ct))
        {
            var invoice = await sys.ReceiptInvoices.FirstOrDefaultAsync(r => r.Id == receiptId && r.OwnerUserId == user, ct)
                ?? throw new AccessDeniedException("Receipt not found.");
            if (transactionId is { } t)
            {
                var booking = await sys.Transactions.AsNoTracking().Where(x => x.Id == t && x.Splitted != SplitState.Child)
                    .Select(x => new { x.OwnerUserId, x.AccountId }).FirstOrDefaultAsync(ct);
                if (booking == null || !scope.CanEditTransactionsOf(booking.OwnerUserId, booking.AccountId))
                    throw new AccessDeniedException("Booking not found.");
                if (await sys.ReceiptInvoices.AnyAsync(r => r.TransactionId == t && r.OwnerUserId != user, ct))
                    throw new InvalidOperationException("The booking has another user's receipt.");
                // the booking's former receipt lets go of it
                foreach (var other in await sys.ReceiptInvoices.Where(r => r.OwnerUserId == user && r.TransactionId == t && r.Id != receiptId).ToListAsync(ct))
                {
                    other.TransactionId = null;
                    other.ManualMatch = false;
                    other.SplitSignature = null;
                    other.State = ReceiptMatchState.Unmatched;
                }
                if (invoice.TransactionId != t) invoice.SplitSignature = null;
                invoice.TransactionId = t;
                invoice.ManualMatch = true;
                invoice.State = ReceiptMatchState.Matched;
            }
            else
            {
                invoice.TransactionId = null;
                invoice.ManualMatch = false;
                invoice.SplitSignature = null;
                invoice.State = ReceiptMatchState.Dismissed;
            }
            await sys.SaveChangesAsync(ct);
            await _store.MatchAsync(sys, user, ct);
        }
        Publish(user);
    }

    /// <summary>Lets a receipt the user assigned or dismissed be matched automatically again.</summary>
    public async Task ResetAssignmentAsync(long receiptId, CancellationToken ct = default)
    {
        var user = await UserAsync();
        await using (var sys = await _system.CreateDbContextAsync(ct))
        {
            var invoice = await sys.ReceiptInvoices.FirstOrDefaultAsync(r => r.Id == receiptId && r.OwnerUserId == user, ct)
                ?? throw new AccessDeniedException("Receipt not found.");
            invoice.ManualMatch = false;
            if (invoice.State == ReceiptMatchState.Dismissed) invoice.State = ReceiptMatchState.Unmatched;
            await sys.SaveChangesAsync(ct);
            await _store.MatchAsync(sys, user, ct);
        }
        Publish(user);
    }

    /// <summary>The user's item memory, by name.</summary>
    public async Task<List<ItemMemoryView>> ListMemoryAsync(CancellationToken ct = default)
    {
        var user = await UserAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var names = await db.Projects.AsNoTracking().Where(p => p.OwnerUserId == user).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        return (await db.ItemCategoryMemory.AsNoTracking().Where(m => m.OwnerUserId == user).OrderBy(m => m.NormalizedName).ThenBy(m => m.ProjectId)
                .Select(m => new ItemMemoryView(m.Id, m.NormalizedName, m.CategoryId, m.UpdatedAt, m.ProjectId, null)).ToListAsync(ct))
            .Select(m => m with { ProjectName = names.GetValueOrDefault(m.ProjectId) }).ToList();
    }

    /// <summary>Changes an item memory entry's category (null: deletes the entry); the bookings are brought up to date.</summary>
    public async Task SetMemoryAsync(long id, long? categoryId, CancellationToken ct = default)
    {
        var user = await UserAsync();
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var entry = await db.ItemCategoryMemory.FirstOrDefaultAsync(m => m.Id == id && m.OwnerUserId == user, ct)
                ?? throw new AccessDeniedException("Item memory entry not found.");
            if (categoryId is { } c)
            {
                if (!await db.Categories.AnyAsync(x => x.Id == c && x.OwnerUserId == user, ct)) throw new AccessDeniedException("Category not found.");
                entry.CategoryId = c;
                entry.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
            }
            else
            {
                db.ItemCategoryMemory.Remove(entry);
            }
            await db.SaveChangesAsync(ct);
        }
        await using (var sys = await _system.CreateDbContextAsync(ct))
            await _store.MatchAsync(sys, user, ct);
        Publish(user);
    }

    /// <summary>
    /// Moves a receipt line to <paramref name="categoryId"/>: the line keeps it, the item memory of the booking's project
    /// remembers it for the product (deposit lines and fees are not remembered), the category becomes one of the
    /// project's receipt item categories for the receipt's source (unless it is the booking's own), and the booking is
    /// split by the receipt again. Allowed to the receipt's owner while they may edit its booking (or it has none), and to
    /// whoever may edit its booking; the category, memory and project are the booking owner's.
    /// </summary>
    public Task MoveItemAsync(long itemId, long categoryId, CancellationToken ct = default) =>
        MoveAsync(item => item.Id == itemId, itemId, categoryId, ct);

    /// <summary>
    /// Moves every uncategorized product of a receipt to <paramref name="categoryId"/>, as <see cref="MoveItemAsync"/>
    /// would one by one (each product is remembered). Nothing happens when none is uncategorized.
    /// </summary>
    public async Task MoveUncategorizedAsync(long receiptId, long categoryId, CancellationToken ct = default)
    {
        long? first;
        await using (var sys = await _system.CreateDbContextAsync(ct))
            first = await sys.ReceiptItems.Where(i => i.ReceiptInvoiceId == receiptId && i.Kind == ReceiptItemKind.Product && i.CategoryId == null)
                .OrderBy(i => i.Position).Select(i => (long?)i.Id).FirstOrDefaultAsync(ct);
        if (first is { } id)
            await MoveAsync(item => item.Kind == ReceiptItemKind.Product && item.CategoryId == null, id, categoryId, ct);
    }

    /// <summary>Moves the lines of <paramref name="itemId"/>'s receipt that <paramref name="which"/> selects (see <see cref="MoveItemAsync"/>).</summary>
    private async Task MoveAsync(Func<ReceiptItem, bool> which, long itemId, long categoryId, CancellationToken ct)
    {
        var user = await UserAsync();
        var scope = await _access.GetAsync();
        var receiptOwner = user;
        await using (var sys = await _system.CreateDbContextAsync(ct))
        {
            var item = await sys.ReceiptItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, ct)
                ?? throw new AccessDeniedException("Receipt item not found.");
            var invoice = await sys.ReceiptInvoices.Include(r => r.Items).FirstAsync(r => r.Id == item.ReceiptInvoiceId, ct);
            receiptOwner = invoice.OwnerUserId;
            var booking = invoice.TransactionId is { } t
                ? await sys.Transactions.AsNoTracking().Where(x => x.Id == t).Select(x => new { x.OwnerUserId, x.AccountId, x.CategoryId }).FirstOrDefaultAsync(ct)
                : null;
            var allowed = booking != null
                ? scope.CanEditTransactionsOf(booking.OwnerUserId, booking.AccountId)
                : invoice.OwnerUserId == user;
            if (!allowed) throw new AccessDeniedException("Receipt item not found.");
            var categoryOwner = booking?.OwnerUserId ?? user;
            if (!await sys.Categories.AnyAsync(c => c.Id == categoryId && c.OwnerUserId == categoryOwner && c.Name != null && c.Name != "", ct))
                throw new AccessDeniedException("Category not found.");
            var lines = invoice.Items.Where(which).ToList();
            foreach (var line in lines)
            {
                line.CategoryId = categoryId;
                line.CategorySource = ReceiptCategorySource.Manual;
            }
            var project = booking == null ? null : await ReceiptProjects.ResolveAsync(sys, categoryOwner, booking.AccountId, booking.CategoryId, ct);
            if (project is { } p)
            {
                var names = lines.Where(l => l.Kind == ReceiptItemKind.Product).Select(l => ReceiptCategorizer.Normalize(l.Name))
                    .Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
                foreach (var name in names)
                {
                    var memory = await sys.ItemCategoryMemory.FirstOrDefaultAsync(m => m.OwnerUserId == categoryOwner && m.ProjectId == p && m.NormalizedName == name, ct);
                    if (memory == null)
                    {
                        memory = new ItemCategoryMemory { OwnerUserId = categoryOwner, ProjectId = p, NormalizedName = name };
                        sys.ItemCategoryMemory.Add(memory);
                    }
                    memory.CategoryId = categoryId;
                    memory.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
                }
                // A category the owner moves an item to becomes one of the project's categories for this source, so the memory applies.
                if (booking!.CategoryId != categoryId)
                {
                    if (!await sys.ProjectReceiptCategories.AnyAsync(c => c.ProjectId == p && c.CategoryId == categoryId, ct))
                        sys.ProjectReceiptCategories.Add(new ProjectReceiptCategory { OwnerUserId = categoryOwner, ProjectId = p, CategoryId = categoryId });
                    var source = ReceiptSources.Of(invoice.Shop);
                    if (await sys.ProjectReceiptSources.FirstOrDefaultAsync(s => s.ProjectId == p && s.Source == source, ct) is { } narrowing)
                        narrowing.CategoryIds = ReceiptProjects.FormatIds(ReceiptProjects.ParseIds(narrowing.CategoryIds).Append(categoryId));
                }
            }
            await sys.SaveChangesAsync(ct);
            await _store.ApplyAsync(sys, invoice, force: true, ct);
            if (booking != null && booking.OwnerUserId != user) Publish(booking.OwnerUserId);
        }
        Publish(user);
        if (receiptOwner != user) Publish(receiptOwner);
    }

    private void Publish(string user)
    {
        _notifier.Publish(new DataChange(DataArea.ImportedData, OwnerUserId: user), _origin.Id);
        _notifier.Publish(new DataChange(DataArea.CloudFiles, UserId: user), _origin.Id);
    }
}
