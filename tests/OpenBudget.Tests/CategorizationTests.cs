#pragma warning disable CA2000 // test objects live for the test
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.FakeBank;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;
using OpenBudget.Services;
using OpenBudget.Tests.Connectors;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>Category picker data and search, similar-transaction lookup, list filters, running balance, is_new from syncs.</summary>
public sealed class CategorizationTests
{
    private const int Source = 2;
    private const string Owner = "u-alice";

    private static long[] L(params long[] values) => values;

    private static long Id(long row) => RowIds.Namespace(Source, row);

    private static (List<CategoryGroup> Groups, List<Category> Categories) Tree()
    {
        // Hidden unnamed root (sentinel), "Haushalt" > { Lebensmittel, Strom }, "Freizeit" > { Reisen }, an empty group "Leer", a root-level category.
        List<CategoryGroup> groups =
        [
            new() { Id = RowIds.SentinelRoot(Source), OwnerUserId = Owner, Name = "" },
            new() { Id = Id(10), OwnerUserId = Owner, Name = "Haushalt", ParentGroupId = RowIds.SentinelRoot(Source), Sort = 1 },
            new() { Id = Id(11), OwnerUserId = Owner, Name = "Freizeit", ParentGroupId = RowIds.SentinelRoot(Source), Sort = 2 },
            new() { Id = Id(12), OwnerUserId = Owner, Name = "Leer", ParentGroupId = RowIds.SentinelRoot(Source), Sort = 3 },
            new() { Id = Id(13), OwnerUserId = Owner, Name = "Nested empty", ParentGroupId = Id(12) },
        ];
        List<Category> cats =
        [
            new() { Id = Id(100), OwnerUserId = Owner, Name = "Strom", GroupId = Id(10), Sort = 2 },
            new() { Id = Id(101), OwnerUserId = Owner, Name = "Lebensmittel", GroupId = Id(10), Sort = 1 },
            new() { Id = Id(102), OwnerUserId = Owner, Name = "Reisen", GroupId = Id(11) },
            new() { Id = Id(103), OwnerUserId = Owner, Name = "Café & Bäckerei", GroupId = Id(11), Sort = 0 },
            new() { Id = Id(104), OwnerUserId = Owner, Name = "", GroupId = Id(10) },
            new() { Id = Id(105), OwnerUserId = Owner, Name = "Loose", GroupId = null },
        ];
        return (groups, cats);
    }

    // ---- picker data and search ----

    [Fact]
    public void The_picker_lists_assignable_categories_with_their_path_in_tree_order()
    {
        var (groups, cats) = Tree();
        var data = CategoryPickerLogic.Build(groups, cats, Source);

        Assert.Equal(
            ["Haushalt › Lebensmittel", "Haushalt › Strom", "Freizeit › Café & Bäckerei", "Freizeit › Reisen", "Loose"],
            data.Options.Select(o => o.Path));
        Assert.DoesNotContain(data.Options, o => o.Name.Length == 0);                 // the unnamed sentinel category
        Assert.Equal("Haushalt › Lebensmittel", data.Options.Single(o => o.Id == Id(101)).Path);
    }

    [Fact]
    public void The_picker_tree_has_group_headers_indentation_and_no_empty_groups()
    {
        var (groups, cats) = Tree();
        var tree = CategoryPickerLogic.Build(groups, cats, Source).Tree;

        Assert.DoesNotContain(tree, r => r.Name is "Leer" or "Nested empty");
        Assert.DoesNotContain(tree, r => r.Name.Length == 0);
        var haushalt = tree.Single(r => r is { IsGroup: true, Name: "Haushalt" });
        var strom = tree.Single(r => r.Id == Id(100));
        Assert.Equal(0, haushalt.Depth);
        Assert.Equal(1, strom.Depth);
        Assert.True(tree.ToList().IndexOf(haushalt) < tree.ToList().IndexOf(strom));
        Assert.Equal(0, tree.Single(r => r.Id == Id(105)).Depth);                       // root-level category
    }

