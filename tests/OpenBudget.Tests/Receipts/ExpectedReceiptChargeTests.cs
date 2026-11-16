using OpenBudget.Connectors;
using OpenBudget.Connectors.Receipts;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Receipts;

/// <summary>
/// The expected charges of orders still waiting for their booking (ReceiptService.ExpectedChargesAsync) and their rows in
/// the Scheduled view: the account they are expected on, the estimated day and who sees them.
/// </summary>
public sealed class ExpectedReceiptChargeTests
{
    private const string Card = "visa-3333";
    private static readonly DateTime Day = new(2026, 3, 12, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Day);

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    /// <summary>
    /// Bob's Amazon card mapped to Alice's shared account; two of his orders were charged 1 and 3 days after delivery,
    /// one waits since <paramref name="waitingDelivered"/>, and one is too old to still be waiting.
    /// </summary>
    private static async Task<(TestDb T, long Waiting)> WorldAsync(DateTime waitingDelivered)
    {
        var t = await AccessWorld.BuildAsync();
        await using var db = await t.System.CreateDbContextAsync();
        var login = new BankLogin { OwnerUserId = AccessWorld.Bob, ConnectorType = "amazon", DisplayName = "Shop" };
        db.BankLogins.Add(login);
        await db.SaveChangesAsync();
        db.BankLoginAccounts.Add(new BankLoginAccount { BankLoginId = login.Id, RemoteId = Card, Name = "Visa •••• 3333", EnrichAccountId = AccessWorld.AccountWp });
        var n = 0;
        foreach (var (delivered, delay) in new[] { (Day.AddDays(-40), 1), (Day.AddDays(-30), 3) })
        {
            var booking = AccessWorld.Id(AccessWorld.SourceAlice, 60 + n);
            db.Transactions.Add(new Transaction
            {
                Id = booking,
                OwnerUserId = AccessWorld.Alice,
                AccountId = AccessWorld.AccountWp,
                Amount = -1000,
                Timestamp = new DateTimeOffset(delivered.AddDays(delay)).ToUnixTimeSeconds(),
                Name = "AMAZON EU",
            });
            db.ReceiptInvoices.Add(Receipt(login.Id, $"302-0000000-000010{n++}", 1000, delivered, ReceiptMatchState.Matched, booking));
        }
        var waiting = Receipt(login.Id, "302-0000000-0000200", 854, waitingDelivered, ReceiptMatchState.Unmatched, null);
        db.ReceiptInvoices.AddRange(waiting, Receipt(login.Id, "302-0000000-0000201", 999, Day.AddDays(-60), ReceiptMatchState.Unmatched, null));
        await db.SaveChangesAsync();
        db.ReceiptItems.Add(new ReceiptItem { ReceiptInvoiceId = waiting.Id, Position = 1, Name = "USB cable", Kind = ReceiptItemKind.Product, TotalCents = 854 });
        await db.SaveChangesAsync();
        return (t, waiting.Id);
    }

    private static ReceiptInvoice Receipt(int login, string order, long cents, DateTime day, ReceiptMatchState state, long? booking) => new()
    {
        OwnerUserId = AccessWorld.Bob,
        Shop = ReceiptShops.Amazon,
        BankLoginId = login,
        SourceKey = order,
        InvoiceNumber = order,
        OrderNumber = order,
        PaymentMethodId = Card,
        TotalCents = cents,
        PaymentCents = cents,
        DeliveryDate = day,
        ReadAt = day,
        State = state,
        TransactionId = booking,
        ManualMatch = booking != null,
    };

    private static ReceiptService Receipts(TestDb t, string user)
    {
        var view = t.ForUser(user);
        return new ReceiptService(view.Factory, t.System, view.Access, null!, new ReceiptStore(), null!, t.Notifier, new ChangeOrigin(), new FixedClock(Day), TestRegistry.Connectors);
    }

    [Fact]
    public async Task A_waiting_order_is_expected_on_its_cards_account_after_the_shops_usual_delay()
    {
        var (t, waiting) = await WorldAsync(Day.AddDays(-2));
        await using var _t = t;
        var charge = Assert.Single(await Receipts(t, AccessWorld.Alice).ExpectedChargesAsync(AccessWorld.Shared, Today));
        // delivered two days ago, charged 1 or 3 days after delivery so far: the median, 3 days, is tomorrow
        Assert.Equal((waiting, AccessWorld.AccountWp, Today.AddDays(1), -854L, "USB cable"), (charge.ReceiptId, charge.AccountId, charge.Date, charge.Cents, charge.FirstItem));

        var item = Assert.Single((await new ScheduledPaymentService(t.ForUser(AccessWorld.Alice).Factory, t.System, receipts: Receipts(t, AccessWorld.Alice))
            .GetAsync(AccessWorld.Shared, Today)).Items);
        Assert.Equal((ScheduledPaymentKind.Order, waiting, -8.54m, Today.AddDays(1)), (item.Kind, item.ReceiptId!.Value, item.Amount, item.NextDate));
    }

    [Fact]
    public async Task An_overdue_estimate_is_expected_today()
    {
        var (t, _) = await WorldAsync(Day.AddDays(-10));
        await using var _t = t;
        Assert.Equal(Today, Assert.Single(await Receipts(t, AccessWorld.Bob).ExpectedChargesAsync(AccessWorld.Shared, Today)).Date);
    }

    [Fact]
    public async Task A_project_without_the_cards_account_expects_nothing()
    {
        var (t, _) = await WorldAsync(Day.AddDays(-2));
        await using var _t = t;
        Assert.Empty(await Receipts(t, AccessWorld.Alice).ExpectedChargesAsync(AccessWorld.Private, Today));
    }
}
