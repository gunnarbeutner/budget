using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// Deterministic test-world seeding for the E2E suite. Mirrors the entity shapes used by the unit
/// tests' TestDb helpers, but writes to the real (temp) SQLite file the hosted app reads from.
/// </summary>
internal static class E2ESeed
{
    // Stable identifiers the tests assert against.
    public const int SourceId = 1;            // referenced by the demo project (delete blocked)
    public const int SpareSourceId = 2;       // unreferenced (delete allowed)
    public const string SpareSourceName = "spare";
    public const int ProjectId = 1;
    public const string ProjectName = "E2E Demo Project";
    public const int BudgetYear = 2026;
    public const int BudgetId = 1;            // the seeded active budget, for /budgets/{id}/edit

    // Logins: the owner of the whole baseline world, and a second user to share with.
    public const string UserName = "e2e";
    public const string SecondUserName = "guest";
    public const string Password = "E2e-Passw0rd!";

    private const long GroupId = 100;
    private const long ExpenseCategoryId = 200;
    private const long IncomeCategoryId = 201;

    private static readonly DateTime Fixed = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Empty every table. The temp DB already has its schema (migrations ran on startup). Query
    /// filters are ignored so rows orphaned by an earlier test (their parent deleted) go too.
    /// </summary>
    public static async Task ResetAsync(IDbContextFactory<BudgetDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.Holdings.RemoveRange(db.Holdings.IgnoreQueryFilters());
        db.BankStatements.RemoveRange(db.BankStatements.IgnoreQueryFilters());
        db.BankLoginAccounts.RemoveRange(db.BankLoginAccounts.IgnoreQueryFilters());
        db.BankLogins.RemoveRange(db.BankLogins.IgnoreQueryFilters());
        db.CloudFileLinks.RemoveRange(db.CloudFileLinks.IgnoreQueryFilters());
        db.CloudConnections.RemoveRange(db.CloudConnections.IgnoreQueryFilters());
        db.ProjectShares.RemoveRange(db.ProjectShares.IgnoreQueryFilters());
        db.Set<Microsoft.AspNetCore.Identity.IdentityUserPasskey<string>>().RemoveRange(db.Set<Microsoft.AspNetCore.Identity.IdentityUserPasskey<string>>());
        db.Users.RemoveRange(db.Users.IgnoreQueryFilters());
        db.MonthlyBudgetEntries.RemoveRange(db.MonthlyBudgetEntries.IgnoreQueryFilters());
        db.Budgets.RemoveRange(db.Budgets.IgnoreQueryFilters());
        db.CategoryRules.RemoveRange(db.CategoryRules.IgnoreQueryFilters());
        db.Transactions.RemoveRange(db.Transactions.IgnoreQueryFilters());
        db.ProjectTrackingItems.RemoveRange(db.ProjectTrackingItems.IgnoreQueryFilters());
        db.ProjectCategoryAssignments.RemoveRange(db.ProjectCategoryAssignments.IgnoreQueryFilters());
        db.ProjectAccountAssignments.RemoveRange(db.ProjectAccountAssignments.IgnoreQueryFilters());
        db.CategoryTypeAssignments.RemoveRange(db.CategoryTypeAssignments.IgnoreQueryFilters());
        db.Projects.RemoveRange(db.Projects.IgnoreQueryFilters());
        db.Categories.RemoveRange(db.Categories.IgnoreQueryFilters());
        db.CategoryGroups.RemoveRange(db.CategoryGroups.IgnoreQueryFilters());
        db.Accounts.RemoveRange(db.Accounts.IgnoreQueryFilters());
        db.AccountGroups.RemoveRange(db.AccountGroups.IgnoreQueryFilters());
        db.ImportSources.RemoveRange(db.ImportSources.IgnoreQueryFilters());
        await db.SaveChangesAsync();
    }

    public static async Task SeedBaselineAsync(IDbContextFactory<BudgetDbContext> factory, string ownerUserId)
    {
        await using var db = await factory.CreateDbContextAsync();

        db.ImportSources.Add(new ImportSource { Id = SourceId, Name = "legacy", CreatedAt = Fixed, OwnerUserId = ownerUserId, UploadToken = "e2e-token-legacy" });
        db.ImportSources.Add(new ImportSource { Id = SpareSourceId, Name = SpareSourceName, CreatedAt = Fixed, OwnerUserId = ownerUserId, UploadToken = "e2e-token-spare" });

        // Category.Type is the source's raw type code; the app classifies income/expense per-project
        // via CategoryTypeAssignment (below), not via this column.
        db.CategoryGroups.Add(new CategoryGroup { Id = GroupId, SourceId = SourceId, Name = "Living" });
        db.Categories.Add(new Category { Id = ExpenseCategoryId, SourceId = SourceId, Name = "Groceries", GroupId = GroupId, Type = 1 });
        db.Categories.Add(new Category { Id = IncomeCategoryId, SourceId = SourceId, Name = "Salary", GroupId = GroupId, Type = 1 });

        db.Projects.Add(new Project
        {
            Id = ProjectId,
            Name = ProjectName,
            Description = "Seeded by the E2E suite",
            SourceId = SourceId,
            OwnerUserId = ownerUserId,
            SortOrder = 0,
            CreatedAt = Fixed,
        });

        // Anchor the project on the group and track both leaf categories so overview/browse have
        // data to show. ProjectCategoryAssignment.CategoryId stores a CategoryGroup id (legacy name).
        db.ProjectCategoryAssignments.Add(new ProjectCategoryAssignment { ProjectId = ProjectId, CategoryId = GroupId });
        db.ProjectTrackingItems.Add(new ProjectTrackingItem { ProjectId = ProjectId, CategoryGroupId = GroupId });
        db.ProjectTrackingItems.Add(new ProjectTrackingItem { ProjectId = ProjectId, CategoryId = ExpenseCategoryId });
        db.ProjectTrackingItems.Add(new ProjectTrackingItem { ProjectId = ProjectId, CategoryId = IncomeCategoryId });

        // Mark Salary as income for this project; Groceries defaults to expense.
        db.CategoryTypeAssignments.Add(new CategoryTypeAssignment
        {
            ProjectId = ProjectId,
            CategoryId = IncomeCategoryId,
            ItemType = CategoryItemType.Category,
            IsIncome = true,
        });

        var budget = new Budget
        {
            Id = BudgetId,
            ProjectId = ProjectId,
            Year = BudgetYear,
            Name = $"{BudgetYear}",
            IsActive = true,
            CreatedAt = Fixed,
        };
        db.Budgets.Add(budget);
        await db.SaveChangesAsync(); // need budget.Id for the entry FKs

        AddEntry(db, budget.Id, ExpenseCategoryId, 100m);
        AddEntry(db, budget.Id, IncomeCategoryId, 500m);

        // A couple of actuals so spending figures aren't all zero. Amounts in cents;
        // negative = expense, positive = income (the source app convention).
        AddTransaction(db, id: 1, categoryId: ExpenseCategoryId, amountCents: -42_00, month: 1);
        AddTransaction(db, id: 2, categoryId: IncomeCategoryId, amountCents: 500_00, month: 1);

        await db.SaveChangesAsync();
    }