    [Theory]
    [InlineData("lebens", "Haushalt › Lebensmittel")]
    [InlineData("LEBENSMITTEL", "Haushalt › Lebensmittel")]
    [InlineData("haushalt leb", "Haushalt › Lebensmittel")]    // tokens match across path and name
    [InlineData("cafe", "Freizeit › Café & Bäckerei")]         // diacritics are ignored
    [InlineData("backerei", "Freizeit › Café & Bäckerei")]
    [InlineData("freizeit", "Freizeit › Café & Bäckerei")]     // a group name finds its categories
    public void Search_matches_name_and_path_ignoring_case_and_diacritics(string query, string expectedFirst)
    {
        var (groups, cats) = Tree();
        var hits = CategoryPickerLogic.Search(CategoryPickerLogic.Build(groups, cats, Source).Options, query);
        Assert.Contains(hits, h => h.Path == expectedFirst);
    }

    [Fact]
    public void Search_ranks_name_hits_before_path_only_hits_and_empty_means_everything()
    {
        var (groups, cats) = Tree();
        cats.Add(new Category { Id = Id(106), OwnerUserId = Owner, Name = "Haushaltsgeld", GroupId = Id(11) });
        var options = CategoryPickerLogic.Build(groups, cats, Source).Options;

        var hits = CategoryPickerLogic.Search(options, "haushalt");
        Assert.Equal("Haushaltsgeld", hits[0].Name);                                     // name starts with it
        Assert.Contains(hits, h => h.Name == "Strom");                                  // path only, later
        Assert.Equal(options.Count, CategoryPickerLogic.Search(options, "  ").Count);
        Assert.Empty(CategoryPickerLogic.Search(options, "zzz"));
        Assert.Empty(CategoryPickerLogic.Search(options, "haushalt reisen"));                // all tokens must match
    }

    [Fact]
    public void Search_folds_the_german_sharp_s()
    {
        var options = new[] { new CategoryOption(1, "Straßenbahn", "Verkehr › Straßenbahn", null) };
        Assert.Single(CategoryPickerLogic.Search(options, "strassenbahn"));
    }

    private static async Task<TestDb> WorldAsync()
    {
        var t = new TestDb();
        t.AddUser("u-alice", "alice");
        t.AddIdSpace("u-alice", Source);
        var (groups, cats) = Tree();
        t.Db.CategoryGroups.AddRange(groups);
        await t.Db.SaveChangesAsync();
        t.Db.Categories.AddRange(cats);
        t.Db.Accounts.Add(new Account { Id = Id(1), OwnerUserId = Owner, Name = "Bank", Provider = "fake" });
        t.Db.Accounts.Add(new Account { Id = Id(2), OwnerUserId = Owner, Name = "MM", Provider = AccountProviders.MoneyMoney });
        await t.Db.SaveChangesAsync();
        return t;
    }

