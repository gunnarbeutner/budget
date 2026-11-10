using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Single DbContext for everything: app-native rows (projects, budgets, assignments) and the
/// imported data. IDs from the source app are namespaced at import time
/// via <see cref="SourceScope.Namespace"/> so multiple sources coexist without collision. Also
/// hosts the ASP.NET Core Identity tables (users, passkeys) so there is a single migration history.
///
/// ACCESS CONTROL lives here. When <see cref="Scope"/> is set (every context the application
/// services get — see <see cref="ScopedDbContextFactory"/>), global query filters restrict every
/// read to what the user may see and <see cref="SaveChangesAsync(bool, CancellationToken)"/> refuses
/// writes the user's role doesn't allow. A null scope means a trusted system context (importers,
/// CLI, migrations, tests) and is unrestricted.
/// </summary>
public partial class BudgetDbContext : IdentityUserContext<AppUser>
{
    public BudgetDbContext(DbContextOptions<BudgetDbContext> options)
        : base(options)
    {
    }

    /// <summary>The user this context acts for; null = unrestricted system context.</summary>
    public AccessScope? Scope { get; set; }

    // Filter inputs. EF turns these context members into per-query parameters, so each context
    // instance filters by its own scope. They are never null (a null scope is "unrestricted").
    private bool ScopeUnrestricted => Scope == null;
    private int[] ScopeVisibleProjectIds => Scope?.VisibleProjectIds ?? [];
    private int[] ScopeOwnedProjectIds => Scope?.OwnedProjectIds ?? [];
    private int[] ScopeOwnedSourceIds => Scope?.OwnedSourceIds ?? [];
    private int[] ScopeVisibleSourceIds => Scope?.VisibleSourceIds ?? [];
    private long[] ScopeSharedAccountIds => Scope?.SharedAccountIds ?? [];
    private string ScopeUserId => Scope?.UserId ?? string.Empty;

    public DbSet<ProjectShare> ProjectShares => Set<ProjectShare>();
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
    public DbSet<PpSecurity> PpSecurities => Set<PpSecurity>();
    public DbSet<PpPortfolio> PpPortfolios => Set<PpPortfolio>();
    public DbSet<PpLot> PpLots => Set<PpLot>();
    public DbSet<PpPortfolioLink> PpPortfolioLinks => Set<PpPortfolioLink>();
    public DbSet<SecurityTaxSetting> SecurityTaxSettings => Set<SecurityTaxSetting>();
    public DbSet<PpSecurityLink> PpSecurityLinks => Set<PpSecurityLink>();
    public DbSet<BankLogin> BankLogins => Set<BankLogin>();
    public DbSet<BankLoginAccount> BankLoginAccounts => Set<BankLoginAccount>();
    public DbSet<BankStatement> BankStatements => Set<BankStatement>();
    public DbSet<StatementRead> StatementReads => Set<StatementRead>();
    public DbSet<CategoryRule> CategoryRules => Set<CategoryRule>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        var modelBuilder = builder;
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

        // Imported-data mappings: the column names match the source app native schema for the columns
        // we copy verbatim during import (so the entity classes can stay close to the source's
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

        // Sentinel = the default (true) so an explicit Booked=false is inserted rather than being
        // mistaken for "unset" and replaced by the column default.
        modelBuilder.Entity<Transaction>()
            .Property(t => t.Booked)
            .HasDefaultValue(true)
            .HasSentinel(true);

        modelBuilder.Entity<Transaction>()
            .Property(t => t.CategorySetBy)
            .HasDefaultValue(TransactionCategorySource.Import);

        // Raw-SQL inserts (the MoneyMoney bulk import) rely on database-side defaults for these.
        modelBuilder.Entity<Transaction>().Property(t => t.Checked).HasDefaultValue(false);
        modelBuilder.Entity<Transaction>().Property(t => t.IsNew).HasDefaultValue(false);

