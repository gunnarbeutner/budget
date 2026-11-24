using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Amazon;
using OpenBudget.Connectors.Receipts;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Receipts;

/// <summary>
/// An order source of one user whose payment method books on another user's account shared with them: mapping it,
/// matching and splitting the account owner's bookings, and what happens once the share is gone.
/// </summary>
public sealed class SharedOrderSourceTests
{
    private const string Card = "visa-2222";
    private static readonly long Booking = AccessWorld.Id(AccessWorld.SourceAlice, 40);
    private static readonly long Household = AccessWorld.Id(AccessWorld.SourceAlice, 201);
    private static readonly DateTime Day = new(2026, 3, 12, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Bob's Amazon login with one card; Alice's booking of 100.00 on the shared account; Alice's Shared project sends toothbrushes to Household.</summary>
    private static async Task<(TestDb T, int Instrument)> WorldAsync(ProjectRole? bobRole = ProjectRole.Editor)
    {
        var t = await AccessWorld.BuildAsync(bobRole);
        await using var db = await t.System.CreateDbContextAsync();
        db.Categories.Add(new Category { Id = Household, OwnerUserId = AccessWorld.Alice, Name = "Household", GroupId = AccessWorld.SharedGroup, Type = 1 });
        db.ProjectReceiptCategories.Add(new ProjectReceiptCategory { OwnerUserId = AccessWorld.Alice, ProjectId = AccessWorld.Shared, CategoryId = Household, Description = "Household" });
        db.ItemCategoryMemory.Add(new ItemCategoryMemory
        {
            OwnerUserId = AccessWorld.Alice,
            ProjectId = AccessWorld.Shared,
            NormalizedName = ReceiptCategorizer.Normalize("Example toothbrush"),
            CategoryId = Household,
            UpdatedAt = Day,
        });
        db.Transactions.Add(new Transaction
        {
            Id = Booking,
            OwnerUserId = AccessWorld.Alice,
            AccountId = AccessWorld.AccountWp,
            Amount = -10000,
            Timestamp = new DateTimeOffset(Day.AddDays(1)).ToUnixTimeSeconds(),
            Name = "AMAZON EU",
            CategoryId = AccessWorld.SharedLeaf,
            CategorySetBy = TransactionCategorySource.Rule,
        });
        var login = new BankLogin { OwnerUserId = AccessWorld.Bob, ConnectorType = "amazon", DisplayName = "Shop" };
        db.BankLogins.Add(login);
        await db.SaveChangesAsync();
        var instrument = new BankLoginAccount { BankLoginId = login.Id, RemoteId = Card, Name = "Visa •••• 2222" };
        db.BankLoginAccounts.Add(instrument);
        var receipt = new ReceiptInvoice
        {
            OwnerUserId = AccessWorld.Bob,
            Shop = ReceiptShops.Amazon,
            BankLoginId = login.Id,
            SourceKey = "charge-1",
            InvoiceNumber = "charge-1",
            OrderNumber = "302-0000000-0000001",
            PaymentMethodId = Card,
            PaymentMethod = "Visa •••• 2222",
            TotalCents = 10000,
            PaymentCents = 10000,
            DeliveryDate = Day,
            ReadAt = Day,
        };
        receipt.Items.Add(new ReceiptItem { OwnerUserId = AccessWorld.Bob, Position = 0, Name = "Example snack box", Quantity = "1", TotalCents = 6000 });
        receipt.Items.Add(new ReceiptItem { OwnerUserId = AccessWorld.Bob, Position = 1, Name = "Example toothbrush", Quantity = "1", TotalCents = 4000 });
        db.ReceiptInvoices.Add(receipt);
        await db.SaveChangesAsync();
        return (t, instrument.Id);
    }

    private static OrderEnrichmentService Orders(TestDb t, string user)
    {
        var view = t.ForUser(user);
        return new OrderEnrichmentService(view.Factory, t.System, new ReceiptStore(), view.Access, t.Notifier, new ChangeOrigin(),
            new ConnectorRegistry([AmazonConnector.CreateRegistration(new AmazonOptions())]));
    }

    private static ReceiptService Receipts(TestDb t, string user)
    {
        var view = t.ForUser(user);
        return new ReceiptService(view.Factory, t.System, view.Access, null!, new ReceiptStore(), null!, t.Notifier, new ChangeOrigin(), TimeProvider.System, TestRegistry.Connectors);
    }

    [Fact]
    public async Task An_editor_may_map_a_payment_method_to_a_shared_account_but_not_to_one_not_shared()
    {
        var (t, instrument) = await WorldAsync();
        await using var _ = t;
        var orders = Orders(t, AccessWorld.Bob);
        var candidates = await orders.GetCandidatesAsync(instrument);
        Assert.Contains(candidates, c => c.AccountId == AccessWorld.AccountWp && c.Shared && c.GroupId == null);
        Assert.Contains(candidates, c => c.AccountId == AccessWorld.AccountBob && !c.Shared);
        Assert.DoesNotContain(candidates, c => c.AccountId == AccessWorld.AccountPrivate);

        await Assert.ThrowsAsync<AccessDeniedException>(() => orders.MapAsync(instrument, AccessWorld.AccountPrivate));
        await orders.MapAsync(instrument, AccessWorld.AccountWp);
        Assert.Equal(AccessWorld.AccountWp, (await orders.GetInstrumentsAsync((await LoginIdAsync(t)))).Single().AccountId);
    }

    [Fact]
    public async Task A_viewer_may_not_map_to_the_shared_account()
    {
        var (t, instrument) = await WorldAsync(ProjectRole.Viewer);
        await using var _ = t;
        var orders = Orders(t, AccessWorld.Bob);
        Assert.DoesNotContain(await orders.GetCandidatesAsync(instrument), c => c.AccountId == AccessWorld.AccountWp);
        await Assert.ThrowsAsync<AccessDeniedException>(() => orders.MapAsync(instrument, AccessWorld.AccountWp));
    }

    [Fact]
    public async Task The_receipt_splits_the_account_owners_booking_with_their_categories_and_parts_they_own()
    {
        var (t, instrument) = await WorldAsync();
        await using var _ = t;
        await Orders(t, AccessWorld.Bob).MapAsync(instrument, AccessWorld.AccountWp);

        await using var db = await t.System.CreateDbContextAsync();
        var receipt = await db.ReceiptInvoices.AsNoTracking().SingleAsync();
        Assert.Equal((ReceiptMatchState.Matched, (long?)Booking), (receipt.State, receipt.TransactionId));
        Assert.NotNull(receipt.SplitSignature);
        var parent = await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == Booking);
        Assert.Equal(SplitState.Parent, parent.Splitted);
        Assert.Contains("Example toothbrush", parent.OrderItems, StringComparison.Ordinal);
        var parts = await db.Transactions.AsNoTracking().Where(x => x.SplitParentId == Booking && x.Splitted == SplitState.Child).ToListAsync();
        Assert.All(parts, p => Assert.Equal(AccessWorld.Alice, p.OwnerUserId));
        Assert.Equal([(-6000L, (long?)null), (-4000L, Household)], parts.OrderBy(p => p.Amount).Select(p => (p.Amount, p.CategoryId)));

        // Both see the receipt on the booking; Bob's list has it matched.
        Assert.Equal(receipt.Id, (await Receipts(t, AccessWorld.Alice).GetForBookingAsync(Booking))!.ReceiptId);
        Assert.NotNull(await Receipts(t, AccessWorld.Alice).GetReceiptAsync(receipt.Id));
        Assert.Equal(receipt.Id, (await Receipts(t, AccessWorld.Bob).GetForBookingAsync(Booking))!.ReceiptId);
        Assert.Equal(ReceiptReviewState.Split, (await Receipts(t, AccessWorld.Bob).ListAllAsync()).Single().State);

        // Alice moves the snacks to Household too: her memory and her categories.
        var item = await db.ReceiptItems.AsNoTracking().SingleAsync(i => i.ReceiptInvoiceId == receipt.Id && i.Position == 0);
        await Receipts(t, AccessWorld.Alice).MoveItemAsync(item.Id, Household);
        await using var check = await t.System.CreateDbContextAsync();
        Assert.True(await check.ItemCategoryMemory.AnyAsync(m => m.OwnerUserId == AccessWorld.Alice && m.NormalizedName == ReceiptCategorizer.Normalize("Example snack box")));
        Assert.Equal(Household, (await check.ReceiptItems.AsNoTracking().SingleAsync(i => i.Id == item.Id)).CategoryId);
    }

