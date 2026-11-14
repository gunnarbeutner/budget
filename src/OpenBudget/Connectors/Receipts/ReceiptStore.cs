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

/// <summary>A receipt line to classify with the date of its receipt.</summary>
public sealed record DatedReceiptLine(ReceiptLine Line, DateOnly Date);

/// <summary>
/// Stores parsed receipts, matches them with the owner's bookings (see <see cref="ReceiptMatcher"/>) and splits a matched
/// booking by the receipt's lines (see <see cref="ReceiptCategorizer"/>): one part per category, each carrying its lines
/// in <see cref="Transaction.OrderItems"/>; the booking itself carries all lines. Matching runs after every bank sync and
/// mailbox read, so a receipt that arrived before its booking is placed once the booking is there.
///
/// A booking is split automatically only when it is booked, not split yet and its category was not chosen by hand, or
/// when it carries exactly the split this receipt wrote before (then the split follows the receipt, e.g. after the
/// owner moved a line). A split the owner changed, and a booking they categorized by hand, keep what they have; the
/// receipt only adds its lines. Running again changes nothing. Works on a system context and only ever pairs receipts
/// and bookings of the same owner.
/// </summary>
public sealed class ReceiptStore
{
    private readonly IReceiptItemClassifier? _ai;

    public ReceiptStore(IReceiptItemClassifier? ai = null) => _ai = ai;

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
    /// the project's receipt categories, the booking becoming booked or being categorized by hand); the bookings, rules
    /// and memory are read in a few set-based queries, the AI is asked once per set of choices for the open lines of all
    /// receipts, and only receipts whose outcome differs write anything.
    /// </summary>
    public async Task MatchAsync(BudgetDbContext db, string ownerUserId, CancellationToken ct)
    {
        var invoices = await db.ReceiptInvoices.Include(i => i.Items).Where(i => i.OwnerUserId == ownerUserId).ToListAsync(ct);
        if (invoices.Count == 0) return;

        // Amazon charges are looked for on the account their payment method is mapped to (none: nothing to look for).
        var accounts = await AmazonAccountsAsync(db, ownerUserId, invoices, ct);
        var dated = invoices.Where(i => (i.DeliveryDate ?? i.OrderDate) != null).ToList();
        var bookings = new List<ReceiptBooking>();
        if (dated.Count > 0)
        {
            var min = new DateTimeOffset(dated.Min(i => (i.DeliveryDate ?? i.OrderDate)!.Value).AddDays(-ReceiptMatcher.MaxDaysBefore - 1), TimeSpan.Zero).ToUnixTimeSeconds();
            var max = new DateTimeOffset(dated.Max(i => (i.DeliveryDate ?? i.OrderDate)!.Value).AddDays(ReceiptMatcher.MaxDaysAfter + 2), TimeSpan.Zero).ToUnixTimeSeconds();
            var rows = await db.Transactions.AsNoTracking()
                .Where(t => t.OwnerUserId == ownerUserId && t.Timestamp >= min && t.Timestamp <= max && t.Splitted != SplitState.Child)
                .Select(t => new { t.Id, t.AccountId, t.Timestamp, t.Amount, t.SplitOriginalAmount, t.Name, t.Purpose, t.Booked })
                .ToListAsync(ct);
            var shops = invoices.Select(i => i.Shop).Where(s => s != ReceiptShops.Amazon).Distinct(StringComparer.Ordinal).ToList();
            var amazonAccounts = accounts.Values.ToHashSet();
            bookings = rows.Where(r => shops.Any(s => ReceiptMatcher.IsShop(s, r.Name, r.Purpose)) || (r.AccountId is { } a && amazonAccounts.Contains(a)))
                .Select(r => new ReceiptBooking(r.Id, DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(r.Timestamp).UtcDateTime),
                    r.SplitOriginalAmount ?? r.Amount, r.Name, r.Purpose, r.AccountId, Pending: !r.Booked)).ToList();
        }

        // Earlier matches stay while their booking is still there with the same amount.
        var linked = invoices.Where(i => i.TransactionId != null).Select(i => i.TransactionId!.Value).Distinct().ToList();
        var current = (await db.Transactions.AsNoTracking()
                .Where(t => linked.Contains(t.Id) && t.OwnerUserId == ownerUserId && t.Splitted != SplitState.Child)
                .Select(t => new { t.Id, Cents = t.SplitOriginalAmount ?? t.Amount, t.AccountId }).ToListAsync(ct))
            .ToDictionary(t => t.Id);
        bool StillFits(long id, long paymentCents, long? account) =>
            current.TryGetValue(id, out var row) && row.Cents == -paymentCents && (account == null || row.AccountId == account);
        var kept = new HashSet<long>();
        var lost = new List<long>();
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
            var account = accounts.TryGetValue(i.Id, out var mapped) ? mapped : (long?)null;
            if (i.Shop == ReceiptShops.Amazon && account == null)
            {
                if (i.TransactionId is { } was) lost.Add(was);
                i.TransactionId = null;
                i.SplitSignature = null;
                i.State = ReceiptMatchState.Skipped;
                continue;
            }
            if (i.TransactionId is { } t && StillFits(t, i.PaymentCents, account) && kept.Add(t))
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
            .Select(i => new ReceiptToMatch(i.Id, i.PaymentCents, Day(i.OrderDate), Day(i.DeliveryDate), i.Shop, accounts.TryGetValue(i.Id, out var a) ? a : null)).ToList();
        var outcome = ReceiptMatcher.Match(open, bookings, kept);
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
            await db.Transactions.Where(t => released.Contains(t.Id) && t.OwnerUserId == ownerUserId).ExecuteUpdateAsync(u => u.SetProperty(t => t.OrderItems, (string?)null), ct);