    private static void AddEntry(BudgetDbContext db, int budgetId, long categoryId, decimal monthly)
    {
        // Every entry here targets a leaf Category (not a CategoryGroup).
        var entry = new MonthlyBudgetEntry { BudgetId = budgetId, CategoryId = categoryId, ItemType = CategoryItemType.Category };
        for (int m = 1; m <= 12; m++) entry.SetMonthAmount(m, monthly);
        db.MonthlyBudgetEntries.Add(entry);
    }

    private static void AddTransaction(BudgetDbContext db, long id, long categoryId, long amountCents, int month)
    {
        var date = new DateTime(BudgetYear, month, 15, 0, 0, 0, DateTimeKind.Utc);
        db.Transactions.Add(new Transaction
        {
            Id = id,
            SourceId = SourceId,
            CategoryId = categoryId,
            Amount = amountCents,
            Timestamp = ((DateTimeOffset)date).ToUnixTimeSeconds(),
            Name = $"txn-{id}",
        });
    }
}

/// <summary>
/// The extra world the transaction-editing tests need on top of the baseline: one bank-connector account (editable)
/// with uncategorized rows, one MoneyMoney-owned account (read-only), both assigned to the demo project.
/// </summary>
internal static class EditingSeed
{
    public const string BankAccountName = "Example Giro";
    public const string MoneyMoneyAccountName = "Example MoneyMoney Account";
    public const string EnergyCategory = "Energy";

    public static readonly long BankAccountId = SourceScope.Namespace(E2ESeed.SourceId, 1);
    public static readonly long MoneyMoneyAccountId = SourceScope.Namespace(E2ESeed.SourceId, 2);

    public static async Task SeedAsync(IDbContextFactory<BudgetDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.Categories.Add(new Category { Id = 202, SourceId = E2ESeed.SourceId, Name = EnergyCategory, GroupId = 100, Type = 1 });
        db.Accounts.Add(new Account
        {
            Id = BankAccountId,
            SourceId = E2ESeed.SourceId,
            Name = BankAccountName,
            Provider = "fake",
            Type = 1,
            Currency = "EUR",
            Balances = "11:1000.000000,3:EUR,",
        });
        db.Accounts.Add(new Account { Id = MoneyMoneyAccountId, SourceId = E2ESeed.SourceId, Name = MoneyMoneyAccountName, Type = 1, Currency = "EUR", Balances = "11:50.000000,3:EUR," });
        await db.SaveChangesAsync();
        db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = E2ESeed.ProjectId, AccountId = BankAccountId });
        db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = E2ESeed.ProjectId, AccountId = MoneyMoneyAccountId });

        Add(db, 101, BankAccountId, -25_00, "Example Grocer", new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc), purpose: "Card payment 1234");
        Add(db, 102, BankAccountId, -30_00, "Example Grocer", new DateTime(2026, 3, 12, 0, 0, 0, DateTimeKind.Utc), isNew: true);
        Add(db, 103, BankAccountId, -12_50, "Example Grocer", new DateTime(2026, 3, 14, 0, 0, 0, DateTimeKind.Utc));
        Add(db, 104, BankAccountId, 2000_00, "Example Employer", new DateTime(2026, 3, 14, 0, 0, 0, DateTimeKind.Utc), category: 201);
        Add(db, 110, MoneyMoneyAccountId, -4_50, "Example Bakery", new DateTime(2026, 3, 11, 0, 0, 0, DateTimeKind.Utc));
        await db.SaveChangesAsync();
    }

    private static void Add(BudgetDbContext db, long id, long account, long cents, string payee, DateTime date, string? purpose = null, bool isNew = false, long? category = null) =>
        db.Transactions.Add(new Transaction
        {
            Id = id,
            SourceId = E2ESeed.SourceId,
            AccountId = account,
            Amount = cents,
            Currency = "EUR",
            Name = payee,
            Purpose = purpose,
            CategoryId = category,
            Timestamp = ((DateTimeOffset)date).ToUnixTimeSeconds(),
            ValueTimestamp = ((DateTimeOffset)date.AddDays(1)).ToUnixTimeSeconds(),
            CounterpartyIban = "DE89370400440532013000",
            CounterpartyBic = "COBADEFFXXX",
            BookingText = "Lastschrift",
            BankReference = $"REF-{id}",
            IsNew = isNew,
        });
}
