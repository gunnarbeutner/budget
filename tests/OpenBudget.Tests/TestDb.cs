using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Tests;

/// <summary>
/// Spins up a fresh in-memory SQLite DB per test, wires the schema via Database.EnsureCreated, gives
/// <see cref="DefaultOwner"/> id space 1 (ids equal rowids), and constructs the live service graph the
/// dashboard uses. Each instance is its own isolated world; tests don't need to clean up
/// after themselves.
/// </summary>
internal sealed class TestDb : IAsyncDisposable
{
    /// <summary>Owner of the rows the seed helpers add unless told otherwise (user "default", id space 1).</summary>
    public const string DefaultOwner = "u-default";

    public SqliteConnection Connection { get; }
    public IDbContextFactory<BudgetDbContext> Factory { get; }
    public AccountService Accounts { get; }
    public BudgetDbContext Db { get; }
    public ChangeNotificationService Notifier { get; }
    public CategoryHierarchyService Hierarchy { get; }
    public ProjectService Projects { get; }
    public BudgetEntryService Entries { get; }
    public BudgetService Spending { get; }
    public BudgetPredictionService Predictions { get; }
    public TransactionQueryService TransactionQueries { get; }
    public BrowseService Browse { get; }
    public CashflowProjectionService Cashflow { get; }
    public PortfolioService Portfolio { get; }
    public DepotMappingService DepotMapping { get; }
    public SecurityTaxService SecurityTax { get; }
    public InstrumentMappingService InstrumentMapping { get; }
    public SystemDbContextFactory System { get; }

    public TestDb()
    {
        Connection = new SqliteConnection("DataSource=:memory:");
        Connection.Open();
        var options = new DbContextOptionsBuilder<BudgetDbContext>()
            .UseSqlite(Connection)
            .Options;
        Db = new BudgetDbContext(options);
        Db.Database.EnsureCreated();
        Db.Users.Add(new AppUser { Id = DefaultOwner, UserName = "default", NormalizedUserName = "DEFAULT" });
        Db.UserDataStates.Add(new UserDataState { UserId = DefaultOwner, IdSpace = 1 });
        Db.SaveChanges();

        // Production services take an IDbContextFactory (one short-lived context per operation).
        // In tests every context shares this single open :memory: connection, so they all see the
        // same database — disposing a per-operation context doesn't drop the schema or data.
        var factory = new SharedConnectionDbContextFactory(options);
        Factory = factory;
        System = new SystemDbContextFactory(options);
        Accounts = new AccountService(factory);
        Notifier = new ChangeNotificationService();
        // Stand-in for the per-circuit origin the DI container would supply in the live app.
        var origin = new ChangeOrigin();
        Hierarchy = new CategoryHierarchyService(factory, Notifier);
        Projects = new ProjectService(factory, Notifier, origin);
        Entries = new BudgetEntryService(factory, Hierarchy, Notifier, origin);
        Spending = new BudgetService(factory, Hierarchy, Projects, Entries, Notifier, origin);
        Predictions = new BudgetPredictionService(factory, Hierarchy);
        TransactionQueries = new TransactionQueryService(factory);
        Browse = new BrowseService(factory, Hierarchy, Projects, Entries, Spending);
        Cashflow = new CashflowProjectionService(factory, Hierarchy, Projects);
        Portfolio = new PortfolioService(factory, Projects);
        DepotMapping = new DepotMappingService(factory);
        SecurityTax = new SecurityTaxService(factory);
        InstrumentMapping = new InstrumentMappingService(factory);
    }

    /// <summary>
    /// The service graph as <paramref name="userId"/> sees it: every context is user-scoped (global
    /// query filters + write guard), exactly as in the running app. Null = logged out.
    /// </summary>
    public UserView ForUser(string? userId) => new(this, userId);

    /// <summary>Seed a login (identity row only; no password).</summary>
    public AppUser AddUser(string id, string name, bool admin = false)
    {
        var u = new AppUser { Id = id, UserName = name, NormalizedUserName = name.ToUpperInvariant(), IsAdmin = admin };
        Db.Users.Add(u);
        return u;
    }

    /// <summary>
    /// An <see cref="IDbContextFactory{TContext}"/> that hands out fresh contexts over a single
    /// shared connection. With an in-memory SQLite database the data lives as long as the
    /// connection is open, so every context created here reads/writes the same store.
    /// </summary>
    private sealed class SharedConnectionDbContextFactory : IDbContextFactory<BudgetDbContext>
    {
        private readonly DbContextOptions<BudgetDbContext> _options;
        public SharedConnectionDbContextFactory(DbContextOptions<BudgetDbContext> options) => _options = options;
        public BudgetDbContext CreateDbContext() => new(_options);
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        Connection.Dispose();
    }

    // ---- Seed helpers ----