    private static void AddTx(TestDb t, long id, long account, string? payee, long? category, int day) =>
        t.Db.Transactions.Add(new Transaction
        {
            Id = id,
            OwnerUserId = Owner,
            AccountId = account,
            Name = payee,
            CategoryId = category,
            Amount = -1000,
            Timestamp = new DateTimeOffset(2026, 5, day, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
        });

    // ---- similar transactions ----

    [Fact]
    public async Task Similar_finds_uncategorized_rows_of_the_same_payee_that_can_be_edited()
    {
        await using var t = await WorldAsync();
        AddTx(t, 100, Id(1), "Example Shop", null, 1);                   // the one just categorized (excluded)
        AddTx(t, 101, Id(1), "example shop", null, 2);                   // similar
        AddTx(t, 102, Id(1), "Example Shop", Id(101), 3);                // already categorized
        AddTx(t, 103, Id(1), "Another Shop", null, 4);                   // other payee
        AddTx(t, 104, Id(2), "Example Shop", null, 5);                   // MoneyMoney account: read-only
        AddTx(t, 105, Id(1), "Example Shop", Id(104), 6);                // sentinel category counts as uncategorized
        await t.Db.SaveChangesAsync();

        var ids = await new CategorizationService(t.Factory).FindSimilarUncategorizedAsync(Owner, "Example Shop", [100]);

        Assert.Equal(L(101, 105), ids.Order());
    }

    [Fact]
    public async Task Similar_respects_the_users_edit_rights()
    {
        await using var t = await WorldAsync();
        t.AddUser("u-bob", "bob");
        AddTx(t, 101, Id(1), "Example Shop", null, 2);
        await t.Db.SaveChangesAsync();

        var asBob = new CategorizationService(t.ForUser("u-bob").Factory);
        Assert.Empty(await asBob.FindSimilarUncategorizedAsync(Owner, "Example Shop", []));
        var asAlice = new CategorizationService(t.ForUser("u-alice").Factory);
        Assert.Single(await asAlice.FindSimilarUncategorizedAsync(Owner, "Example Shop", []));
    }

    // ---- list filters ----

    [Fact]
    public async Task The_list_filters_by_check_mark_direction_new_period_and_payee()
    {
        await using var t = await WorldAsync();
        AddTx(t, 100, Id(1), "Example Shop", Id(101), 1);
        AddTx(t, 101, Id(1), "Example Salary", Id(101), 2);
        AddTx(t, 102, Id(1), "example shop", Id(101), 20);
        await t.Db.SaveChangesAsync();
        await using (var db = await t.System.CreateDbContextAsync())
        {
            var rows = await db.Transactions.ToListAsync();
            rows.Single(r => r.Id == 101).Amount = 250_000;
            rows.Single(r => r.Id == 100).Checked = true;
            rows.Single(r => r.Id == 102).IsNew = true;
            await db.SaveChangesAsync();
        }
        async Task<long[]> Ids(TransactionQuery q) =>
            (await t.TransactionQueries.QueryAsync(q)).Rows.Select(r => r.Id).Order().ToArray();
        var baseQuery = new TransactionQuery { OwnerUserId = Owner, Take = 100 };

        Assert.Equal(L(100), await Ids(new TransactionQuery { OwnerUserId = Owner, Take = 100, Checked = true }));
        Assert.Equal(L(101, 102), await Ids(new TransactionQuery { OwnerUserId = Owner, Take = 100, Checked = false }));
        Assert.Equal(L(101), await Ids(new TransactionQuery { OwnerUserId = Owner, Take = 100, Direction = AmountDirection.Incoming }));
        Assert.Equal(L(100, 102), await Ids(new TransactionQuery { OwnerUserId = Owner, Take = 100, Direction = AmountDirection.Outgoing }));
        Assert.Equal(L(102), await Ids(new TransactionQuery { OwnerUserId = Owner, Take = 100, NewOnly = true }));
        Assert.Equal(L(100, 102), await Ids(new TransactionQuery { OwnerUserId = Owner, Take = 100, PayeeEquals = "EXAMPLE SHOP" }));
        Assert.Equal(
            L(102),
            await Ids(new TransactionQuery
            {
                OwnerUserId = Owner,
                Take = 100,
                FromUtc = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc),
                ToUtcExclusive = new DateTime(2026, 5, 21, 0, 0, 0, DateTimeKind.Utc),
            }));
        Assert.Equal(3, (await t.TransactionQueries.QueryAsync(baseQuery)).TotalCount);
    }

    [Fact]
    public async Task The_note_is_searchable_and_select_all_returns_the_ids_in_list_order()
    {
        await using var t = await WorldAsync();
        AddTx(t, 100, Id(1), "A", Id(101), 1);
        AddTx(t, 101, Id(1), "B", Id(101), 2);
        await t.Db.SaveChangesAsync();
        await using (var db = await t.System.CreateDbContextAsync())
        {
            (await db.Transactions.SingleAsync(x => x.Id == 100)).Note = "Reimbursed by employer";
            await db.SaveChangesAsync();
        }

        var found = await t.TransactionQueries.QueryAsync(new TransactionQuery { OwnerUserId = Owner, SearchText = "employer" });
        Assert.Equal(100, Assert.Single(found.Rows).Id);
        Assert.Equal(L(101, 100), await t.TransactionQueries.QueryIdsAsync(new TransactionQuery { OwnerUserId = Owner }));
        Assert.Equal(L(100), await t.TransactionQueries.QueryIdsAsync(new TransactionQuery { OwnerUserId = Owner }, skip: 1, take: 1));
        var totals = await t.TransactionQueries.GetSelectionTotalsAsync([100, 101]);
        Assert.Equal((2, -20m), totals);
    }

    // ---- running balance ----

    private static DateOnly Day(int d) => new(2026, 5, d);