        // Same trick as Booked: the default (true) is the sentinel so an explicit false is inserted.
        modelBuilder.Entity<Category>()
            .Property(c => c.ShowInCharts)
            .HasDefaultValue(true)
            .HasSentinel(true);

        modelBuilder.Entity<Account>()
            .Property(a => a.Provider)
            .HasDefaultValue(AccountProviders.MoneyMoney);

        modelBuilder.Entity<Transaction>()
            .HasIndex(t => new { t.SourceId, t.Timestamp });
        modelBuilder.Entity<Transaction>()
            .HasIndex(t => new { t.SourceId, t.CategoryId, t.Timestamp });
        modelBuilder.Entity<Transaction>()
            .HasIndex(t => new { t.SourceId, t.AccountId });

        // Bank connectors (see BudgetDbContext.Access for who can see them).
        modelBuilder.Entity<BankLogin>().HasIndex(b => b.OwnerUserId);
        modelBuilder.Entity<BankLogin>().Property(b => b.RefreshInterval).HasConversion<int>();
        modelBuilder.Entity<BankLoginAccount>()
            .HasOne(a => a.BankLogin).WithMany(b => b.Accounts).HasForeignKey(a => a.BankLoginId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<BankLoginAccount>().HasIndex(a => new { a.BankLoginId, a.RemoteId }).IsUnique();
        modelBuilder.Entity<BankLoginAccount>().Property(a => a.Type).HasConversion<int>();
        modelBuilder.Entity<BankLoginAccount>().Property(a => a.StatementsInterval).HasConversion<int>();
        // Statements deliberately have no foreign key to the login: they follow the visibility of
        // their account (viewers of a shared account may read them) while the login stays private.
        modelBuilder.Entity<BankStatement>().HasIndex(s => new { s.BankLoginId, s.Identifier }).IsUnique();
        modelBuilder.Entity<BankStatement>().HasIndex(s => s.AccountId);
        modelBuilder.Entity<StatementRead>().HasKey(r => new { r.UserId, r.StatementId });
        modelBuilder.Entity<StatementRead>().HasIndex(r => r.StatementId);
        modelBuilder.Entity<StatementRead>().HasOne<BankStatement>().WithMany().HasForeignKey(r => r.StatementId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<StatementRead>().HasOne<AppUser>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CategoryRule>().HasOne(r => r.Project).WithMany().HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CategoryRule>().HasIndex(r => new { r.ProjectId, r.SortOrder });
        modelBuilder.Entity<CategoryRule>().Property(r => r.AmountSign).HasConversion<int>();

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

        // PortfolioPerformance security catalogue. App-native (not namespaced); scoped by source
        // and looked up by source+ISIN to merge against held securities.
        modelBuilder.Entity<PpSecurity>()
            .HasIndex(s => new { s.SourceId, s.Isin });

        // PortfolioPerformance depots + reconstructed FIFO lots. Scoped by source; lots are read by
        // source (then grouped by ISIN/depot in memory for the tax engine).
        modelBuilder.Entity<PpPortfolio>()
            .HasIndex(p => p.SourceId);
        modelBuilder.Entity<PpLot>()
            .HasIndex(l => new { l.SourceId, l.Isin });

        // Depot mapping is user config (not rebuilt on upload); one link per PP depot per source.
        modelBuilder.Entity<PpPortfolioLink>()
            .HasIndex(l => new { l.SourceId, l.PpPortfolioUuid })
            .IsUnique();

        // Per-instrument tax overrides (user config); one row per instrument (uuid) per source.
        modelBuilder.Entity<SecurityTaxSetting>()
            .HasIndex(s => new { s.SourceId, s.SecurityUuid })
            .IsUnique();

        // Instrument mapping (user config); one link per ISIN-less PP instrument per source.
        modelBuilder.Entity<PpSecurityLink>()
            .HasIndex(l => new { l.SourceId, l.PpSecurityUuid })
            .IsUnique();

        ConfigureAccessFilters(modelBuilder);
    }
}
