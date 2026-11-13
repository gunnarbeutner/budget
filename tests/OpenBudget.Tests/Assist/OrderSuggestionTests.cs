#pragma warning disable CA2000 // the world is disposed by the test
using Microsoft.EntityFrameworkCore;
using OpenBudget.Assist;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Assist;

/// <summary>Order items on matched bookings: item texts, per-item categories, single and split suggestions, pending bookings.</summary>
public sealed partial class CategorySuggestionTests
{
    private static readonly long Household = AccessWorld.Id(AccessWorld.SourceAlice, 212);
    private const string Card = "amazon-visa-1111";
    private static readonly DateTime ChargeDay = new(2026, 3, 11, 0, 0, 0, DateTimeKind.Utc);

    private sealed record OrderWorld(TestDb T, int LoginId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => T.DisposeAsync();
    }

    /// <summary>
    /// History: a snack order (booking 50, Groceries) and a fridge order (booking 51, Household). New: snacks and a
    /// fridge shipped together (booking 60, one charge) and shipped separately (bookings 61 and 62, two charges).
    /// </summary>
    private static async Task<OrderWorld> OrderWorldAsync()
    {
        var t = await WorldAsync();
        await using var db = await t.System.CreateDbContextAsync();
        db.Categories.Add(new Category { Id = Household, OwnerUserId = AccessWorld.Alice, Name = "Household", GroupId = AccessWorld.SharedGroup, Type = 1 });
        var login = new BankLogin { OwnerUserId = AccessWorld.Alice, ConnectorType = "amazon", DisplayName = "Shop" };
        db.BankLogins.Add(login);
        await db.SaveChangesAsync();
        db.BankLoginAccounts.Add(new BankLoginAccount { BankLoginId = login.Id, RemoteId = Card, Name = "Amazon Visa •••• 1111", EnrichAccountId = AccessWorld.AccountWp });
        db.Transactions.AddRange(
            Row(50, "AMAZON", Groceries, amount: -399), Row(51, "AMAZON", Household, amount: -59900),
            Row(60, "AMAZON", amount: -66147), Row(61, "AMAZON", amount: -1247), Row(62, "AMAZON", amount: -64900));
        AddOrder(db, login.Id, "301-0000000-0000050", (0, "Salted snack mix", "B000000001", 399));
        AddOrder(db, login.Id, "301-0000000-0000051", (0, "Fridge freezer combination", "B000000002", 59900));
        AddOrder(db, login.Id, "301-0000000-0000060", (0, "Salted snack mix", "B000000001", 1247), (0, "Fridge freezer combination", "B000000002", 64900));
        AddOrder(db, login.Id, "301-0000000-0000061", (0, "Salted snack mix", "B000000001", 1247), (1, "Fridge freezer combination", "B000000002", 64900));
        AddCharge(db, login.Id, "301-0000000-0000050", -399);
        AddCharge(db, login.Id, "301-0000000-0000051", -59900);
        AddCharge(db, login.Id, "301-0000000-0000060", -66147);
        AddCharge(db, login.Id, "301-0000000-0000061", -1247);
        AddCharge(db, login.Id, "301-0000000-0000061", -64900);
        await db.SaveChangesAsync();
        await new AmazonOrderStore().MatchAsync(db, AccessWorld.Alice, CancellationToken.None);
        return new OrderWorld(t, login.Id);
    }

    private static void AddOrder(BudgetDbContext db, int loginId, string orderId, params (int Shipment, string Title, string Asin, long Cents)[] items)
    {
        var order = new AmazonOrder { OwnerUserId = AccessWorld.Alice, BankLoginId = loginId, OrderId = orderId, DetailsFetchedAt = ChargeDay, TotalCents = items.Sum(i => i.Cents) };
        foreach (var i in items)
            order.Items.Add(new AmazonOrderItem { OwnerUserId = AccessWorld.Alice, Title = i.Title, Asin = i.Asin, Quantity = 1, PriceCents = i.Cents, Shipment = i.Shipment });
        db.AmazonOrders.Add(order);
    }

    private static void AddCharge(BudgetDbContext db, int loginId, string orderId, long cents) => db.AmazonCharges.Add(new AmazonCharge
    {
        OwnerUserId = AccessWorld.Alice,
        BankLoginId = loginId,
        OrderId = orderId,
        ChargeKey = $"{orderId}|{cents}",
        Date = ChargeDay,
        AmountCents = cents,
        InstrumentId = Card,
        Instrument = "Amazon Visa •••• 1111",
    });

    private static SplitService Splits(TestDb t) => new(t.ForUser(AccessWorld.Alice).Factory, t.Notifier, new ChangeOrigin());

    [Fact]
    public async Task Matched_bookings_carry_their_items_and_are_found_by_search_and_rules()
    {
        await using var w = await OrderWorldAsync();
        await using var db = await w.T.System.CreateDbContextAsync();
        var rows = await db.Transactions.AsNoTracking().Where(t => t.Id >= 50 && t.Id <= 62).ToDictionaryAsync(t => t.Id);
        Assert.Equal("Salted snack mix; Fridge freezer combination", rows[60].OrderItems);
        Assert.Equal("Salted snack mix", rows[61].OrderItems); // its own shipment only
        Assert.Equal("Fridge freezer combination", rows[62].OrderItems);
        Assert.True(RuleExpression.Matches("order_items:fridge", RuleSubject.Of(rows[62])));
        Assert.True(RuleExpression.Matches("fridge", RuleSubject.Of(rows[62])));
        Assert.False(RuleExpression.Matches("order_items:fridge", RuleSubject.Of(rows[61])));
    }

