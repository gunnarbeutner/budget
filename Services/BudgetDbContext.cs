using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Database context for our budget tracking data
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configure relationships for Projects
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

        // Configure unique constraint: one assignment per category per project
        modelBuilder.Entity<CategoryTypeAssignment>()
            .HasIndex(cta => new { cta.ProjectId, cta.CategoryId })
            .IsUnique();

        // Configure relationships for Budgets
        modelBuilder.Entity<Budget>()
            .HasOne(b => b.Project)
            .WithMany()
            .HasForeignKey(b => b.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

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

        // Note: We don't create a FK to Category since it's in a different database
        // We'll handle the relationship in code
    }
}
