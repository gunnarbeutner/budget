using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Connectors.Receipts;

/// <summary>
/// Places receipt lines the memory and keywords left open (the optional AI), choosing only among
/// <paramref name="choices"/> (the source's allowed categories and its default); line id → category. The lines may come
/// from several receipts that share these choices, each with its receipt's date.
/// </summary>
public interface IReceiptItemClassifier
{
    Task<Dictionary<long, long>> ClassifyAsync(string ownerUserId, IReadOnlyList<DatedReceiptLine> lines, IReadOnlyList<AllowedCategory> choices, CancellationToken ct);
}

/// <summary>A receipt line to classify, with the receipt's date and the shop's name (<see cref="ReceiptShops.DisplayName"/>).</summary>
public sealed record DatedReceiptLine(ReceiptLine Line, DateOnly Date, string Shop);

/// <summary>
/// Stores parsed receipts, matches them with the owner's bookings (see <see cref="ReceiptMatcher"/>) and splits a matched
/// booking by the receipt's lines (see <see cref="ReceiptCategorizer"/>): one part per category, each carrying its lines
/// in <see cref="Transaction.OrderItems"/>; the booking itself carries all lines. Matching runs after every bank sync and
/// mailbox read, so a receipt that arrived before its booking is placed once the booking is there.
///
/// A booking, booked or pending, is split automatically only when it is not split yet and its category was not chosen by
/// hand, or when it carries exactly the split this receipt wrote before (then the split follows the receipt, e.g. after
/// the owner moved a line or the booking's amount changed). A sync that replaces a pending booking moves its receipt and
/// the receipt's split to the new booking (see <see cref="Sync.PendingCarryOver"/>). A split the owner changed, and a booking they categorized by hand, keep what they have; the
/// receipt only adds its lines. Running again changes nothing. Works on a system context.
///
/// A receipt pairs with bookings of its owner and, for a payment method mapped to another user's account, with
/// bookings on that account while the receipt's owner may edit them (see <see cref="AccessScope.CanEditTransactionsOf"/>);
/// once that access is gone, the match is dropped on the next run. What the receipt writes to a booking (parts, item
/// texts) belongs to the booking's owner and follows that owner's projects, receipt categories and item memory. A
/// booking linked to a receipt of one owner is taken for the receipts of every other owner.
/// </summary>
public sealed class ReceiptStore
{
    private readonly IReceiptItemClassifier? _ai;

    /// <summary>A store with the optional AI for item categories.</summary>
    public ReceiptStore(IReceiptItemClassifier? ai = null)
    {
        _ai = ai;
    }

