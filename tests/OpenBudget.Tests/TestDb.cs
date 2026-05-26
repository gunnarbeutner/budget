using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Tests;

/// <summary>
/// Spins up a fresh in-memory SQLite DB per test, wires the schema via Database.EnsureCreated
/// (which honors the HasData seed for source 1), and constructs the live service graph the
/// dashboard uses. Each instance is its own isolated world; tests don't need to clean up
/// after themselves.
/// </summary>
internal sealed class TestDb : IAsyncDisposable
{
    public SqliteConnection Connection { get; }
    public BudgetDbContext Db { get; }
    public CategoryHierarchyService Hierarchy { get; }
    public ProjectService Projects { get; }
    public BudgetEntryService Entries { get; }
    public BudgetService Spending { get; }
    public TransactionQueryService TransactionQueries { get; }
    public BrowseService Browse { get; }

    public TestDb()
    {
        Connection = new SqliteConnection("DataSource=:memory:");
        Connection.Open();
        var options = new DbContextOptionsBuilder<BudgetDbContext>()
            .UseSqlite(Connection)
            .Options;
        Db = new BudgetDbContext(options);
        Db.Database.EnsureCreated();
        Hierarchy = new CategoryHierarchyService(Db);
        Projects = new ProjectService(Db);
        Entries = new BudgetEntryService(Db, Hierarchy);
        Spending = new BudgetService(Db, Hierarchy, Projects, Entries);
        TransactionQueries = new TransactionQueryService(Db);
        Browse = new BrowseService(Db, Hierarchy, Projects, Entries, Spending);
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        Connection.Dispose();
    }

    // ---- Seed helpers ----

    /// <summary>Add a category group with optional parent and an explicit Id.</summary>
    public CategoryGroup AddGroup(long id, long? parent = null, string name = "G", int sourceId = 1)
    {
        var g = new CategoryGroup { Id = id, SourceId = sourceId, Name = $"{name}{id}", ParentGroupId = parent };
        Db.CategoryGroups.Add(g);
        return g;
    }

    /// <summary>Add a leaf category. Type = 1 (default expense) unless overridden.</summary>
    public Category AddLeaf(long id, long parent, string name = "C", long? type = 1, int sourceId = 1)
    {
        var c = new Category { Id = id, SourceId = sourceId, Name = $"{name}{id}", GroupId = parent, Type = type };
        Db.Categories.Add(c);
        return c;
    }

    public Project AddProject(int id, string name = "P", int sourceId = 1)
    {
        var p = new Project
        {
            Id = id,
            Name = name,
            SourceId = sourceId,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        Db.Projects.Add(p);
        return p;
    }

    public void TrackGroup(int projectId, long groupId)
    {
        Db.ProjectTrackingItems.Add(new ProjectTrackingItem
        {
            ProjectId = projectId,
            CategoryGroupId = groupId,
            CategoryId = null,
        });
    }

    public void TrackCategory(int projectId, long categoryId)
    {
        Db.ProjectTrackingItems.Add(new ProjectTrackingItem
        {
            ProjectId = projectId,
            CategoryGroupId = null,
            CategoryId = categoryId,
        });
    }

    /// <summary>
    /// Assign a CategoryGroup to a project — the "assigned group" anchor the browse tree
    /// roots itself on. The legacy table is named ProjectCategoryAssignments and its column
    /// is "CategoryId" but the values it stores are CategoryGroup IDs.
    /// </summary>
    public void AssignGroup(int projectId, long categoryGroupId)
    {
        Db.ProjectCategoryAssignments.Add(new ProjectCategoryAssignment
        {
            ProjectId = projectId,
            CategoryId = categoryGroupId,
        });
    }

    /// <summary>
    /// Create a budget for a project with a uniform monthly amount per (Id, ItemType) entry.
    /// </summary>
    public Budget AddBudget(int projectId, int year, params (long Id, CategoryItemType ItemType, decimal MonthlyAmount)[] entries)
    {
        var budget = new Budget
        {
            ProjectId = projectId,
            Year = year,
            Name = $"{year}",
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            IsActive = true,
        };
        Db.Budgets.Add(budget);
        Db.SaveChanges(); // need budget.Id for FK

        foreach (var (id, type, amt) in entries)
        {
            var entry = new MonthlyBudgetEntry
            {
                BudgetId = budget.Id,
                CategoryId = id,
                ItemType = type,
            };
            for (int m = 1; m <= 12; m++) entry.SetMonthAmount(m, amt);
            Db.MonthlyBudgetEntries.Add(entry);
        }
        return budget;
    }

    /// <summary>
    /// Add a transaction. Amount is in cents (matches the on-disk column type post-migration).
    /// Negative cents = expense, positive cents = income — same convention as MoneyMoney.
    /// </summary>
    public void AddTransaction(
        long id, long categoryId, long amountCents, DateTime date, int sourceId = 1,
        long? accountId = null, string? name = null, string? comment = null, long? splitted = null)
    {
        Db.Transactions.Add(new Transaction
        {
            Id = id,
            SourceId = sourceId,
            CategoryId = categoryId,
            AccountId = accountId,
            Amount = amountCents,
            Timestamp = ((DateTimeOffset)DateTime.SpecifyKind(date, DateTimeKind.Utc)).ToUnixTimeSeconds(),
            Name = name,
            Comment = comment,
            Splitted = splitted,
        });
    }

    /// <summary>Add a MoneyMoney account with a known Id + name.</summary>
    public Account AddAccount(long id, string name = "Acct", int sourceId = 1)
    {
        var a = new Account { Id = id, SourceId = sourceId, Name = name };
        Db.Accounts.Add(a);
        return a;
    }
}
