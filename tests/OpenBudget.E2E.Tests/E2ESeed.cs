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

    private const long GroupId = 100;
    private const long ExpenseCategoryId = 200;
    private const long IncomeCategoryId = 201;

    private static readonly DateTime Fixed = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Empty every table. The temp DB already has its schema (migrations ran on startup).</summary>
    public static async Task ResetAsync(IDbContextFactory<BudgetDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.MonthlyBudgetEntries.RemoveRange(db.MonthlyBudgetEntries);
        db.Budgets.RemoveRange(db.Budgets);
        db.Transactions.RemoveRange(db.Transactions);
        db.ProjectTrackingItems.RemoveRange(db.ProjectTrackingItems);
        db.ProjectCategoryAssignments.RemoveRange(db.ProjectCategoryAssignments);
        db.ProjectAccountAssignments.RemoveRange(db.ProjectAccountAssignments);
        db.CategoryTypeAssignments.RemoveRange(db.CategoryTypeAssignments);
        db.Projects.RemoveRange(db.Projects);
        db.Categories.RemoveRange(db.Categories);
        db.CategoryGroups.RemoveRange(db.CategoryGroups);
        db.Accounts.RemoveRange(db.Accounts);
        db.AccountGroups.RemoveRange(db.AccountGroups);
        db.ImportSources.RemoveRange(db.ImportSources);
        await db.SaveChangesAsync();
    }

    public static async Task SeedBaselineAsync(IDbContextFactory<BudgetDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();

        db.ImportSources.Add(new ImportSource { Id = SourceId, Name = "legacy", CreatedAt = Fixed });
        db.ImportSources.Add(new ImportSource { Id = SpareSourceId, Name = SpareSourceName, CreatedAt = Fixed });

        // Category.Type is MoneyMoney's raw type code; the app classifies income/expense per-project
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
        // negative = expense, positive = income (MoneyMoney convention).
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