        await ApplyAllAsync(db, ownerUserId, invoices.Where(i => i.TransactionId != null).ToList(), force: false, ct);
    }

    /// <summary>
    /// For each Amazon receipt of the owner: the owner's account its payment method is mapped to (absent for points, gift
    /// cards and payment methods not mapped).
    /// </summary>
    private static async Task<Dictionary<long, long>> AmazonAccountsAsync(BudgetDbContext db, string ownerUserId, IReadOnlyList<ReceiptInvoice> invoices, CancellationToken ct)
    {
        var result = new Dictionary<long, long>();
        var amazon = invoices.Where(i => i.Shop == ReceiptShops.Amazon && i.BankLoginId != null && !Amazon.AmazonPages.IsPointsLabel(i.PaymentMethod ?? "")).ToList();
        if (amazon.Count == 0) return result;
        var loginIds = amazon.Select(i => i.BankLoginId!.Value).Distinct().ToList();
        var mapping = (await db.BankLoginAccounts.AsNoTracking().Where(a => loginIds.Contains(a.BankLoginId) && a.EnrichAccountId != null).ToListAsync(ct))
            .ToDictionary(a => (a.BankLoginId, a.RemoteId), a => a.EnrichAccountId!.Value);
        var mapped = mapping.Values.Distinct().ToList();
        var owned = (await db.Accounts.AsNoTracking().Where(a => mapped.Contains(a.Id) && a.OwnerUserId == ownerUserId).Select(a => a.Id).ToListAsync(ct)).ToHashSet();
        foreach (var i in amazon)
            if (mapping.TryGetValue((i.BankLoginId!.Value, i.PaymentMethodId ?? ""), out var account) && owned.Contains(account)) result[i.Id] = account;
        return result;
    }

    /// <summary>
    /// Brings one matched receipt's booking up to date: the lines' categories, the split (see the class remarks; with
    /// <paramref name="force"/> also over a split or category the owner set by hand) and the item texts.
    /// True when the booking was split or re-split.
    /// </summary>
    public async Task<bool> ApplyAsync(BudgetDbContext db, ReceiptInvoice invoice, bool force, CancellationToken ct) =>
        (await ApplyAllAsync(db, invoice.OwnerUserId, [invoice], force, ct)).Contains(invoice.Id);

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
    /// the ids of the receipts whose booking was split or re-split.
    /// </summary>
    private async Task<HashSet<long>> ApplyAllAsync(BudgetDbContext db, string owner, IReadOnlyList<ReceiptInvoice> invoices, bool force, CancellationToken ct)
    {
        var changed = new HashSet<long>();
        var txIds = invoices.Where(i => i.TransactionId != null).Select(i => i.TransactionId!.Value).Distinct().ToList();
        if (txIds.Count == 0) return changed;
        var parents = await db.Transactions.Where(t => txIds.Contains(t.Id) && t.OwnerUserId == owner).ToDictionaryAsync(t => t.Id, ct);
        var splitIds = parents.Values.Where(p => p.Splitted == SplitState.Parent).Select(p => (long?)p.Id).ToList();
        var childrenOf = splitIds.Count == 0 ? [] : (await db.Transactions
                .Where(t => splitIds.Contains(t.SplitParentId) && t.Splitted == SplitState.Child && t.OwnerUserId == owner).ToListAsync(ct))
            .GroupBy(t => t.SplitParentId!.Value).ToDictionary(g => g.Key, g => g.ToList());

        var projects = new Dictionary<(long?, long?), int?>();
        var rulesBy = new Dictionary<(int?, string), ReceiptSourceRules>();
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
                : p.Ours || (parent.Booked && (parent.Splitted ?? SplitState.Normal) == SplitState.Normal && parent.CategorySetBy != TransactionCategorySource.Manual);
            if (!projects.TryGetValue((parent.AccountId, parent.CategoryId), out var project))
                projects[(parent.AccountId, parent.CategoryId)] = project = await ReceiptProjects.ResolveAsync(db, owner, parent.AccountId, parent.CategoryId, ct);
            var source = ReceiptSources.Of(invoice.Shop);
            if (!rulesBy.TryGetValue((project, source), out var rules))
                rulesBy[(project, source)] = rules = await ReceiptProjects.RulesAsync(db, owner, project, source, ct);
            p.Project = project;
            p.Rules = rules;
            work.Add(p);
        }

        // Each project's memory, for the names on its receipts.
        foreach (var group in work.GroupBy(p => p.Project))
        {
            var names = group.SelectMany(p => p.Items).Select(i => ReceiptCategorizer.Normalize(i.Name)).Distinct(StringComparer.Ordinal).ToList();
            var memory = await MemoryAsync(db, owner, group.Key, names, ct);
            foreach (var p in group) p.Memory = memory;
        }

        // Categories: the owner's moves stay, AI answers are asked once and kept, the rest follows memory and keywords.
        // An AI answer outside this source's categories (left from before they were configured, or a mapping that
        // changed) is dropped and asked again.
        Dictionary<long, string>? paths = null;
        var asks = new List<(Pending P, List<AllowedCategory> Choices, List<DatedReceiptLine> Lines)>();
        foreach (var p in work)
        {
            var parent = p.Parent;
            p.Manual = p.Items.Where(i => i.CategorySource == ReceiptCategorySource.Manual).ToDictionary(i => i.Id, i => i.CategoryId);
            foreach (var stale in p.Items.Where(i => i.CategorySource == ReceiptCategorySource.Ai
                && !(i.CategoryId is { } c && ReceiptCategorizer.IsAllowed(c, p.Rules, parent.CategoryId))))
            {
                stale.CategorySource = null;
                stale.CategoryId = null;
            }
            p.Ai = p.Items.Where(i => i.CategorySource == ReceiptCategorySource.Ai && i.CategoryId != null).ToDictionary(i => i.Id, i => i.CategoryId!.Value);
            if (!p.MayWrite || _ai == null) continue;
            var ask = ReceiptCategorizer.Undecided(p.Lines, p.Rules, p.Memory, parent.CategoryId)
                .Where(l => p.Items.First(i => i.Id == l.Id).CategorySource == null).ToList();
            if (ask.Count == 0) continue;
            var date = Day(p.Invoice.DeliveryDate ?? p.Invoice.OrderDate) ?? DateOnly.FromDateTime(parent.Date);
            var choices = p.Rules.Allowed.ToList();
            if (parent.CategoryId is { } h && choices.All(c => c.CategoryId != h))
            {
                paths ??= await PathsAsync(db, owner, ct);
                choices.Insert(0, new AllowedCategory(h, paths.GetValueOrDefault(h) ?? "", "Lebensmittel und alles andere"));
            }
            asks.Add((p, choices, ask.Select(l => new DatedReceiptLine(l, date)).ToList()));
        }
        // Receipts offering the AI the same choices are asked together.
        foreach (var group in asks.GroupBy(a => ChoicesKey(a.Choices), StringComparer.Ordinal))
        {
            var answers = await _ai!.ClassifyAsync(owner, group.SelectMany(a => a.Lines).ToList(), group.First().Choices, ct);
            foreach (var (p, _, lines) in group)
                foreach (var l in lines)
                    if (answers.TryGetValue(l.Line.Id, out var category) && ReceiptCategorizer.IsAllowed(category, p.Rules, p.Parent.CategoryId))
                        p.Ai[l.Line.Id] = category;
        }

        foreach (var p in work)
            if (await WriteAsync(db, p, ct)) changed.Add(p.Invoice.Id);
        await db.SaveChangesAsync(ct);
        return changed;
    }

    /// <summary>What sets the AI's choices apart: id, path and description of each, in order.</summary>
    private static string ChoicesKey(IEnumerable<AllowedCategory> choices) =>
        string.Join('\n', choices.Select(c => $"{c.CategoryId}\t{c.Path}\t{c.Description}"));

    /// <summary>
    /// Assigns one receipt's lines and writes its booking's item texts and split (the caller saves). True when the
    /// booking was split or re-split.
    /// </summary>
    private static async Task<bool> WriteAsync(BudgetDbContext db, Pending p, CancellationToken ct)
    {
        var (invoice, parent, items, lines) = (p.Invoice, p.Parent, p.Items, p.Lines);
        var home = parent.CategoryId;
        var assigned = ReceiptCategorizer.Assign(lines, parent.CategoryId, p.Rules, p.Memory, p.Ai, p.Manual);
        foreach (var item in items)
        {
            var a = assigned[item.Id];
            if (item.CategorySource == ReceiptCategorySource.Manual) continue;
            item.CategoryId = a.CategoryId == parent.CategoryId ? null : a.CategoryId;
            // A line is marked as decided (so the AI is not asked about it again) once the receipt could split the booking.
            if (p.MayWrite || a.Source != ReceiptCategorySource.Default || item.CategorySource != null) item.CategorySource = a.Source;
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
        if (!p.Ours) return false;
        // Everything is in one category again: the receipt's split goes.
        db.Transactions.RemoveRange(p.Children);
        parent.Splitted = SplitState.Normal;
        parent.SplitOriginalAmount = null;
        parent.SplitCheck = false;
        invoice.SplitSignature = null;
        return true;
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
