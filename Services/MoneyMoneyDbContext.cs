using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Database context for read-only access to MoneyMoney encrypted database
/// </summary>
public class MoneyMoneyDbContext : DbContext
{
    public MoneyMoneyDbContext(DbContextOptions<MoneyMoneyDbContext> options)
        : base(options)
    {
    }

    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<CategoryGroup> CategoryGroups => Set<CategoryGroup>();
    public DbSet<Account> Accounts => Set<Account>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configure relationships
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

        // Configure category group hierarchy (self-referential)
        modelBuilder.Entity<CategoryGroup>()
            .HasOne(cg => cg.ParentGroup)
            .WithMany(cg => cg.SubGroups)
            .HasForeignKey(cg => cg.ParentGroupId)
            .OnDelete(DeleteBehavior.Restrict);

        // Configure category to category group relationship
        modelBuilder.Entity<Category>()
            .HasOne(c => c.Group)
            .WithMany(cg => cg.Categories)
            .HasForeignKey(c => c.GroupId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