    /// <summary>Stores a parsed receipt for the owner; false when it is known already (same shop and invoice number).</summary>
    public static async Task<bool> AddAsync(
        BudgetDbContext db, string ownerUserId, string shop, ParsedReceipt r, string? messageId, DateTime nowUtc, CancellationToken ct, string? store = null)
    {
        if (await db.ReceiptInvoices.AnyAsync(i => i.OwnerUserId == ownerUserId && i.Shop == shop && i.InvoiceNumber == r.InvoiceNumber, ct)) return false;
        static DateTime? Utc(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var invoice = new ReceiptInvoice
        {
            OwnerUserId = ownerUserId,
            Shop = shop,
            Store = Truncate(store, 200),
            InvoiceNumber = Truncate(r.InvoiceNumber, 40)!,
            OrderNumber = Truncate(r.OrderNumber, 40),
            OrderDate = Utc(r.OrderDate),
            DeliveryDate = Utc(r.DeliveryDate),
            InvoiceDate = Utc(r.InvoiceDate),
            TotalCents = r.TotalCents,
            BonusCents = r.BonusCents,
            PaymentCents = r.PaymentCents,
            MessageId = Truncate(messageId, 500),
            ReadAt = nowUtc,
        };
        var position = 0;
        foreach (var i in r.Items)
            invoice.Items.Add(new ReceiptItem
            {
                OwnerUserId = ownerUserId,
                Position = position++,
                Name = Truncate(i.Name, 300)!,
                Quantity = Truncate(i.Quantity, 20)!,
                VatCode = Truncate(i.VatCode, 5),
                UnitCents = i.UnitCents,
                TotalCents = i.TotalCents,
                NoDiscount = i.NoDiscount,
                Kind = i.Kind,
            });
        db.ReceiptInvoices.Add(invoice);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// (Re)matches every receipt of the owner and brings the matched bookings' splits and item texts up to date. Every
    /// matched receipt is applied again, since what it writes also follows things outside the receipt (the item memory,
    /// the project's receipt categories, the booking being categorized by hand); the bookings, rules
    /// and memory are read in a few set-based queries, the AI is asked once per set of choices for the open lines of all
    /// receipts, and only receipts whose outcome differs write anything. One run per owner at a time: syncs finishing
    /// together wait for each other, and the later ones find the work done. A run also waits for the runs of the owners
    /// whose accounts this owner's payment methods are mapped to, and of those who map theirs to this owner's accounts,
    /// so two owners never match one booking at the same time.
    /// </summary>
    /// <param name="progress">Told how far the item categories got (the AI is asked in batches).</param>
    public async Task MatchAsync(BudgetDbContext db, string ownerUserId, CancellationToken ct, Action<string>? progress = null)
    {
        // Taken in one order (by id) by every run, so runs waiting for each other cannot deadlock.
        var owners = (await LinkedOwnersAsync(db, ownerUserId, ct)).Append(ownerUserId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var held = new List<SemaphoreSlim>();
        try
        {
            foreach (var owner in owners)
            {
                var gate = _ownerGates.GetOrAdd(owner, _ => new SemaphoreSlim(1, 1));
                if (!await gate.WaitAsync(0, ct))
                {
                    progress?.Invoke("waiting for another sync to finish matching");
                    await gate.WaitAsync(ct);
                }
                held.Add(gate);
            }
            await MatchCoreAsync(db, ownerUserId, progress, ct);
        }
        finally
        {
            foreach (var gate in held) gate.Release();
        }
    }

    /// <summary>
    /// The other users whose accounts the owner's payment methods are mapped to, and those whose payment methods are mapped
    /// to the owner's accounts.
    /// </summary>
    private static async Task<List<string>> LinkedOwnersAsync(BudgetDbContext db, string ownerUserId, CancellationToken ct)
    {
        var pairs = await (from m in db.BankLoginAccounts.AsNoTracking()
                           join a in db.Accounts.AsNoTracking() on m.EnrichAccountId equals a.Id
                           where m.BankLogin.OwnerUserId != a.OwnerUserId && (m.BankLogin.OwnerUserId == ownerUserId || a.OwnerUserId == ownerUserId)
                           select new { Login = m.BankLogin.OwnerUserId, Account = a.OwnerUserId }).Distinct().ToListAsync(ct);
        return pairs.Select(p => p.Login == ownerUserId ? p.Account : p.Login).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The accounts of other users whose bookings the owner may edit through projects shared with them.</summary>
    public static async Task<HashSet<long>> SharedAccountsAsync(BudgetDbContext db, string ownerUserId, CancellationToken ct)
    {
        var scope = await AccessScopeProvider.ComputeAsync(db, ownerUserId, ct);
        if (scope.EditableSharedAccountIds.Length == 0) return [];
        var ids = scope.EditableSharedAccountIds.ToList();
        return (await db.Accounts.AsNoTracking().Where(a => ids.Contains(a.Id) && a.OwnerUserId != ownerUserId).Select(a => a.Id).ToListAsync(ct)).ToHashSet();
    }

    // Per store: the app has one (a singleton), so its runs share these gates.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _ownerGates = new(StringComparer.Ordinal);

    private async Task MatchCoreAsync(BudgetDbContext db, string ownerUserId, Action<string>? progress, CancellationToken ct)
    {
        var invoices = await db.ReceiptInvoices.Include(i => i.Items).Where(i => i.OwnerUserId == ownerUserId).ToListAsync(ct);
        if (invoices.Count == 0) return;

        // Receipts with a payment method are looked for on the account it is mapped to (none: nothing to look for).
        var shared = await SharedAccountsAsync(db, ownerUserId, ct);
        var sharedList = shared.ToList();
        var accounts = await PaymentMethodAccountsAsync(db, ownerUserId, shared, invoices, ct);
        // A source mapped as a whole is looked for only on the accounts it is mapped to.
        var sourceAccounts = await SourceAccountsAsync(db, ownerUserId, shared, ct);
        var sharedMapped = accounts.Values.Concat(sourceAccounts.Values.SelectMany(a => a)).Where(shared.Contains).Distinct().ToList();
        var dated = invoices.Where(i => (i.DeliveryDate ?? i.OrderDate) != null).ToList();
        var bookings = new List<ReceiptBooking>();
        if (dated.Count > 0)
        {
            var (min, max) = ReceiptSourceProfiles.Default.QueryRange(
                DateOnly.FromDateTime(dated.Min(i => (i.DeliveryDate ?? i.OrderDate)!.Value)), DateOnly.FromDateTime(dated.Max(i => (i.DeliveryDate ?? i.OrderDate)!.Value)));
            var rows = await db.Transactions.AsNoTracking()
                .Where(t => (t.OwnerUserId == ownerUserId || (t.AccountId != null && sharedMapped.Contains(t.AccountId.Value)))
                    && t.Timestamp >= min && t.Timestamp < max && t.Splitted != SplitState.Child)
                .Select(t => new { t.Id, t.AccountId, t.Timestamp, t.Amount, t.SplitOriginalAmount, t.Name, t.Purpose, t.Booked })
                .ToListAsync(ct);
            // Per shop with receipts: which bookings may fit any of them (on its mapped accounts, naming it when it must).
            var shops = invoices.GroupBy(i => i.Shop, StringComparer.Ordinal)
                .Select(g => (Profile: ReceiptSourceProfiles.Default.For(g.Key), Source: sourceAccounts.GetValueOrDefault(ReceiptSources.Of(g.Key)),
                    Bound: g.Where(i => accounts.ContainsKey(i.Id)).Select(i => accounts[i.Id]).ToHashSet()))
                .Where(s => s.Profile != null).ToList();
            bool Fits(long? account, string? name, string? purpose) => shops.Any(s =>
                (s.Source == null || (account is { } x && s.Source.Contains(x)))
                && (!s.Profile!.RequiresAccount || (account is { } y && s.Bound.Contains(y)))
                && (!s.Profile.RequirePayeeMatch || s.Profile.Names(name, purpose)));
            bookings = rows.Where(r => Fits(r.AccountId, r.Name, r.Purpose))
                .Select(r => new ReceiptBooking(r.Id, DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(r.Timestamp).UtcDateTime),
                    r.SplitOriginalAmount ?? r.Amount, r.Name, r.Purpose, r.AccountId, Pending: !r.Booked)).ToList();
        }

        // Earlier matches stay while their booking is still there with the same amount.
        var linked = invoices.Where(i => i.TransactionId != null).Select(i => i.TransactionId!.Value).Distinct().ToList();
        var current = (await db.Transactions.AsNoTracking()
                .Where(t => linked.Contains(t.Id) && (t.OwnerUserId == ownerUserId || (t.AccountId != null && sharedList.Contains(t.AccountId.Value)))
                    && t.Splitted != SplitState.Child)
                .Select(t => new { t.Id, Cents = t.SplitOriginalAmount ?? t.Amount, t.AccountId }).ToListAsync(ct))
            .ToDictionary(t => t.Id);
        bool StillFits(long id, long paymentCents, long? account) =>
            current.TryGetValue(id, out var row) && row.Cents == -paymentCents && (account == null || row.AccountId == account);
        var kept = new HashSet<long>();
        var lost = new List<long>();
        // The full price of an order paid in instalments has no booking: its instalments have theirs.
        var fullPrices = invoices.Where(i => i.BankLoginId != null && i.OrderNumber != null && ReceiptSourceProfiles.Default.For(i.Shop)?.HasInstalments == true)
            .GroupBy(i => (i.Shop, i.BankLoginId, i.OrderNumber))
            .SelectMany(g => ReceiptSourceProfiles.Default.For(g.Key.Shop)!.InstalmentFullPrices!(g.ToList())).ToHashSet();
        foreach (var i in invoices)
        {
            if (i.State == ReceiptMatchState.Dismissed) continue;
            if (i.ManualMatch && i.TransactionId is { } own
                && current.ContainsKey(own) && kept.Add(own))
            {
                i.State = ReceiptMatchState.Matched;
                continue;
            }
            i.ManualMatch = false;
            if (fullPrices.Contains(i))
            {
                if (i.TransactionId is { } was) lost.Add(was);
                i.TransactionId = null;
                i.SplitSignature = null;
                i.State = ReceiptMatchState.Instalments;
                continue;
            }
            if (i.State == ReceiptMatchState.Instalments) i.State = ReceiptMatchState.Unmatched;
            var account = accounts.TryGetValue(i.Id, out var mapped) ? mapped : (long?)null;
            if (ReceiptSourceProfiles.Default.For(i.Shop)?.RequiresAccount == true && account == null)
            {
                if (i.TransactionId is { } was) lost.Add(was);
                i.TransactionId = null;
                i.SplitSignature = null;
                i.State = ReceiptMatchState.Skipped;
                continue;
            }
            if (i.TransactionId is { } t && StillFits(t, i.PaymentCents, account)
                && (!sourceAccounts.TryGetValue(ReceiptSources.Of(i.Shop), out var onlyOn) || (current[t].AccountId is { } at && onlyOn.Contains(at))) && kept.Add(t))
            {
                i.State = ReceiptMatchState.Matched;
                continue;
            }
            if (i.TransactionId is { } gone) lost.Add(gone);
            i.TransactionId = null;
            i.SplitSignature = null;
            i.State = ReceiptMatchState.Unmatched;
        }
        var open = invoices.Where(i => i.State == ReceiptMatchState.Unmatched)
            .Select(i => new ReceiptToMatch(i.Id, i.PaymentCents, Day(i.OrderDate), Day(i.DeliveryDate), i.Shop, accounts.TryGetValue(i.Id, out var a) ? a : null, i.OrderNumber,
                sourceAccounts.GetValueOrDefault(ReceiptSources.Of(i.Shop)))).ToList();
        // Bookings linked to another owner's receipts are taken.
        var candidateIds = bookings.Select(b => b.Id).ToList();
        var takenByOthers = candidateIds.Count == 0 ? [] : await db.ReceiptInvoices.AsNoTracking()
            .Where(r => r.OwnerUserId != ownerUserId && r.TransactionId != null && candidateIds.Contains(r.TransactionId.Value))
            .Select(r => r.TransactionId!.Value).Distinct().ToListAsync(ct);
        var outcome = ReceiptMatcher.Match(open, bookings, kept.Concat(takenByOthers).ToHashSet());
        foreach (var i in invoices.Where(i => i.State == ReceiptMatchState.Unmatched))
        {
            if (outcome.Matches.TryGetValue(i.Id, out var t))
            {
                i.TransactionId = t;
                i.State = ReceiptMatchState.Matched;
            }
            else if (outcome.Ambiguous.Contains(i.Id))
            {
                i.State = ReceiptMatchState.Ambiguous;
            }
        }
        await db.SaveChangesAsync(ct);

        // A booking that lost its receipt loses the receipt's item text (its split, if any, stays as it is).
        var stillLinked = invoices.Where(i => i.TransactionId != null).Select(i => i.TransactionId!.Value).ToHashSet();
        var released = lost.Where(l => !kept.Contains(l) && !stillLinked.Contains(l)).Distinct().ToList();
        if (released.Count > 0)
            await db.Transactions.Where(t => released.Contains(t.Id) && (t.OwnerUserId == ownerUserId || (t.AccountId != null && sharedList.Contains(t.AccountId.Value))))
                .ExecuteUpdateAsync(u => u.SetProperty(t => t.OrderItems, (string?)null), ct);

        await ApplyAllAsync(db, ownerUserId, shared, invoices.Where(i => i.TransactionId != null).ToList(), force: false, ct, progress);
    }

    /// <summary>
    /// Per mapped receipt source of the owner (see <see cref="ReceiptSourceAccount"/>): its accounts that are the owner's
    /// or in <paramref name="shared"/>; sources without such an account are absent.
    /// </summary>
    private static async Task<Dictionary<string, HashSet<long>>> SourceAccountsAsync(BudgetDbContext db, string ownerUserId, HashSet<long> shared, CancellationToken ct)
    {
        var rows = await db.ReceiptSourceAccounts.AsNoTracking().Where(m => m.OwnerUserId == ownerUserId)
            .Select(m => new { m.Source, m.AccountId, Owned = db.Accounts.Any(a => a.Id == m.AccountId && a.OwnerUserId == ownerUserId) }).ToListAsync(ct);
        return rows.Where(r => r.Owned || shared.Contains(r.AccountId)).GroupBy(r => r.Source, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(r => r.AccountId).ToHashSet(), StringComparer.Ordinal);
    }

    /// <summary>
    /// For each receipt of the owner with a payment method (<see cref="ReceiptSourceProfile.PerPaymentMethod"/>): the
    /// account it is mapped to, one of the owner's or of <paramref name="shared"/> (absent for payment methods never booked
    /// on an account and those not mapped).
    /// </summary>
    private static async Task<Dictionary<long, long>> PaymentMethodAccountsAsync(
        BudgetDbContext db, string ownerUserId, HashSet<long> shared, IReadOnlyList<ReceiptInvoice> invoices, CancellationToken ct)
    {
        var result = new Dictionary<long, long>();
        var withMethod = invoices.Where(i => i.BankLoginId != null && ReceiptSourceProfiles.Default.For(i.Shop) is { PerPaymentMethod: true } p
            && !p.IsUnbookablePaymentMethod(i.PaymentMethod ?? "")).ToList();
        if (withMethod.Count == 0) return result;
        var loginIds = withMethod.Select(i => i.BankLoginId!.Value).Distinct().ToList();
        var mapping = (await db.BankLoginAccounts.AsNoTracking().Where(a => loginIds.Contains(a.BankLoginId) && a.EnrichAccountId != null).ToListAsync(ct))
            .ToDictionary(a => (a.BankLoginId, a.RemoteId), a => a.EnrichAccountId!.Value);
        var mapped = mapping.Values.Distinct().ToList();
        var owned = (await db.Accounts.AsNoTracking().Where(a => mapped.Contains(a.Id) && a.OwnerUserId == ownerUserId).Select(a => a.Id).ToListAsync(ct)).ToHashSet();
        foreach (var i in withMethod)
            if (mapping.TryGetValue((i.BankLoginId!.Value, i.PaymentMethodId ?? ""), out var account) && (owned.Contains(account) || shared.Contains(account)))
                result[i.Id] = account;
        return result;
    }

    /// <summary>
    /// Brings one matched receipt's booking up to date: the lines' categories, the split (see the class remarks; with
    /// <paramref name="force"/> also over a split or category the owner set by hand) and the item texts.
    /// True when the booking was split, re-split or took the receipt's one category.
    /// </summary>
    public async Task<bool> ApplyAsync(BudgetDbContext db, ReceiptInvoice invoice, bool force, CancellationToken ct) =>
        (await ApplyAllAsync(db, invoice.OwnerUserId, await SharedAccountsAsync(db, invoice.OwnerUserId, ct), [invoice], force, ct)).Contains(invoice.Id);

    /// <summary>One receipt being applied: its booking, the booking's parts and what was worked out for its lines.</summary>
    private sealed class Pending(ReceiptInvoice invoice, Transaction parent, List<Transaction> children)
    {
        public ReceiptInvoice Invoice { get; } = invoice;
        public Transaction Parent { get; } = parent;
        public List<Transaction> Children { get; } = children;
        public List<ReceiptItem> Items { get; } = invoice.Items.OrderBy(i => i.Position).ToList();
        public List<ReceiptLine> Lines { get; set; } = [];
        public bool Ours { get; set; }
        public bool MayWrite { get; set; }
        public int? Project { get; set; }
        public ReceiptSourceRules Rules { get; set; } = ReceiptSourceRules.None;
        public Dictionary<string, long> Memory { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<long, long?> Manual { get; set; } = [];
        public Dictionary<long, long> Ai { get; set; } = [];
    }

    /// <summary>
    /// Applies the owner's matched receipts (see <see cref="ApplyAsync"/>): bookings, projects, rules and memory are read
    /// once for all of them, and the AI gets the open lines of all receipts with the same choices in one request. Returns
    /// the ids of the receipts whose booking was split, re-split or categorized. Only bookings of the owner and on
    /// <paramref name="shared"/> are written; categories, projects, rules and memory are those of the booking's owner.
    /// </summary>
    private async Task<HashSet<long>> ApplyAllAsync(
        BudgetDbContext db, string owner, HashSet<long> shared, IReadOnlyList<ReceiptInvoice> invoices, bool force, CancellationToken ct, Action<string>? progress = null)
    {
        var changed = new HashSet<long>();
        var txIds = invoices.Where(i => i.TransactionId != null).Select(i => i.TransactionId!.Value).Distinct().ToList();
        if (txIds.Count == 0) return changed;
        var sharedList = shared.ToList();
        var parents = await db.Transactions
            .Where(t => txIds.Contains(t.Id) && (t.OwnerUserId == owner || (t.AccountId != null && sharedList.Contains(t.AccountId.Value))))
            .ToDictionaryAsync(t => t.Id, ct);
        var splitIds = parents.Values.Where(p => p.Splitted == SplitState.Parent).Select(p => (long?)p.Id).ToList();
        var childrenOf = splitIds.Count == 0 ? [] : (await db.Transactions
                .Where(t => splitIds.Contains(t.SplitParentId) && t.Splitted == SplitState.Child).ToListAsync(ct))
            .Where(t => parents.TryGetValue(t.SplitParentId!.Value, out var p) && p.OwnerUserId == t.OwnerUserId)
            .GroupBy(t => t.SplitParentId!.Value).ToDictionary(g => g.Key, g => g.ToList());

        var projects = new Dictionary<(long?, long?), int?>();
        var rulesBy = new Dictionary<(int?, string, string), ReceiptSourceRules>();
        var work = new List<Pending>();
        foreach (var invoice in invoices)
        {
            if (invoice.TransactionId is not { } txId || !parents.TryGetValue(txId, out var parent)) continue;
            var p = new Pending(invoice, parent, parent.Splitted == SplitState.Parent ? childrenOf.GetValueOrDefault(parent.Id) ?? [] : []);
            p.Lines = p.Items.Select(i => new ReceiptLine(i.Id, i.Name, i.VatCode, i.TotalCents, i.NoDiscount, i.Kind)).ToList();
            p.Ours = invoice.SplitSignature != null && parent.Splitted == SplitState.Parent
                && ReceiptCategorizer.Signature(p.Children.Select(c => (c.Amount, c.CategoryId))) == invoice.SplitSignature;
            if (!p.Ours) invoice.SplitSignature = null;
            p.MayWrite = force
                ? parent.Splitted != SplitState.Remainder
                : p.Ours || ((parent.Splitted ?? SplitState.Normal) == SplitState.Normal && parent.CategorySetBy != TransactionCategorySource.Manual);
            if (!projects.TryGetValue((parent.AccountId, parent.CategoryId), out var project))
                projects[(parent.AccountId, parent.CategoryId)] = project = await ReceiptProjects.ResolveAsync(db, parent.OwnerUserId, parent.AccountId, parent.CategoryId, ct);
            var source = ReceiptSources.Of(invoice.Shop);
            if (!rulesBy.TryGetValue((project, source, parent.OwnerUserId), out var rules))
                rulesBy[(project, source, parent.OwnerUserId)] = rules = await ReceiptProjects.RulesAsync(db, parent.OwnerUserId, project, source, ct);
            p.Project = project;
            p.Rules = rules;
            work.Add(p);
        }

        // Each project's memory, for the names on its receipts.
        foreach (var group in work.GroupBy(p => (p.Project, p.Parent.OwnerUserId)))
        {
            var names = group.SelectMany(p => p.Items).Select(i => ReceiptCategorizer.Normalize(i.Name)).Distinct(StringComparer.Ordinal).ToList();
            var memory = await MemoryAsync(db, group.Key.OwnerUserId, group.Key.Project, names, ct);
            foreach (var p in group) p.Memory = memory;
        }

        // Categories: the owner's moves stay, AI answers are asked once and kept, the rest follows memory and keywords.
        // An AI answer outside this source's categories (left from before they were configured, or a mapping that
        // changed) is dropped and asked again.
        var asks = new List<(Pending P, List<AllowedCategory> Choices, List<DatedReceiptLine> Lines)>();
        foreach (var p in work)
        {
            var parent = p.Parent;
            // The owner's moves stay, and so do products that took the booking's category before items stopped
            // inheriting it (see Kept).
            p.Manual = p.Items.Where(i => i.CategorySource == ReceiptCategorySource.Manual || Kept(i)).ToDictionary(i => i.Id, i => i.CategoryId);
            foreach (var stale in p.Items.Where(i => i.CategorySource == ReceiptCategorySource.Ai
                && i.CategoryId is { } c && !ReceiptCategorizer.IsAllowed(c, p.Rules, parent.CategoryId)))
            {
                stale.CategorySource = null;
                stale.CategoryId = null;
            }
            // An AI answer without a category: the AI was asked and placed it nowhere; it is not asked again.
            p.Ai = p.Items.Where(i => i.CategorySource == ReceiptCategorySource.Ai && i.CategoryId != null)
                .ToDictionary(i => i.Id, i => i.CategoryId!.Value);
            if (!p.MayWrite || _ai == null) continue;
            var ask = ReceiptCategorizer.Undecided(p.Lines, p.Rules, p.Memory, parent.CategoryId)
                .Where(l => p.Items.First(i => i.Id == l.Id).CategorySource == null).ToList();
            if (ask.Count == 0) continue;
            var date = Day(p.Invoice.DeliveryDate ?? p.Invoice.OrderDate) ?? DateOnly.FromDateTime(parent.Date);
            var choices = p.Rules.Allowed.ToList();
            asks.Add((p, choices, ask.Select(l => new DatedReceiptLine(l, date, ReceiptShops.DisplayName(p.Invoice.Shop))).ToList()));
        }
        // Receipts offering the AI the same choices are asked together, a batch at a time (for the progress).
        var total = asks.Sum(a => a.Lines.Count);
        var done = 0;
        foreach (var group in asks.GroupBy(a => ChoicesKey(a.Choices), StringComparer.Ordinal))
        {
            var lines = group.SelectMany(a => a.Lines.Select(l => (a.P, Line: l))).ToList();
            foreach (var batch in lines.Chunk(OpenBudget.Assist.CategorySuggestionService.BatchSize))
            {
                progress?.Invoke($"Categorising receipt items ({done} of {total})");
                var answers = await _ai!.ClassifyAsync(owner, batch.Select(b => b.Line).ToList(), group.First().Choices, ct);
                foreach (var (p, l) in batch)
                {
                    if (answers.TryGetValue(l.Line.Id, out var category) && ReceiptCategorizer.IsAllowed(category, p.Rules, p.Parent.CategoryId))
                        p.Ai[l.Line.Id] = category;
                    else
                        p.Items.First(i => i.Id == l.Line.Id).CategorySource = ReceiptCategorySource.Ai;
                }
                done += batch.Length;
            }
        }

        foreach (var p in work)
            if (await WriteAsync(db, p, ct)) changed.Add(p.Invoice.Id);
        await db.SaveChangesAsync(ct);
        return changed;
    }

    /// <summary>
    /// A product that took the booking's category when items still inherited it: it keeps that category like a move of
    /// the owner, so receipts already split stay as they are.
    /// </summary>
    private static bool Kept(ReceiptItem item) =>
        item.Kind == ReceiptItemKind.Product && item.CategorySource == ReceiptCategorySource.Default && item.CategoryId != null;

    /// <summary>What sets the AI's choices apart: id, path and description of each, in order.</summary>
    private static string ChoicesKey(IEnumerable<AllowedCategory> choices) =>
        string.Join('\n', choices.Select(c => $"{c.CategoryId}\t{c.Path}\t{c.Description}"));

    /// <summary>
    /// Assigns one receipt's lines and writes its booking's item texts and split (the caller saves). True when the
    /// booking was split, re-split or took the receipt's one category.
    /// </summary>
    private static async Task<bool> WriteAsync(BudgetDbContext db, Pending p, CancellationToken ct)
    {
        var (invoice, parent, items, lines) = (p.Invoice, p.Parent, p.Items, p.Lines);
        var home = parent.CategoryId;
        var assigned = ReceiptCategorizer.Assign(lines, parent.CategoryId, p.Rules, p.Memory, p.Ai, p.Manual);
        foreach (var item in items)
        {
            var a = assigned[item.Id];
            if (item.CategorySource == ReceiptCategorySource.Manual || Kept(item)) continue;
            // Deposit lines and fees have no category of their own; a product keeps the AI's "nowhere" so it is not asked again.
            item.CategoryId = item.Kind == ReceiptItemKind.Product ? a.CategoryId : null;
            item.CategorySource = a.Source ?? (item.CategorySource == ReceiptCategorySource.Ai ? ReceiptCategorySource.Ai : null);
        }

        var all = ReceiptCategorizer.Describe(items.Select(i => (i.Name, i.Quantity)));
        parent.OrderItems = all.Length == 0 ? null : all;
        if (!p.MayWrite) return false;
        var parts = ReceiptCategorizer.Parts(lines, assigned, invoice.BonusCents, parent.SplitOriginalAmount ?? parent.Amount, home);
        if (parts.Count >= 2)
        {
            var signature = ReceiptCategorizer.Signature(parts.Select(x => (x.AmountCents, x.CategoryId)));
            var texts = parts.Select(x => ReceiptCategorizer.Describe(x.LineIds.Select(id => items.First(i => i.Id == id)).Select(i => (i.Name, i.Quantity)))).ToList();
            var sameTexts = p.Children.Count == parts.Count && p.Children.OrderBy(c => c.Id).Select(c => c.OrderItems).SequenceEqual(texts, StringComparer.Ordinal);
            if (signature == invoice.SplitSignature && p.Ours && sameTexts) return false;
            await SplitService.WritePartsAsync(db, parent, parts.Select((x, n) => new SplitPartWrite(x.AmountCents, x.CategoryId, null, texts[n])).ToList(), ct);
            invoice.SplitSignature = signature;
            return true;
        }
        var changed = false;
        if (p.Ours)
        {
            // Everything is in one category again: the receipt's split goes.
            db.Transactions.RemoveRange(p.Children);
            parent.Splitted = SplitState.Normal;
            parent.SplitOriginalAmount = null;
            parent.SplitCheck = false;
            invoice.SplitSignature = null;
            changed = true;
        }
        // All items in one category: an uncategorized booking takes it, and one that took it before follows the items.
        if (parts.Count == 1 && parts[0].CategoryId is { } only && only != parent.CategoryId
            && (parent.CategoryId == null || parent.CategorySetBy == TransactionCategorySource.Receipt))
        {
            parent.CategoryId = only;
            parent.CategorySetBy = TransactionCategorySource.Receipt;
            parent.RuleId = null;
            changed = true;
        }
        return changed;
    }

    /// <summary>The owner's categories by id with their paths ("Group › Category").</summary>
    public static async Task<Dictionary<long, string>> PathsAsync(BudgetDbContext db, string ownerUserId, CancellationToken ct)
    {
        var groups = await db.CategoryGroups.AsNoTracking().Where(g => g.OwnerUserId == ownerUserId).ToListAsync(ct);
        var categories = await db.Categories.AsNoTracking().Where(c => c.OwnerUserId == ownerUserId).ToListAsync(ct);
        var picker = CategoryPickerLogic.Build(groups, categories, await RowIds.SentinelRootAsync(db, ownerUserId, ct));
        var paths = picker.Options.ToDictionary(o => o.Id, o => o.Path);
        foreach (var c in categories.Where(c => !paths.ContainsKey(c.Id))) paths[c.Id] = c.Name ?? "";
        return paths;
    }

    /// <summary>A project's memory entries for these normalised names (none without a project).</summary>
    public static async Task<Dictionary<string, long>> MemoryAsync(BudgetDbContext db, string ownerUserId, int? projectId, IReadOnlyList<string> names, CancellationToken ct)
    {
        if (projectId is not { } project) return new Dictionary<string, long>(StringComparer.Ordinal);
        var rows = await db.ItemCategoryMemory.AsNoTracking().Where(m => m.OwnerUserId == ownerUserId && m.ProjectId == project && names.Contains(m.NormalizedName))
            .Select(m => new { m.NormalizedName, m.CategoryId }).ToListAsync(ct);
        var valid = (await db.Categories.AsNoTracking().Where(c => c.OwnerUserId == ownerUserId).Select(c => c.Id).ToListAsync(ct)).ToHashSet();
        return rows.Where(r => valid.Contains(r.CategoryId)).ToDictionary(r => r.NormalizedName, r => r.CategoryId, StringComparer.Ordinal);
    }

    private static DateOnly? Day(DateTime? d) => d is { } v ? DateOnly.FromDateTime(v) : null;

    private static string? Truncate(string? value, int max) => value == null || value.Length <= max ? value : value[..max];
}