    /// <summary>Add a category group with optional parent and an explicit Id.</summary>
    public CategoryGroup AddGroup(long id, long? parent = null, string name = "G", string ownerId = DefaultOwner)
    {
        var g = new CategoryGroup { Id = id, OwnerUserId = ownerId, Name = $"{name}{id}", ParentGroupId = parent };
        Db.CategoryGroups.Add(g);
        return g;
    }

    /// <summary>Add a leaf category. Type = 1 (default expense) unless overridden.</summary>
    public Category AddLeaf(long id, long parent, string name = "C", long? type = 1, string ownerId = DefaultOwner)
    {
        var c = new Category { Id = id, OwnerUserId = ownerId, Name = $"{name}{id}", GroupId = parent, Type = type };
        Db.Categories.Add(c);
        return c;
    }

    public Project AddProject(int id, string name = "P", string ownerId = DefaultOwner)
    {
        var p = new Project
        {
            Id = id,
            Name = name,
            OwnerUserId = ownerId,
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
    /// Negative cents = expense, positive cents = income — same convention as the source app.
    /// </summary>
    public void AddTransaction(
        long id, long categoryId, long amountCents, DateTime date, string ownerId = DefaultOwner,
        long? accountId = null, string? name = null, string? comment = null, long? splitted = null)
    {
        Db.Transactions.Add(new Transaction
        {
            Id = id,
            OwnerUserId = ownerId,
            CategoryId = categoryId,
            AccountId = accountId,
            Amount = amountCents,
            Timestamp = ((DateTimeOffset)DateTime.SpecifyKind(date, DateTimeKind.Utc)).ToUnixTimeSeconds(),
            Name = name,
            Comment = comment,
            Splitted = splitted,
        });
    }

    /// <summary>Add a imported account with a known Id + name.</summary>
    public Account AddAccount(long id, string name = "Acct", string ownerId = DefaultOwner)
    {
        var a = new Account { Id = id, OwnerUserId = ownerId, Name = name };
        Db.Accounts.Add(a);
        return a;
    }
}

/// <summary>Resolves a fixed user id, standing in for the Blazor auth state in tests.</summary>
internal sealed class FixedCurrentUser : ICurrentUser
{
    private readonly string? _userId;

    public FixedCurrentUser(string? userId) => _userId = userId;

    public ValueTask<string?> GetUserIdAsync() => ValueTask.FromResult(_userId);
}

/// <summary>The app's service graph wired over user-scoped contexts for one user.</summary>
internal sealed class UserView
{
    public UserView(TestDb t, string? userId)
    {
        Notifier = t.Notifier;
        Access = new AccessScopeProvider(t.System, new FixedCurrentUser(userId), Notifier);
        Factory = new ScopedDbContextFactory(t.System, Access);
        var origin = new ChangeOrigin();
        Hierarchy = new CategoryHierarchyService(Factory, Notifier);
        Projects = new ProjectService(Factory, Notifier, origin);
        Entries = new BudgetEntryService(Factory, Hierarchy, Notifier, origin);
        Spending = new BudgetService(Factory, Hierarchy, Projects, Entries, Notifier, origin);
        TransactionQueries = new TransactionQueryService(Factory);
        Browse = new BrowseService(Factory, Hierarchy, Projects, Entries, Spending, Access);
        Cashflow = new CashflowProjectionService(Factory, Hierarchy, Projects);
        Portfolio = new PortfolioService(Factory, Projects);
        Freshness = new FreshnessService(Factory);
        DepotMapping = new DepotMappingService(Factory);
        SecurityTax = new SecurityTaxService(Factory);
        InstrumentMapping = new InstrumentMappingService(Factory);
        Accounts = new AccountService(Factory);
        Sharing = new SharingService(Factory, t.System, Notifier, origin);
    }

    public ChangeNotificationService Notifier { get; }
    public AccessScopeProvider Access { get; }
    public IDbContextFactory<BudgetDbContext> Factory { get; }
    public CategoryHierarchyService Hierarchy { get; }
    public ProjectService Projects { get; }
    public BudgetEntryService Entries { get; }
    public BudgetService Spending { get; }
    public TransactionQueryService TransactionQueries { get; }
    public BrowseService Browse { get; }
    public CashflowProjectionService Cashflow { get; }
    public PortfolioService Portfolio { get; }
    public FreshnessService Freshness { get; }
    public DepotMappingService DepotMapping { get; }
    public SecurityTaxService SecurityTax { get; }
    public InstrumentMappingService InstrumentMapping { get; }
    public AccountService Accounts { get; }
    public SharingService Sharing { get; }
}

internal static class TestDbExtensions
{
    /// <summary>Gives <paramref name="userId"/> the id space <paramref name="idSpace"/> (rows of theirs are numbered in it).</summary>
    public static void AddIdSpace(this TestDb t, string userId, int idSpace) =>
        t.Db.UserDataStates.Add(new UserDataState { UserId = userId, IdSpace = idSpace });
}