    [Fact]
    public void The_running_balance_is_derived_backwards_from_the_current_balance()
    {
        // current 1000; flows: +100 on the 3rd, -30 on the 5th, -20 on the 5th, +50 on the 9th.
        var flows = new (DateTime, decimal)[]
        {
            (new DateTime(2026, 5, 3, 0, 0, 0, DateTimeKind.Utc), 100m),
            (new DateTime(2026, 5, 5, 8, 0, 0, DateTimeKind.Utc), -30m),
            (new DateTime(2026, 5, 5, 9, 0, 0, DateTimeKind.Utc), -20m),
            (new DateTime(2026, 5, 9, 0, 0, 0, DateTimeKind.Utc), 50m),
        };
        var balance = new RunningBalance(1000m, flows, d => DateOnly.FromDateTime(d));

        Assert.Equal(1000m, balance.AtEndOf(Day(12)));  // after everything
        Assert.Equal(1000m, balance.AtEndOf(Day(9)));
        Assert.Equal(950m, balance.AtEndOf(Day(7)));    // a day without bookings: before the +50 of the 9th
        Assert.Equal(950m, balance.AtEndOf(Day(5)));    // both bookings of the 5th are included
        Assert.Equal(1000m, balance.AtEndOf(Day(3)));   // 1000 - (-50 + 50)
        Assert.Equal(900m, balance.AtEndOf(Day(1)));    // before the first booking: 1000 - 100
    }

    // ---- is_new from connector syncs ----

    [Fact]
    public async Task Rows_a_connector_inserts_are_new_but_pending_and_adopted_rows_are_not()
    {
        await using var w = await BankWorld.CreateAsync();
        var snapshot = w.BuildSnapshot([("MM Giro", "DE00 0000 0000 0000 0000 01")], [new BankWorld.MmTx(1, -52.10, new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc), "Old Name", "Old purpose")]);
        await w.ImportMoneyMoneyAsync(snapshot);
        var account = RowIds.Namespace(BankWorld.SourceAlice, 1);
        var id = await w.AddLoginAsync(BankWorld.Alice, "demo+pending");
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, false, false));
        var giro = await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId);
        await w.For(BankWorld.Alice).LinkAsync(giro.Id, account);
        Assert.True((await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, true, false))).Succeeded);
        // The first sync brings history; rows a later sync inserts are new.
        await using (var db = await w.T.System.CreateDbContextAsync())
            await db.Transactions.Where(t => t.AccountId == account && t.Booked && t.ExternalId != null && t.Name != "Old Name").ExecuteDeleteAsync();
        Assert.True((await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, true, false))).Succeeded);

        var rows = await w.TransactionsAsync(account);
        Assert.NotEmpty(rows);
        Assert.Contains(rows, r => r.Booked && r.ExternalId != null && r.Name != "Old Name");
        Assert.All(rows.Where(r => r.Booked && r.ExternalId != null && r.Name != "Old Name"), r => Assert.True(r.IsNew));
        Assert.All(rows.Where(r => !r.Booked), r => Assert.False(r.IsNew));
        Assert.All(rows.Where(r => r.Name == "Old Name"), r => Assert.False(r.IsNew));   // pre-existing MoneyMoney row stays as it was
        Assert.All(rows, r => Assert.Equal(TransactionCategorySource.Import, r.CategorySetBy));
    }

    [Fact]
    public async Task A_repeated_sync_keeps_what_the_user_did_to_known_rows()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice);
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, false, false));
        await w.LinkAllAsync(BankWorld.Alice, id);
        var account = (await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId)).LinkedAccountId!.Value;
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, true, false));
        var all = await w.TransactionsAsync(account);
        var salary = all.First(t => t.Name == "Example Employer");
        Assert.False(salary.IsNew); // the account's first sync brings history, not news
        Assert.Equal("DE89370400440532013000", salary.CounterpartyIban);
        Assert.Equal("COBADEFFXXX", salary.CounterpartyBic);
        Assert.Equal("Gehalt/Rente", salary.BookingText);
        Assert.NotNull(salary.ValueTimestamp);
        Assert.NotNull(salary.BankReference);
        var first = all.First();

        var edit = new TransactionEditService(w.View(BankWorld.Alice).Factory, w.T.Notifier, new ChangeOrigin());
        await edit.SetNoteAsync([first.Id], "keep me");
        await edit.SetCheckedAsync([first.Id], true);
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, true, false));

        var again = (await w.TransactionsAsync(account)).Single(t => t.Id == first.Id);
        Assert.Equal("keep me", again.Note);
        Assert.True(again.Checked);
        Assert.False(again.IsNew);
    }
}