    [Fact]
    public async Task A_booking_with_the_account_owners_own_receipt_is_taken()
    {
        var (t, instrument) = await WorldAsync();
        await using var _ = t;
        await using (var db = await t.System.CreateDbContextAsync())
        {
            db.ReceiptInvoices.Add(new ReceiptInvoice
            {
                OwnerUserId = AccessWorld.Alice,
                Shop = ReceiptShops.Rewe,
                InvoiceNumber = "alice-1",
                PaymentCents = 10000,
                TotalCents = 10000,
                TransactionId = Booking,
                ManualMatch = true,
                State = ReceiptMatchState.Matched,
                DeliveryDate = Day,
                ReadAt = Day,
            });
            await db.SaveChangesAsync();
        }
        await Orders(t, AccessWorld.Bob).MapAsync(instrument, AccessWorld.AccountWp);
        await using var check = await t.System.CreateDbContextAsync();
        var bobs = await check.ReceiptInvoices.AsNoTracking().SingleAsync(r => r.OwnerUserId == AccessWorld.Bob);
        Assert.NotEqual(Booking, bobs.TransactionId);
        Assert.DoesNotContain(await Receipts(t, AccessWorld.Bob).CandidatesAsync(bobs.Id), c => c.TransactionId == Booking);
    }

    [Fact]
    public async Task Once_the_share_is_gone_the_receipt_lets_go_of_the_booking()
    {
        var (t, instrument) = await WorldAsync();
        await using var _ = t;
        await Orders(t, AccessWorld.Bob).MapAsync(instrument, AccessWorld.AccountWp);
        await using (var db = await t.System.CreateDbContextAsync())
        {
            db.ProjectShares.RemoveRange(db.ProjectShares);
            await db.SaveChangesAsync();
            await new ReceiptStore().MatchAsync(db, AccessWorld.Bob, default);
        }
        await using var check = await t.System.CreateDbContextAsync();
        var receipt = await check.ReceiptInvoices.AsNoTracking().SingleAsync();
        Assert.Null(receipt.TransactionId);
        Assert.Equal(ReceiptMatchState.Skipped, receipt.State);
        // Alice's booking keeps its split; Bob no longer sees it.
        Assert.Equal(SplitState.Parent, (await check.Transactions.AsNoTracking().SingleAsync(x => x.Id == Booking)).Splitted);
        Assert.Null(await Receipts(t, AccessWorld.Bob).GetForBookingAsync(Booking));
    }

    private static async Task<int> LoginIdAsync(TestDb t)
    {
        await using var db = await t.System.CreateDbContextAsync();
        return await db.BankLogins.Where(l => l.OwnerUserId == AccessWorld.Bob).Select(l => l.Id).SingleAsync();
    }
}
