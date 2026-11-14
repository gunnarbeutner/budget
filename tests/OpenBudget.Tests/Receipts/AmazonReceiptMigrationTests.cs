using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.Amazon;
using OpenBudget.Connectors.Receipts;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Receipts;

/// <summary>Moving Amazon orders, items and charges from their former tables into the receipts.</summary>
public sealed class AmazonReceiptMigrationTests
{
    private const string Owner = "u-a";
    private const long Account = 5;
    private const long Food = 11;
    private const long Household = 12;

    private static async Task<(SqliteConnection Connection, BudgetDbContext Db)> DatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<BudgetDbContext>().UseSqlite(connection)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)).Options;
        var db = new BudgetDbContext(options);
        await db.Database.MigrateAsync();
        return (connection, db);
    }

    private static Transaction Booking(long id, long cents, long? category = Food) => new()
    {
        Id = id,
        OwnerUserId = Owner,
        AccountId = Account,
        Amount = cents,
        Timestamp = new DateTimeOffset(2026, 3, 12, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
        Name = "AMAZON EU",
        CategoryId = category,
        Booked = true,
    };

    [Fact]
    public async Task Charges_become_receipts_keeping_matches_and_the_splits_they_explain()
    {
        var (connection, db) = await DatabaseAsync();
        await using var _ = connection;
        await using var __ = db;
        db.UserDataStates.Add(new UserDataState { UserId = Owner, IdSpace = 2 });
        db.Projects.Add(new Project { Id = 1, Name = "Home", OwnerUserId = Owner });
        db.CategoryGroups.Add(new CategoryGroup { Id = 100, OwnerUserId = Owner, Name = "Home" });
        db.Categories.AddRange(
            new Category { Id = Food, OwnerUserId = Owner, Name = "Food", GroupId = 100 },
            new Category { Id = Household, OwnerUserId = Owner, Name = "Household", GroupId = 100 });
        db.ProjectCategoryAssignments.Add(new ProjectCategoryAssignment { ProjectId = 1, CategoryId = 100 });
        db.Accounts.Add(new Account { Id = Account, OwnerUserId = Owner, Name = "Card" });
        db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = 1, AccountId = Account });
        db.BankLogins.Add(new BankLogin { Id = 7, OwnerUserId = Owner, ConnectorType = "amazon", DisplayName = "Shop" });
        db.Transactions.AddRange(Booking(50, -4000), Booking(51, -2100));
        await db.SaveChangesAsync();
        db.BankLoginAccounts.Add(new BankLoginAccount { BankLoginId = 7, RemoteId = "visa", Name = "Visa •••• 1111", EnrichAccountId = Account });
        await db.SaveChangesAsync();
        // Booking 50 was split as an order suggestion said; booking 51 by hand into other amounts.
        var b50 = await db.Transactions.SingleAsync(t => t.Id == 50);
        await SplitService.WritePartsAsync(db, b50, [new SplitPartWrite(-1000, Food, "Snack", null), new SplitPartWrite(-3000, Household, "Kettle", null)], default);
        var b51 = await db.Transactions.SingleAsync(t => t.Id == 51);
        await SplitService.WritePartsAsync(db, b51, [new SplitPartWrite(-1500, Household, null, null), new SplitPartWrite(-600, Food, null, null)], default);

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO amazon_orders (Id, OwnerUserId, BankLoginId, OrderId, OrderDate, TotalCents, Currency, DetailsFetchedAt) VALUES
            (1, 'u-a', 7, '301-0000000-0000001', '2026-03-10 00:00:00', 4000, 'EUR', '2026-03-12 00:00:00'),
            (2, 'u-a', 7, '301-0000000-0000002', '2026-03-10 00:00:00', 2100, 'EUR', '2026-03-12 00:00:00'),
            (3, 'u-a', 7, '301-0000000-0000003', NULL, NULL, NULL, NULL);
            INSERT INTO amazon_order_items (Id, OwnerUserId, AmazonOrderId, Title, Quantity, PriceCents, Shipment, Asin, CategoryId) VALUES
            (1, 'u-a', 1, 'Snack', 1, 1000, 0, NULL, NULL),
            (2, 'u-a', 1, 'Kettle', 1, 3000, 0, NULL, 12),
            (3, 'u-a', 2, 'Lamp', 1, 2000, 0, NULL, 12);
            INSERT INTO amazon_charges (Id, OwnerUserId, BankLoginId, ChargeKey, OrderId, Date, AmountCents, Currency, InstrumentId, Instrument, State, TransactionId) VALUES
            (1, 'u-a', 7, 'k1', '301-0000000-0000001', '2026-03-11 00:00:00', -4000, 'EUR', 'visa', 'Visa •••• 1111', 1, 50),
            (2, 'u-a', 7, 'k2', '301-0000000-0000002', '2026-03-11 00:00:00', -2100, 'EUR', 'visa', 'Visa •••• 1111', 1, 51),
            (3, 'u-a', 7, 'k3', '301-0000000-0000003', '2026-03-11 00:00:00', -999, 'EUR', 'visa', 'Visa •••• 1111', 0, NULL),
            (4, 'u-a', 7, 'k4', '301-0000000-0000003', '2026-03-11 00:00:00', -100, 'EUR', 'gift', 'Amazon Gift Card', 3, NULL);
            """);

        var result = await AmazonReceiptMigration.RunAsync(db, DateTime.UtcNow);

        Assert.Equal(new AmazonMigrationResult(4, 2, 1, 1, 2), result);
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sqlite_master WHERE name LIKE 'amazon_%'").SingleAsync());
        var receipts = await db.ReceiptInvoices.Include(r => r.Items).OrderBy(r => r.SourceKey).ToListAsync();
        Assert.Equal(["k1", "k2", "k3", "k4"], receipts.Select(r => r.SourceKey));
        Assert.Equal([50L, 51L, null, null], receipts.Select(r => r.TransactionId));
        Assert.Equal(ReceiptMatchState.Skipped, receipts[3].State);
        Assert.NotNull(receipts[0].SplitSignature);
        Assert.Null(receipts[1].SplitSignature);
        Assert.Equal(["Snack", "Kettle"], receipts[0].Items.OrderBy(i => i.Position).Select(i => i.Name));
        Assert.Equal(["kettle", "lamp"], await db.ItemCategoryMemory.OrderBy(m => m.NormalizedName).Select(m => m.NormalizedName).ToListAsync());

        // Matching afterwards keeps both splits as they are.
        var before = await db.Transactions.AsNoTracking().Where(t => t.SplitParentId != null).OrderBy(t => t.Id).Select(t => new { t.Id, t.Amount, t.CategoryId, t.Note }).ToListAsync();
        await new ReceiptStore().MatchAsync(db, Owner, default);
        var after = await db.Transactions.AsNoTracking().Where(t => t.SplitParentId != null).OrderBy(t => t.Id).Select(t => new { t.Id, t.Amount, t.CategoryId, t.Note }).ToListAsync();
        Assert.Equal(before, after);
        Assert.Equal("Snack; Kettle", (await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == 50)).OrderItems);

        // Running again does nothing.
        Assert.Null(await AmazonReceiptMigration.RunAsync(db, DateTime.UtcNow));
    }
}
