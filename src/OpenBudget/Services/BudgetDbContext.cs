using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Single DbContext for everything: app-native rows (projects, budgets, assignments) and the
/// imported MoneyMoney data (mm_* tables). IDs from MoneyMoney are namespaced at import time
/// via <see cref="SourceScope.Namespace"/> so multiple sources coexist without collision.
/// </summary>
public class BudgetDbContext : DbContext
{
    public BudgetDbContext(DbContextOptions<BudgetDbContext> options)
        : base(options)
    {
    }

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectCategoryAssignment> ProjectCategoryAssignments => Set<ProjectCategoryAssignment>();
    public DbSet<ProjectTrackingItem> ProjectTrackingItems => Set<ProjectTrackingItem>();
    public DbSet<CategoryTypeAssignment> CategoryTypeAssignments => Set<CategoryTypeAssignment>();
    public DbSet<Budget> Budgets => Set<Budget>();
    public DbSet<MonthlyBudgetEntry> MonthlyBudgetEntries => Set<MonthlyBudgetEntry>();
    public DbSet<BudgetContributionSplit> BudgetContributionSplits => Set<BudgetContributionSplit>();
    public DbSet<ProjectAccountAssignment> ProjectAccountAssignments => Set<ProjectAccountAssignment>();

    public DbSet<ImportSource> ImportSources => Set<ImportSource>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<CategoryGroup> CategoryGroups => Set<CategoryGroup>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<AccountGroup> AccountGroups => Set<AccountGroup>();
    public DbSet<Holding> Holdings => Set<Holding>();
    public DbSet<PortfolioChartPoint> PortfolioChartPoints => Set<PortfolioChartPoint>();
    public DbSet<SecurityTaxonomy> SecurityTaxonomies => Set<SecurityTaxonomy>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ProjectCategoryAssignment>()
            .HasOne(pca => pca.Project)
            .WithMany(p => p.CategoryAssignments)
            .HasForeignKey(pca => pca.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ProjectTrackingItem>()
            .HasOne(pti => pti.Project)
            .WithMany(p => p.TrackingItems)
            .HasForeignKey(pti => pti.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<CategoryTypeAssignment>()
            .HasOne(cta => cta.Project)
            .WithMany(p => p.CategoryTypeAssignments)
            .HasForeignKey(cta => cta.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<CategoryTypeAssignment>()
            .HasIndex(cta => new { cta.ProjectId, cta.CategoryId })
            .IsUnique();

        modelBuilder.Entity<Budget>()
            .HasOne(b => b.Project)
            .WithMany()
            .HasForeignKey(b => b.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // Optimistic-concurrency token: EF includes its original value in the WHERE clause of the
        // UPDATE, so a save whose loaded Version no longer matches the row touches 0 rows and
        // throws DbUpdateConcurrencyException. Incremented manually in SaveBudgetAsync (SQLite has
        // no auto rowversion).
        modelBuilder.Entity<Budget>()
            .Property(b => b.Version)
            .IsConcurrencyToken();

        modelBuilder.Entity<MonthlyBudgetEntry>()
            .HasOne(mbe => mbe.Budget)
            .WithMany(b => b.MonthlyEntries)
            .HasForeignKey(mbe => mbe.BudgetId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<BudgetContributionSplit>()
            .HasOne(bcs => bcs.Budget)
            .WithMany()
            .HasForeignKey(bcs => bcs.BudgetId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<BudgetContributionSplit>()
            .HasIndex(bcs => new { bcs.BudgetId, bcs.CategoryId, bcs.ItemType })
            .IsUnique();

        modelBuilder.Entity<ProjectAccountAssignment>()
            .HasOne(paa => paa.Project)
            .WithMany(p => p.AccountAssignments)
            .HasForeignKey(paa => paa.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ProjectAccountAssignment>()
            .HasIndex(paa => new { paa.ProjectId, paa.AccountId })
            .IsUnique();

        // Source binding: every Project belongs to exactly one ImportSource. OnDelete is
        // Restrict so a source with attached projects can't be silently deleted; the UI surfaces
        // the blockers and asks the user to clean up first.
        modelBuilder.Entity<Project>()
            .HasOne(p => p.Source)
            .WithMany(s => s.Projects)
            .HasForeignKey(p => p.SourceId)
            .OnDelete(DeleteBehavior.Restrict);

        // Self-referencing parent/child. SetNull on parent delete so children survive as
        // top-level projects rather than getting cascade-removed with the parent.
        modelBuilder.Entity<Project>()
            .HasOne(p => p.Parent)
            .WithMany(p => p.Children)
            .HasForeignKey(p => p.ParentProjectId)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<Project>()
            .HasIndex(p => p.ParentProjectId);

        modelBuilder.Entity<ImportSource>()
            .HasIndex(s => s.Name)
            .IsUnique();

        // Seed source 1 so identity namespacing is always available; the upload endpoint
        // creates additional sources on demand. "Legacy" in the name predates multi-source
        // support and is kept only because changing it would churn schema migrations.
        modelBuilder.Entity<ImportSource>().HasData(new ImportSource
        {
            Id = SourceScope.LegacySourceId,
            Name = SourceScope.LegacySourceName,
            CreatedAt = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });

        // MM-side mappings: the column names match the MoneyMoney native schema for the columns
        // we copy verbatim during import (so the entity classes can stay close to MoneyMoney's
        // shape), but the table names are ours.
        modelBuilder.Entity<Transaction>()
            .HasOne(t => t.Category)
            .WithMany(c => c.Transactions)
            .HasForeignKey(t => t.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Transaction>()
            .HasOne(t => t.Account)
            .WithMany(a => a.Transactions)
            .HasForeignKey(t => t.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Transaction>()
            .HasIndex(t => new { t.SourceId, t.Timestamp });
        modelBuilder.Entity<Transaction>()
            .HasIndex(t => new { t.SourceId, t.CategoryId, t.Timestamp });
        modelBuilder.Entity<Transaction>()
            .HasIndex(t => new { t.SourceId, t.AccountId });

        modelBuilder.Entity<CategoryGroup>()
            .HasOne(cg => cg.ParentGroup)
            .WithMany(cg => cg.SubGroups)
            .HasForeignKey(cg => cg.ParentGroupId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CategoryGroup>()
            .HasIndex(cg => cg.SourceId);

        modelBuilder.Entity<Category>()
            .HasOne(c => c.Group)
            .WithMany(cg => cg.Categories)
            .HasForeignKey(c => c.GroupId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Category>()
            .HasIndex(c => new { c.SourceId, c.GroupId });

        modelBuilder.Entity<Account>()
            .HasIndex(a => a.SourceId);

        modelBuilder.Entity<Account>()
            .HasOne(a => a.Group)
            .WithMany(g => g.Accounts)
            .HasForeignKey(a => a.GroupId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AccountGroup>()
            .HasOne(ag => ag.ParentGroup)
            .WithMany(ag => ag.SubGroups)
            .HasForeignKey(ag => ag.ParentGroupId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AccountGroup>()
            .HasIndex(ag => ag.SourceId);

        // Holdings belong to an account; Restrict mirrors how transactions hang off accounts so a
        // referenced account can't be deleted out from under its securities mid-import.
        modelBuilder.Entity<Holding>()
            .HasOne(s => s.Account)
            .WithMany(a => a.Holdings)
            .HasForeignKey(s => s.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Holding>()
            .HasIndex(s => new { s.SourceId, s.AccountId });

        // portfolio_chart hangs off accounts only; securities_key deliberately has no FK (it can
        // reference historical positions the import skips — see PortfolioChartPoint remarks).
        modelBuilder.Entity<PortfolioChartPoint>()
            .HasOne(p => p.Account)
            .WithMany()
            .HasForeignKey(p => p.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PortfolioChartPoint>()
            .HasIndex(p => new { p.SourceId, p.AccountId, p.Timestamp });

        // PortfolioPerformance taxonomy, joined to holdings by ISIN. App-native (not namespaced);
        // scoped + indexed by source, and by source+taxonomy for the per-taxonomy lookups.
        modelBuilder.Entity<SecurityTaxonomy>()
            .HasIndex(t => new { t.SourceId, t.Isin });
        modelBuilder.Entity<SecurityTaxonomy>()
            .HasIndex(t => new { t.SourceId, t.TaxonomyId });
    }
}
