using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.Receipts;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Receipts;

/// <summary>Alice's categories, account and REWE direct debits for the receipt tests.</summary>
internal static class ReceiptWorld
{
    public const string Alice = "u-alice";
    public const string Bob = "u-bob";

    public static long Id(int row) => RowIds.Namespace(2, row);

    public static long Account => Id(1);
    public static long Groceries => Id(200);
    public static long Drugstore => Id(201);
    public static long Drinks => Id(202);

    /// <summary>Alice's household project: the joint account and the "Lebensmittel" group.</summary>
    public const int Project = 40;

    public static readonly DateTime Delivered = new(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Users, id spaces, Alice's categories and account (no bookings).</summary>
    public static async Task SeedAsync(TestDb t, bool users = true)
    {
        if (users)
        {
            t.AddUser(Alice, "alice");
            t.AddUser(Bob, "bob");
            t.AddIdSpace(Alice, 2);
            t.AddIdSpace(Bob, 3);
        }
        t.AddGroup(Id(100), name: "Lebensmittel", ownerId: Alice);
        t.AddLeaf(Groceries, Id(100), "Supermarkt", ownerId: Alice);
        t.AddLeaf(Drugstore, Id(100), "Drogerie", ownerId: Alice);
        t.AddLeaf(Drinks, Id(100), "Getränke", ownerId: Alice);
        t.AddAccount(Account, "Joint account", Alice);
        t.AddProject(Project, "Household", Alice);
        t.AssignGroup(Project, Id(100));
        t.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = Project, AccountId = Account });
        await t.Db.SaveChangesAsync();
    }

    /// <summary>A REWE direct debit of Alice, booked <paramref name="days"/> after delivery, categorized by a rule.</summary>
    public static async Task AddDebitAsync(TestDb t, long id, long cents, int days = 3, string setBy = TransactionCategorySource.Rule)
    {
        await using var db = await t.System.CreateDbContextAsync();
        db.Transactions.Add(new Transaction
        {
            Id = id,
            OwnerUserId = Alice,
            AccountId = Account,
            Amount = cents,
            Timestamp = new DateTimeOffset(Delivered.AddDays(days)).ToUnixTimeSeconds(),
            Name = "REWE Markt GmbH",
            Purpose = "48466003261102300881",
            CategoryId = Groceries,
            CategorySetBy = setBy,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>The standard synthetic invoice with a bonus of 1,00 € (payment 14,28 €).</summary>
    public static ParsedReceipt Invoice(string number = "PN10000000000001") =>
        ReweInvoice.Parse(SyntheticInvoice.Pdf(SyntheticInvoice.StandardLines(), bonus: -1.00m, invoice: number));

    /// <summary>Adds <paramref name="category"/> to the receipt item categories of Alice's household project.</summary>
    public static async Task AllowAsync(TestDb t, long? category, string? description = "Drogerie, Haushalt, Non-Food")
    {
        if (category is not { } c) return;
        await using var db = await t.System.CreateDbContextAsync();
        db.ProjectReceiptCategories.Add(new ProjectReceiptCategory { OwnerUserId = Alice, ProjectId = Project, CategoryId = c, Description = description });
        await db.SaveChangesAsync();
    }
}

/// <summary>Storing receipts, matching them with bookings and splitting those by category.</summary>
public sealed class ReceiptStoreTests
{
    private const long Payment = -1428;
    private static readonly long Debit = ReceiptWorld.Id(500);

    private static async Task<TestDb> WorldAsync(long? nonFood = null)
    {
        var t = new TestDb();
        await ReceiptWorld.SeedAsync(t);
        await ReceiptWorld.AllowAsync(t, nonFood ?? ReceiptWorld.Drugstore);
        return t;
    }

    private static async Task AddAndMatchAsync(TestDb t, ReceiptStore store, ParsedReceipt? receipt = null)
    {
        await using var db = await t.System.CreateDbContextAsync();
        await ReceiptStore.AddAsync(db, ReceiptWorld.Alice, ReceiptShops.Rewe, receipt ?? ReceiptWorld.Invoice(), "mail-1", DateTime.UtcNow, default);
        await store.MatchAsync(db, ReceiptWorld.Alice, default);
    }

    private static async Task<List<Transaction>> PartsAsync(TestDb t, long parent)
    {
        await using var db = await t.System.CreateDbContextAsync();
        return await db.Transactions.AsNoTracking().Where(x => x.SplitParentId == parent && x.Splitted == SplitState.Child).OrderBy(x => x.Id).ToListAsync();
    }

    private static async Task<Transaction> RowAsync(TestDb t, long id)
    {
        await using var db = await t.System.CreateDbContextAsync();
        return await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    [Fact]
    public async Task A_matched_booking_is_split_by_category_once()
    {
        await using var t = await WorldAsync();
        await ReceiptWorld.AddDebitAsync(t, Debit, Payment);
        var store = new ReceiptStore();
        await AddAndMatchAsync(t, store);

        var parts = await PartsAsync(t, Debit);
        Assert.Equal(2, parts.Count);
        Assert.Equal(Payment, parts.Sum(p => p.Amount));
        var drugstore = parts.Single(p => p.CategoryId == ReceiptWorld.Drugstore);
        Assert.Equal("Musterdeo Roll-On Fresh 50ml; Spülmittel Zitrone 450ml", drugstore.OrderItems);
        Assert.Contains("Leergut Glas", parts.Single(p => p.CategoryId == ReceiptWorld.Groceries).OrderItems, StringComparison.Ordinal);
        var parent = await RowAsync(t, Debit);
        Assert.Equal(SplitState.Parent, parent.Splitted);
        Assert.Contains("ja! Kochschinken 200g ×2", parent.OrderItems, StringComparison.Ordinal);

        // Matching again changes nothing.
        await using (var db = await t.System.CreateDbContextAsync())
            await store.MatchAsync(db, ReceiptWorld.Alice, default);
        Assert.Equal(parts.Select(p => p.Id), (await PartsAsync(t, Debit)).Select(p => p.Id));

        await using (var db = await t.System.CreateDbContextAsync())
        {
            var invoice = await db.ReceiptInvoices.Include(i => i.Items).SingleAsync();
            Assert.Equal((ReceiptMatchState.Matched, Debit), (invoice.State, invoice.TransactionId!.Value));
            Assert.NotNull(invoice.SplitSignature);
            Assert.Equal(ReceiptCategorySource.Keyword, invoice.Items.Single(i => i.Name.StartsWith("Musterdeo", StringComparison.Ordinal)).CategorySource);
        }
    }

    [Fact]
    public async Task The_same_invoice_is_stored_once()
    {
        await using var t = await WorldAsync();
        await using var db = await t.System.CreateDbContextAsync();
        Assert.True(await ReceiptStore.AddAsync(db, ReceiptWorld.Alice, ReceiptShops.Rewe, ReceiptWorld.Invoice(), "m1", DateTime.UtcNow, default));
        Assert.False(await ReceiptStore.AddAsync(db, ReceiptWorld.Alice, ReceiptShops.Rewe, ReceiptWorld.Invoice(), "m2", DateTime.UtcNow, default));
        Assert.Equal(1, await db.ReceiptInvoices.CountAsync());
    }

    [Fact]
    public async Task A_booking_categorized_by_hand_gets_the_items_but_no_split()
    {
        await using var t = await WorldAsync();
        await ReceiptWorld.AddDebitAsync(t, Debit, Payment, setBy: TransactionCategorySource.Manual);
        await AddAndMatchAsync(t, new ReceiptStore());

        Assert.Empty(await PartsAsync(t, Debit));
        var row = await RowAsync(t, Debit);
        Assert.Equal(SplitState.Normal, row.Splitted ?? SplitState.Normal);
        Assert.Contains("Musterdeo", row.OrderItems, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_non_food_items_nothing_is_split()
    {
        await using var t = new TestDb();
        await ReceiptWorld.SeedAsync(t);
        await ReceiptWorld.AddDebitAsync(t, Debit, Payment);
        await AddAndMatchAsync(t, new ReceiptStore());

        Assert.Empty(await PartsAsync(t, Debit));
        Assert.NotNull((await RowAsync(t, Debit)).OrderItems);
    }

    [Fact]
    public async Task A_receipt_waits_for_its_booking()
    {
        await using var t = await WorldAsync();
        var store = new ReceiptStore();
        await AddAndMatchAsync(t, store);
        await using (var db = await t.System.CreateDbContextAsync())
            Assert.Equal(ReceiptMatchState.Unmatched, (await db.ReceiptInvoices.SingleAsync()).State);

        await ReceiptWorld.AddDebitAsync(t, Debit, Payment, days: 6);
        await using (var db = await t.System.CreateDbContextAsync())
            await store.MatchAsync(db, ReceiptWorld.Alice, default);
        Assert.Equal(2, (await PartsAsync(t, Debit)).Count);
    }

    [Fact]
    public async Task Another_owners_booking_is_never_matched()
    {
        await using var t = await WorldAsync();
        await using (var db = await t.System.CreateDbContextAsync())
        {
            db.Transactions.Add(new Transaction
            {
                Id = RowIds.Namespace(3, 500),
                OwnerUserId = ReceiptWorld.Bob,
                Amount = Payment,
                Name = "REWE Markt GmbH",
                Timestamp = new DateTimeOffset(ReceiptWorld.Delivered.AddDays(2)).ToUnixTimeSeconds(),
            });
            await db.SaveChangesAsync();
        }
        await AddAndMatchAsync(t, new ReceiptStore());
        await using (var db = await t.System.CreateDbContextAsync())
            Assert.Null((await db.ReceiptInvoices.SingleAsync()).TransactionId);
    }

    [Fact]
    public async Task A_split_changed_by_hand_is_left_alone()
    {
        await using var t = await WorldAsync();
        await ReceiptWorld.AddDebitAsync(t, Debit, Payment);
        var store = new ReceiptStore();
        await AddAndMatchAsync(t, store);
        await using (var db = await t.System.CreateDbContextAsync())
        {
            var part = await db.Transactions.FirstAsync(x => x.SplitParentId == Debit && x.CategoryId == ReceiptWorld.Drugstore);
            part.CategoryId = ReceiptWorld.Drinks;
            await db.SaveChangesAsync();
            await store.MatchAsync(db, ReceiptWorld.Alice, default);
        }
        var parts = await PartsAsync(t, Debit);
        Assert.Contains(parts, p => p.CategoryId == ReceiptWorld.Drinks);
        Assert.DoesNotContain(parts, p => p.CategoryId == ReceiptWorld.Drugstore);
    }

    [Fact]
    public async Task Remembered_items_and_ai_answers_place_later_receipts()
    {
        await using var t = await WorldAsync();
        await ReceiptWorld.AddDebitAsync(t, Debit, Payment);
        await using (var db = await t.System.CreateDbContextAsync())
        {
            db.ItemCategoryMemory.Add(new ItemCategoryMemory { OwnerUserId = ReceiptWorld.Alice, ProjectId = ReceiptWorld.Project, NormalizedName = "apfelschorle", CategoryId = ReceiptWorld.Drinks });
            await db.SaveChangesAsync();
        }
        await ReceiptWorld.AllowAsync(t, ReceiptWorld.Drinks, null);
        var ai = new FakeClassifier(name => name.StartsWith("Beispiel Banane", StringComparison.Ordinal) ? ReceiptWorld.Drinks : null);
        var store = new ReceiptStore(ai);
        await AddAndMatchAsync(t, store);

        var drinks = (await PartsAsync(t, Debit)).Single(p => p.CategoryId == ReceiptWorld.Drinks);
        Assert.Equal("Beispiel Banane ca. 200g; Apfelschorle 0,5l ×2", drinks.OrderItems);
        // Only products the memory and keywords left open were asked about, and only once.
        Assert.Equal(["Beispiel Gouda gerieben 150g", "ja! Kochschinken 200g", "Beispiel Banane ca. 200g"], ai.Asked);
        await using (var db = await t.System.CreateDbContextAsync())
            await store.MatchAsync(db, ReceiptWorld.Alice, default);
        Assert.Equal(3, ai.Asked.Count);
    }

    [Fact]
    public async Task The_ai_is_asked_once_for_all_receipts_with_the_same_choices_and_not_again()
    {
        await using var t = await WorldAsync();
        var ai = new FakeClassifier(name => name.StartsWith("Beispiel Banane", StringComparison.Ordinal) ? ReceiptWorld.Drugstore : null);
        var store = new ReceiptStore(ai);
        await using (var db = await t.System.CreateDbContextAsync())
            for (var n = 1; n <= 3; n++)
            {
                await ReceiptWorld.AddDebitAsync(t, ReceiptWorld.Id(500 + n), Payment + 100 * (n - 1));
                var receipt = ReweInvoice.Parse(SyntheticInvoice.Pdf(SyntheticInvoice.StandardLines(), bonus: -n, invoice: $"PN1000000000000{n}"));
                await ReceiptStore.AddAsync(db, ReceiptWorld.Alice, ReceiptShops.Rewe, receipt, $"mail-{n}", DateTime.UtcNow, default);
            }
        var progress = new List<string>();
        await using (var db = await t.System.CreateDbContextAsync())
            await store.MatchAsync(db, ReceiptWorld.Alice, default, progress.Add);

        Assert.Equal(1, ai.Calls);
        Assert.Matches(@"^Categorising receipt items \(0 of [1-9][0-9]*\)$", Assert.Single(progress));
        Assert.Equal(3, ai.Asked.Count(n => n.StartsWith("Beispiel Banane", StringComparison.Ordinal)));
        for (var n = 1; n <= 3; n++)
            Assert.Contains("Beispiel Banane", (await PartsAsync(t, ReceiptWorld.Id(500 + n))).Single(p => p.CategoryId == ReceiptWorld.Drugstore).OrderItems, StringComparison.Ordinal);

        // Matching again leaves the settled receipts as they are, without asking the AI.
        var parts = new List<long>();
        for (var n = 1; n <= 3; n++) parts.AddRange((await PartsAsync(t, ReceiptWorld.Id(500 + n))).Select(p => p.Id));
        await using (var db = await t.System.CreateDbContextAsync())
            await store.MatchAsync(db, ReceiptWorld.Alice, default);
        Assert.Equal(1, ai.Calls);
        var again = new List<long>();
        for (var n = 1; n <= 3; n++) again.AddRange((await PartsAsync(t, ReceiptWorld.Id(500 + n))).Select(p => p.Id));
        Assert.Equal(parts, again);
    }

    [Fact]
    public async Task An_ai_answer_naming_the_bookings_own_category_is_not_asked_again()
    {
        await using var t = await WorldAsync();
        var ai = new FakeClassifier(_ => ReceiptWorld.Groceries);
        var store = new ReceiptStore(ai);
        await ReceiptWorld.AddDebitAsync(t, ReceiptWorld.Id(500), Payment);
        await using (var db = await t.System.CreateDbContextAsync())
            await ReceiptStore.AddAsync(db, ReceiptWorld.Alice, ReceiptShops.Rewe,
                ReweInvoice.Parse(SyntheticInvoice.Pdf(SyntheticInvoice.StandardLines(), bonus: -1, invoice: "PN10000000000001")), "mail-1", DateTime.UtcNow, default);
        for (var run = 0; run < 3; run++)
            await using (var db = await t.System.CreateDbContextAsync())
                await store.MatchAsync(db, ReceiptWorld.Alice, default);
        Assert.Equal(1, ai.Calls);
    }

    [Fact]
    public async Task A_second_match_of_the_same_owner_says_it_waits_for_the_first()
    {
        await using var t = await WorldAsync();
        var release = new TaskCompletionSource();
        var ai = new FakeClassifier(_ => null) { Hold = release.Task };
        var store = new ReceiptStore(ai);
        await ReceiptWorld.AddDebitAsync(t, ReceiptWorld.Id(500), Payment);
        await using (var db = await t.System.CreateDbContextAsync())
            await ReceiptStore.AddAsync(db, ReceiptWorld.Alice, ReceiptShops.Rewe,
                ReweInvoice.Parse(SyntheticInvoice.Pdf(SyntheticInvoice.StandardLines(), bonus: -1, invoice: "PN10000000000001")), "mail-1", DateTime.UtcNow, default);
        var first = await t.System.CreateDbContextAsync();
        var second = await t.System.CreateDbContextAsync();
        try
        {
#pragma warning disable CA2025 // both tasks are awaited before the contexts are disposed
            var running = store.MatchAsync(first, ReceiptWorld.Alice, default);
            await ai.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var progress = new List<string>();
            var waiting = store.MatchAsync(second, ReceiptWorld.Alice, default, progress.Add);
#pragma warning restore CA2025
            Assert.Equal(["waiting for another sync to finish matching"], progress);
            release.SetResult();
            await Task.WhenAll(running, waiting);
        }
        finally
        {
            release.TrySetResult();
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task Receipts_with_other_choices_are_asked_separately()
    {
        // One booking is in another category of the project: its default choice differs, so its lines go in their own request.
        await using var t = await WorldAsync();
        var ai = new FakeClassifier(_ => null);
        var store = new ReceiptStore(ai);
        await ReceiptWorld.AddDebitAsync(t, ReceiptWorld.Id(501), Payment);
        await ReceiptWorld.AddDebitAsync(t, ReceiptWorld.Id(502), Payment + 100);
        await using (var db = await t.System.CreateDbContextAsync())
        {
            (await db.Transactions.SingleAsync(x => x.Id == ReceiptWorld.Id(502))).CategoryId = ReceiptWorld.Drinks;
            await db.SaveChangesAsync();
            for (var n = 1; n <= 2; n++)
                await ReceiptStore.AddAsync(db, ReceiptWorld.Alice, ReceiptShops.Rewe,
                    ReweInvoice.Parse(SyntheticInvoice.Pdf(SyntheticInvoice.StandardLines(), bonus: -n, invoice: $"PN1000000000000{n}")), $"mail-{n}", DateTime.UtcNow, default);
            await store.MatchAsync(db, ReceiptWorld.Alice, default);
        }
        Assert.Equal(2, ai.Calls);
        Assert.Contains(ReceiptWorld.Drinks, ai.Choices);
    }

    [Fact]
    public async Task Items_follow_the_project_of_the_bookings_account_and_its_narrowing_for_the_source()
    {
        // A second project (listed first) shares the account but holds other categories: the booking's category
        // decides, so the household project's list applies.
        await using var t = await WorldAsync();
        var other = ReceiptWorld.Id(300);
        t.AddGroup(other, name: "Other", ownerId: ReceiptWorld.Alice);
        t.AddLeaf(ReceiptWorld.Id(301), other, "Hobby", ownerId: ReceiptWorld.Alice);
        var p = t.AddProject(41, "Other", ReceiptWorld.Alice);
        p.SortOrder = -1;
        t.AssignGroup(41, other);
        t.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = 41, AccountId = ReceiptWorld.Account });
        await t.Db.SaveChangesAsync();
        await using (var db = await t.System.CreateDbContextAsync())
            Assert.Equal(ReceiptWorld.Project, await ReceiptProjects.ResolveAsync(db, ReceiptWorld.Alice, ReceiptWorld.Account, ReceiptWorld.Groceries, default));
        await using (var db = await t.System.CreateDbContextAsync())
            Assert.Equal(41, await ReceiptProjects.ResolveAsync(db, ReceiptWorld.Alice, ReceiptWorld.Account, null, default));

        // Narrowed to nothing for the mailbox: the receipt does not split the booking.
        await using (var db = await t.System.CreateDbContextAsync())
        {
            db.ProjectReceiptSources.Add(new ProjectReceiptSource { OwnerUserId = ReceiptWorld.Alice, ProjectId = ReceiptWorld.Project, Source = ReceiptSources.Mail, CategoryIds = "" });
            await db.SaveChangesAsync();
        }
        await ReceiptWorld.AddDebitAsync(t, Debit, Payment);
        await AddAndMatchAsync(t, new ReceiptStore());
        Assert.Empty(await PartsAsync(t, Debit));

        // Narrowed to the drugstore category: it splits again.
        await using (var db = await t.System.CreateDbContextAsync())
        {
            (await db.ProjectReceiptSources.SingleAsync()).CategoryIds = ReceiptProjects.FormatIds([ReceiptWorld.Drugstore]);
            await db.SaveChangesAsync();
            await new ReceiptStore().MatchAsync(db, ReceiptWorld.Alice, default);
        }
        Assert.Contains(await PartsAsync(t, Debit), x => x.CategoryId == ReceiptWorld.Drugstore);
    }

    [Fact]
    public async Task Item_memory_of_another_project_does_not_apply()
    {
        await using var t = await WorldAsync();
        t.AddProject(41, "Other", ReceiptWorld.Alice);
        await t.Db.SaveChangesAsync();
        await using (var db = await t.System.CreateDbContextAsync())
        {
            db.ItemCategoryMemory.Add(new ItemCategoryMemory { OwnerUserId = ReceiptWorld.Alice, ProjectId = 41, NormalizedName = "apfelschorle", CategoryId = ReceiptWorld.Drugstore });
            await db.SaveChangesAsync();
        }
        await ReceiptWorld.AddDebitAsync(t, Debit, Payment);
        await AddAndMatchAsync(t, new ReceiptStore());
        Assert.DoesNotContain("Apfelschorle", (await PartsAsync(t, Debit)).Single(x => x.CategoryId == ReceiptWorld.Drugstore).OrderItems, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Items_the_ai_put_into_another_shops_category_return_to_the_booking_and_the_split_follows()
    {
        await using var t = await WorldAsync();
        var rewe = ReceiptWorld.Id(203);
        t.AddLeaf(rewe, ReceiptWorld.Id(100), "Rewe", ownerId: ReceiptWorld.Alice);
        await t.Db.SaveChangesAsync();
        var lidlDebit = ReceiptWorld.Id(510);
        await using (var db = await t.System.CreateDbContextAsync())
        {
            db.Transactions.Add(new Transaction
            {
                Id = lidlDebit,
                OwnerUserId = ReceiptWorld.Alice,
                AccountId = ReceiptWorld.Account,
                Amount = -633,
                Timestamp = new DateTimeOffset(ReceiptWorld.Delivered.AddDays(1)).ToUnixTimeSeconds(),
                Name = "S. Payment Solutions GmbH",
                Purpose = "Lidl sagt Danke Testfiliale Lidl Pay",
                CategoryId = ReceiptWorld.Groceries,
                CategorySetBy = TransactionCategorySource.Rule,
            });
            await db.SaveChangesAsync();
        }
        var day = DateOnly.FromDateTime(ReceiptWorld.Delivered);
        var receipt = new ParsedReceipt("LIDL-T-1", null, day, day, day,
        [
            new ParsedReceiptItem("Testeis Vanille", "1", "B", 399, 399, false, ReceiptItemKind.Product),
            new ParsedReceiptItem("Testbutter", "1", "B", 239, 239, false, ReceiptItemKind.Product),
            new ParsedReceiptItem("Pfand 0,25 EM", "12", "A", 25, 300, false, ReceiptItemKind.Deposit),
            new ParsedReceiptItem("Pfandrückgabe", "4", "A", -25, -100, false, ReceiptItemKind.Deposit),
            new ParsedReceiptItem("Pfandrückgabe", "16", "A", -25, -400, false, ReceiptItemKind.Deposit),
            new ParsedReceiptItem("Testduschgel", "1", "A", 195, 195, false, ReceiptItemKind.Product),
        ], 633, 0, 633);
        var store = new ReceiptStore();
        await using (var db = await t.System.CreateDbContextAsync())
        {
            await ReceiptStore.AddAsync(db, ReceiptWorld.Alice, ReceiptShops.Lidl, receipt, null, DateTime.UtcNow, default, store: "Lidl Teststraße 1");
            await store.MatchAsync(db, ReceiptWorld.Alice, default);
        }

        // What an earlier AI answer left: the food in another shop's category and the deposits in a part of their own.
        await using (var db = await t.System.CreateDbContextAsync())
        {
            var invoice = await db.ReceiptInvoices.Include(i => i.Items).SingleAsync();
            Assert.Equal(lidlDebit, invoice.TransactionId);
            foreach (var item in invoice.Items.Where(i => i.Name is "Testeis Vanille" or "Testbutter"))
                (item.CategoryId, item.CategorySource) = (rewe, ReceiptCategorySource.Ai);
            var parent = await db.Transactions.SingleAsync(x => x.Id == lidlDebit);
            await SplitService.WritePartsAsync(db, parent,
                [new SplitPartWrite(-638, rewe, null, "Testeis Vanille; Testbutter"), new SplitPartWrite(-195, ReceiptWorld.Drugstore, null, "Testduschgel"),
                 new SplitPartWrite(200, ReceiptWorld.Groceries, null, "Pfand")], default);
            invoice.SplitSignature = ReceiptCategorizer.Signature([(-638, rewe), (-195, ReceiptWorld.Drugstore), (200, ReceiptWorld.Groceries)]);
            await db.SaveChangesAsync();
        }

        await using (var db = await t.System.CreateDbContextAsync())
            await store.MatchAsync(db, ReceiptWorld.Alice, default);

        var parts = await PartsAsync(t, lidlDebit);
        Assert.Equal(2, parts.Count);
        Assert.DoesNotContain(parts, p => p.CategoryId == rewe);
        Assert.All(parts, p => Assert.True(p.Amount < 0));
        var food = parts.Single(p => p.CategoryId == ReceiptWorld.Groceries);
        Assert.Equal(-438, food.Amount);
        Assert.Equal("Testeis Vanille; Testbutter; Pfand 0,25 EM ×12; Pfandrückgabe ×4; Pfandrückgabe ×16", food.OrderItems);
        Assert.Equal(-195, parts.Single(p => p.CategoryId == ReceiptWorld.Drugstore).Amount);
    }

    private sealed class FakeClassifier(Func<string, long?> answer) : IReceiptItemClassifier
    {
        public List<string> Asked { get; } = [];

        public List<long> Choices { get; } = [];

        public int Calls { get; private set; }

        /// <summary>When set, each request waits for it (after signalling <see cref="Entered"/>).</summary>
        public Task? Hold { get; init; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Dictionary<long, long>> ClassifyAsync(
            string ownerUserId, IReadOnlyList<DatedReceiptLine> dated, IReadOnlyList<AllowedCategory> choices, CancellationToken ct)
        {
            Calls++;
            var lines = dated.Select(d => d.Line).ToList();
            Asked.AddRange(lines.Select(l => l.Name));
            Choices.AddRange(choices.Select(c => c.CategoryId));
            Entered.TrySetResult();
            if (Hold != null) await Hold;
            return lines.Where(l => answer(l.Name) != null).ToDictionary(l => l.Id, l => answer(l.Name)!.Value);
        }
    }
}