    [Fact]
    public async Task Separate_shipments_get_single_categories_and_a_joint_one_a_split()
    {
        await using var w = await OrderWorldAsync();
        var s = For(w.T, AccessWorld.Alice);

        var result = await s.Service.SuggestAsync(AccessWorld.Shared, [60, 61, 62], useAi: false);

        var byTx = result.Suggestions.ToDictionary(x => x.TransactionId);
        Assert.Equal((Groceries, "order", null), (byTx[61].CategoryId!.Value, byTx[61].Source, byTx[61].Split));
        Assert.Equal((Household, "order", null), (byTx[62].CategoryId!.Value, byTx[62].Source, byTx[62].Split));
        var split = byTx[60].Split!;
        Assert.Equal([(-1247L, Groceries), (-64900L, Household)], split.Select(p => (p.AmountCents, p.CategoryId)));
        Assert.Equal(Household, byTx[60].CategoryId);
        Assert.True(byTx[60].Confidence >= 0.9); // the same products were categorized before

        // Cached with the split.
        var again = await s.Service.SuggestAsync(AccessWorld.Shared, [60], useAi: false);
        Assert.Equal(2, again.Suggestions.Single().Split!.Count);

        // Accepting splits the booking and remembers the items' categories.
        var service = WithSplits(w.T);
        await service.AcceptSplitAsync(60, split);
        await using var db = await w.T.System.CreateDbContextAsync();
        var parts = await db.Transactions.AsNoTracking().Where(t => t.SplitParentId == 60).OrderBy(t => t.Amount).ToListAsync();
        Assert.Equal([(-64900L, Household, "Fridge freezer combination"), (-1247L, Groceries, "Salted snack mix")], parts.Select(p => (p.Amount, p.CategoryId!.Value, p.Note!)));
        Assert.Equal(2, await db.AmazonOrderItems.CountAsync(i => i.CategoryId != null));
    }

    private static CategorySuggestionService WithSplits(TestDb t)
    {
        var v = t.ForUser(AccessWorld.Alice);
        var ai = new FakeAssistClient(AnswerAllWith("Dining"));
        return new CategorySuggestionService(v.Factory, t.System, new CategorizationService(v.Factory),
            new CategoryRuleService(v.Factory, t.Notifier, new ChangeOrigin()), v.Spending,
            new TransactionEditService(v.Factory, t.Notifier, new ChangeOrigin()), new AssistSettingsService(v.Factory, t.System, ai), ai,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CategorySuggestionService>.Instance, TimeProvider.System, Splits(t));
    }

    [Fact]
    public async Task Unknown_items_go_to_the_ai_as_items_with_consent()
    {
        await using var w = await OrderWorldAsync();
        await using (var db = await w.T.System.CreateDbContextAsync())
        {
            db.Transactions.Add(Row(70, "AMAZON", amount: -2390));
            AddOrder(db, w.LoginId, "301-0000000-0000070", (0, "Cast iron pan", "B000000070", 2390));
            AddCharge(db, w.LoginId, "301-0000000-0000070", -2390);
            await db.SaveChangesAsync();
            await new AmazonOrderStore().MatchAsync(db, AccessWorld.Alice, CancellationToken.None);
        }
        await OptInAsync(w.T, AccessWorld.Alice);
        var s = For(w.T, AccessWorld.Alice);

        var result = await s.Service.SuggestAsync(AccessWorld.Shared, [70], useAi: true);

        var request = Assert.Single(s.Ai.Requests);
        Assert.Contains("Cast iron pan", request.UserContent, StringComparison.Ordinal);
        var suggestion = Assert.Single(result.Suggestions);
        Assert.Equal((Dining, "order"), (suggestion.CategoryId!.Value, suggestion.Source));
    }

    [Fact]
    public async Task A_pending_booking_shows_its_split_and_the_charge_follows_the_booked_row()
    {
        await using var w = await OrderWorldAsync();
        await using (var db = await w.T.System.CreateDbContextAsync())
        {
            var pending = Row(80, "AMAZON", amount: -2500);
            pending.Booked = false;
            db.Transactions.Add(pending);
            AddOrder(db, w.LoginId, "301-0000000-0000080", (0, "Salted snack mix", "B000000001", 500), (0, "Fridge freezer combination", "B000000002", 2000));
            AddCharge(db, w.LoginId, "301-0000000-0000080", -2500);
            await db.SaveChangesAsync();
            await new AmazonOrderStore().MatchAsync(db, AccessWorld.Alice, CancellationToken.None);
        }
        var service = WithSplits(w.T);
        var first = Assert.Single((await service.SuggestAsync(AccessWorld.Shared, [80], useAi: false)).Suggestions);
        Assert.NotNull(first.Split);

        // The bank sync replaces the pending row by the booked one: the charge follows it.
        await using (var db = await w.T.System.CreateDbContextAsync())
        {
            db.Transactions.Remove(await db.Transactions.SingleAsync(t => t.Id == 80));
            db.Transactions.Add(Row(81, "AMAZON", amount: -2500));
            await db.SaveChangesAsync();
            await new AmazonOrderStore().MatchAsync(db, AccessWorld.Alice, CancellationToken.None);
            Assert.Equal(81, (await db.AmazonCharges.SingleAsync(c => c.OrderId == "301-0000000-0000080")).TransactionId);
        }
        var booked = Assert.Single((await service.SuggestAsync(AccessWorld.Shared, [81], useAi: false)).Suggestions);
        await service.AcceptSplitAsync(81, booked.Split!);
    }
}
