using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Single DbContext for everything: app-native rows (projects, budgets, assignments) and the
/// imported data. Imported rows are numbered in their owner's id space (see <see cref="RowIds"/>). Also
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
    private string[] ScopeVisibleOwnerIds => Scope?.VisibleOwnerIds ?? [];
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

    public DbSet<UserDataState> UserDataStates => Set<UserDataState>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<CategoryGroup> CategoryGroups => Set<CategoryGroup>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<AccountGroup> AccountGroups => Set<AccountGroup>();
    public DbSet<Holding> Holdings => Set<Holding>();
    public DbSet<PortfolioChartPoint> PortfolioChartPoints => Set<PortfolioChartPoint>();
    public DbSet<SecurityTaxonomy> SecurityTaxonomies => Set<SecurityTaxonomy>();
    public DbSet<PpSecurity> PpSecurities => Set<PpSecurity>();
    public DbSet<MarketQuote> MarketQuotes => Set<MarketQuote>();
    public DbSet<PpPortfolio> PpPortfolios => Set<PpPortfolio>();
    public DbSet<PpLot> PpLots => Set<PpLot>();
    public DbSet<PpPortfolioLink> PpPortfolioLinks => Set<PpPortfolioLink>();
    public DbSet<PpPortfolioSetting> PpPortfolioSettings => Set<PpPortfolioSetting>();
    public DbSet<SecurityTaxSetting> SecurityTaxSettings => Set<SecurityTaxSetting>();
    public DbSet<PpSecurityLink> PpSecurityLinks => Set<PpSecurityLink>();
    public DbSet<BankLogin> BankLogins => Set<BankLogin>();
    public DbSet<BankLoginAccount> BankLoginAccounts => Set<BankLoginAccount>();
    public DbSet<BankStatement> BankStatements => Set<BankStatement>();
    public DbSet<StatementRead> StatementReads => Set<StatementRead>();
    public DbSet<BankStatementAlias> BankStatementAliases => Set<BankStatementAlias>();
    public DbSet<BackgroundSyncRun> BackgroundSyncRuns => Set<BackgroundSyncRun>();
    public DbSet<FinTsBankSetting> FinTsBankSettings => Set<FinTsBankSetting>();
    public DbSet<UserProjectOrder> UserProjectOrders => Set<UserProjectOrder>();
    public DbSet<CategoryRule> CategoryRules => Set<CategoryRule>();
    public DbSet<CloudConnection> CloudConnections => Set<CloudConnection>();
    public DbSet<CloudFileLink> CloudFileLinks => Set<CloudFileLink>();
    public DbSet<RemovedImportItem> RemovedImportItems => Set<RemovedImportItem>();
    public DbSet<ScheduledPayment> ScheduledPayments => Set<ScheduledPayment>();
    public DbSet<CategorySuggestion> CategorySuggestions => Set<CategorySuggestion>();
    public DbSet<CancelledReservation> CancelledReservations => Set<CancelledReservation>();
    public DbSet<AmazonOrder> AmazonOrders => Set<AmazonOrder>();
    public DbSet<AmazonOrderItem> AmazonOrderItems => Set<AmazonOrderItem>();
    public DbSet<AmazonCharge> AmazonCharges => Set<AmazonCharge>();
    public DbSet<ReceiptInvoice> ReceiptInvoices => Set<ReceiptInvoice>();
    public DbSet<ReceiptItem> ReceiptItems => Set<ReceiptItem>();
    public DbSet<ItemCategoryMemory> ItemCategoryMemory => Set<ItemCategoryMemory>();
    public DbSet<ReceiptSettings> ReceiptSettings => Set<ReceiptSettings>();

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
        modelBuilder.Entity<ProjectAccountAssignment>()
            .Property(paa => paa.InCashflow)
            .HasDefaultValue(true)
            .ValueGeneratedNever(); // EF always writes the value; the default serves raw inserts.

        // Self-referencing parent/child. SetNull on parent delete so children survive as
        // top-level projects rather than getting cascade-removed with the parent.
        modelBuilder.Entity<Project>()
            .HasOne(p => p.Parent)
            .WithMany(p => p.Children)
            .HasForeignKey(p => p.ParentProjectId)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<Project>()
            .HasIndex(p => p.ParentProjectId);

        modelBuilder.Entity<UserDataState>().HasIndex(s => s.IdSpace).IsUnique();

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
        modelBuilder.Entity<Transaction>().Property(t => t.SplitCheck).HasDefaultValue(false);
        modelBuilder.Entity<CancelledReservation>().HasIndex(r => r.AccountId);
        modelBuilder.Entity<Account>().Property(a => a.InitialBalance).HasDefaultValue(0L);

        modelBuilder.Entity<Account>()
            .Property(a => a.Provider)
            .HasDefaultValue(AccountProviders.Manual);

        modelBuilder.Entity<Transaction>()
            .HasIndex(t => new { t.OwnerUserId, t.Timestamp });
        modelBuilder.Entity<Transaction>()
            .HasIndex(t => new { t.OwnerUserId, t.CategoryId, t.Timestamp });
        modelBuilder.Entity<Transaction>()
            .HasIndex(t => new { t.OwnerUserId, t.AccountId });

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
        modelBuilder.Entity<BankStatement>().HasIndex(s => new { s.OwnerUserId, s.Sha256 });
        modelBuilder.Entity<BankStatementAlias>().HasIndex(a => new { a.BankLoginId, a.Identifier }).IsUnique();
        modelBuilder.Entity<BackgroundSyncRun>().HasIndex(r => new { r.BankLoginId, r.StartedAt });
        modelBuilder.Entity<BackgroundSyncRun>().HasIndex(r => new { r.Blz, r.StartedAt });
        modelBuilder.Entity<BackgroundSyncRun>().Property(r => r.Result).HasConversion<int>();
        modelBuilder.Entity<BackgroundSyncRun>().HasOne<BankLogin>().WithMany().HasForeignKey(r => r.BankLoginId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<BankStatementAlias>().HasIndex(a => a.StatementId);
        modelBuilder.Entity<BankStatementAlias>().HasOne<BankStatement>().WithMany().HasForeignKey(a => a.StatementId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<UserProjectOrder>().HasIndex(o => new { o.UserId, o.ProjectId }).IsUnique();
        modelBuilder.Entity<UserProjectOrder>().HasIndex(o => o.ProjectId);
        modelBuilder.Entity<UserProjectOrder>().HasOne<Project>().WithMany().HasForeignKey(o => o.ProjectId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<UserProjectOrder>().HasOne<AppUser>().WithMany().HasForeignKey(o => o.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<StatementRead>().HasKey(r => new { r.UserId, r.StatementId });
        modelBuilder.Entity<MarketQuote>().HasKey(q => new { q.Symbol, q.Day });
        modelBuilder.Entity<StatementRead>().HasIndex(r => r.StatementId);
        modelBuilder.Entity<StatementRead>().HasOne<BankStatement>().WithMany().HasForeignKey(r => r.StatementId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<StatementRead>().HasOne<AppUser>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CloudConnection>().HasIndex(c => new { c.OwnerUserId, c.Provider }).IsUnique();
        modelBuilder.Entity<CloudFileLink>()
            .HasOne(f => f.Connection).WithMany(c => c.Files).HasForeignKey(f => f.ConnectionId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CloudFileLink>().HasIndex(f => new { f.ConnectionId, f.DriveId, f.ItemId }).IsUnique();
        modelBuilder.Entity<CloudFileLink>().HasIndex(f => f.OwnerUserId);
        modelBuilder.Entity<CategoryRule>().HasOne(r => r.Project).WithMany().HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CategoryRule>().HasIndex(r => new { r.ProjectId, r.SortOrder });

        modelBuilder.Entity<CategoryGroup>()
            .HasOne(cg => cg.ParentGroup)
            .WithMany(cg => cg.SubGroups)
            .HasForeignKey(cg => cg.ParentGroupId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CategoryGroup>()
            .HasIndex(cg => cg.OwnerUserId);

        modelBuilder.Entity<Category>()
            .HasOne(c => c.Group)
            .WithMany(cg => cg.Categories)
            .HasForeignKey(c => c.GroupId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Category>()
            .HasIndex(c => new { c.OwnerUserId, c.GroupId });

        modelBuilder.Entity<Account>()
            .HasIndex(a => a.OwnerUserId);

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
            .HasIndex(ag => ag.OwnerUserId);

        // Holdings belong to an account; Restrict mirrors how transactions hang off accounts so a
        // referenced account can't be deleted out from under its securities mid-import.
        modelBuilder.Entity<Holding>()
            .HasOne(s => s.Account)
            .WithMany(a => a.Holdings)
            .HasForeignKey(s => s.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Holding>()
            .HasIndex(s => new { s.OwnerUserId, s.AccountId });

        // The bank's standing orders and scheduled transfers go with their account.
        modelBuilder.Entity<ScheduledPayment>()
            .HasOne(p => p.Account)
            .WithMany()
            .HasForeignKey(p => p.AccountId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ScheduledPayment>().HasIndex(p => new { p.AccountId, p.NextDate });
        modelBuilder.Entity<ScheduledPayment>().Property(p => p.Kind).HasConversion<int>();
        modelBuilder.Entity<ScheduledPayment>().Property(p => p.IntervalUnit).HasConversion<int?>();
        modelBuilder.Entity<ScheduledPayment>().Property(p => p.Currency).HasDefaultValue("EUR");

        // portfolio_chart hangs off accounts only; securities_key deliberately has no FK (it can
        // reference historical positions the import skips — see PortfolioChartPoint remarks).
        modelBuilder.Entity<PortfolioChartPoint>()
            .HasOne(p => p.Account)
            .WithMany()
            .HasForeignKey(p => p.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PortfolioChartPoint>()
            .HasIndex(p => new { p.OwnerUserId, p.AccountId, p.Timestamp });

        // PortfolioPerformance taxonomy, joined to holdings by ISIN or PP security uuid. App-native (not namespaced);
        // scoped + indexed by source, and by source+taxonomy for the per-taxonomy lookups.
        modelBuilder.Entity<SecurityTaxonomy>()
            .HasIndex(t => new { t.OwnerUserId, t.Isin });
        modelBuilder.Entity<SecurityTaxonomy>()
            .HasIndex(t => new { t.OwnerUserId, t.TaxonomyId });
        modelBuilder.Entity<SecurityTaxonomy>()
            .Property(t => t.SecurityUuid).HasDefaultValue("");

        // PortfolioPerformance security catalogue. App-native (not namespaced); scoped by owner
        // and looked up by owner+ISIN to merge against held securities.
        modelBuilder.Entity<PpSecurity>()
            .HasIndex(s => new { s.OwnerUserId, s.Isin });

        // PortfolioPerformance depots + reconstructed FIFO lots. Scoped by owner; lots are read by
        // owner (then grouped by ISIN/depot in memory for the tax engine).
        modelBuilder.Entity<PpPortfolio>()
            .HasIndex(p => p.OwnerUserId);
        modelBuilder.Entity<PpLot>()
            .HasIndex(l => new { l.OwnerUserId, l.Isin });

        // Depot mapping is user config (not rebuilt on upload); one link per PP depot per source.
        modelBuilder.Entity<PpPortfolioLink>()
            .HasIndex(l => new { l.OwnerUserId, l.PpPortfolioUuid })
            .IsUnique();

        // How each PP portfolio shows as accounts (user config); one row per portfolio per source.
        modelBuilder.Entity<PpPortfolioSetting>()
            .HasIndex(s => new { s.OwnerUserId, s.PpPortfolioUuid })
            .IsUnique();

        // Per-instrument tax overrides (user config); one row per instrument (uuid) per source.
        modelBuilder.Entity<SecurityTaxSetting>()
            .HasIndex(s => new { s.OwnerUserId, s.SecurityUuid })
            .IsUnique();

        // Instrument mapping (user config); one link per ISIN-less PP instrument per source.
        modelBuilder.Entity<PpSecurityLink>()
            .HasIndex(l => new { l.OwnerUserId, l.PpSecurityUuid })
            .IsUnique();

        // Orders and charges of an order source (Amazon) go with their bank login.
        modelBuilder.Entity<AmazonOrder>().HasOne<BankLogin>().WithMany().HasForeignKey(o => o.BankLoginId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<AmazonOrder>().HasIndex(o => new { o.BankLoginId, o.OrderId }).IsUnique();
        modelBuilder.Entity<AmazonOrderItem>().HasOne(i => i.Order).WithMany(o => o.Items).HasForeignKey(i => i.AmazonOrderId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<AmazonCharge>().HasOne<BankLogin>().WithMany().HasForeignKey(c => c.BankLoginId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<AmazonCharge>().HasIndex(c => new { c.BankLoginId, c.ChargeKey }).IsUnique();
        modelBuilder.Entity<AmazonCharge>().HasIndex(c => c.TransactionId);
        modelBuilder.Entity<AmazonCharge>().Property(c => c.State).HasConversion<int>();

        // Receipts read from the owner's mailbox, the item category memory and the receipt settings.
        modelBuilder.Entity<ReceiptInvoice>().HasIndex(r => new { r.OwnerUserId, r.Shop, r.InvoiceNumber }).IsUnique();
        modelBuilder.Entity<ReceiptInvoice>().HasIndex(r => r.TransactionId);
        modelBuilder.Entity<ReceiptInvoice>().Property(r => r.State).HasConversion<int>();
        modelBuilder.Entity<ReceiptItem>().HasOne(i => i.Invoice).WithMany(r => r.Items).HasForeignKey(i => i.ReceiptInvoiceId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ReceiptItem>().Property(i => i.Kind).HasConversion<int>();
        modelBuilder.Entity<ItemCategoryMemory>().HasIndex(m => new { m.OwnerUserId, m.NormalizedName }).IsUnique();
        modelBuilder.Entity<ReceiptSettings>().HasIndex(s => s.OwnerUserId).IsUnique();

        ConfigureAccessFilters(modelBuilder);
    }
}
