using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

public partial class BudgetDbContext
{
    /// <summary>
    /// Read-side access control: one global filter per scoped entity, so every query — including
    /// navigation includes and aggregates — only sees what <see cref="Scope"/> allows.
    /// </summary>
    private void ConfigureAccessFilters(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProjectShare>()
            .HasOne(s => s.Project).WithMany(p => p.Shares).HasForeignKey(s => s.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ProjectShare>()
            .HasOne(s => s.User).WithMany().HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ProjectShare>().HasIndex(s => new { s.ProjectId, s.UserId }).IsUnique();
        modelBuilder.Entity<ProjectShare>().Property(s => s.Role).HasConversion<int>();
        modelBuilder.Entity<Project>().HasIndex(p => p.OwnerUserId);
        modelBuilder.Entity<ImportSource>().HasIndex(s => s.OwnerUserId);

        // Projects and everything hanging off them.
        modelBuilder.Entity<Project>().HasQueryFilter(p => ScopeUnrestricted || ScopeVisibleProjectIds.Contains(p.Id));
        modelBuilder.Entity<ProjectShare>().HasQueryFilter(s =>
            ScopeUnrestricted || ScopeOwnedProjectIds.Contains(s.ProjectId) || s.UserId == ScopeUserId);
        modelBuilder.Entity<ProjectCategoryAssignment>().HasQueryFilter(e => ScopeUnrestricted || ScopeVisibleProjectIds.Contains(e.ProjectId));
        modelBuilder.Entity<ProjectTrackingItem>().HasQueryFilter(e => ScopeUnrestricted || ScopeVisibleProjectIds.Contains(e.ProjectId));
        modelBuilder.Entity<CategoryTypeAssignment>().HasQueryFilter(e => ScopeUnrestricted || ScopeVisibleProjectIds.Contains(e.ProjectId));
        modelBuilder.Entity<ProjectAccountAssignment>().HasQueryFilter(e => ScopeUnrestricted || ScopeVisibleProjectIds.Contains(e.ProjectId));
        modelBuilder.Entity<Budget>().HasQueryFilter(e => ScopeUnrestricted || ScopeVisibleProjectIds.Contains(e.ProjectId));
        modelBuilder.Entity<MonthlyBudgetEntry>().HasQueryFilter(e => ScopeUnrestricted || ScopeVisibleProjectIds.Contains(e.Budget.ProjectId));
        modelBuilder.Entity<BudgetContributionSplit>().HasQueryFilter(e => ScopeUnrestricted || ScopeVisibleProjectIds.Contains(e.Budget.ProjectId));

        // Sources, and the category tree (names/hierarchy) of every source that has a visible project.
        modelBuilder.Entity<ImportSource>().HasQueryFilter(s => ScopeUnrestricted || ScopeVisibleSourceIds.Contains(s.Id));
        modelBuilder.Entity<Category>().HasQueryFilter(e => ScopeUnrestricted || ScopeVisibleSourceIds.Contains(e.SourceId));
        modelBuilder.Entity<CategoryGroup>().HasQueryFilter(e => ScopeUnrestricted || ScopeVisibleSourceIds.Contains(e.SourceId));
        modelBuilder.Entity<AccountGroup>().HasQueryFilter(e => ScopeUnrestricted || ScopeVisibleSourceIds.Contains(e.SourceId));

        // Accounts and what hangs off them: all of an owned source, otherwise ONLY the accounts
        // assigned to a project shared with the user.
        modelBuilder.Entity<Account>().HasQueryFilter(a =>
            ScopeUnrestricted || ScopeOwnedSourceIds.Contains(a.SourceId) || ScopeSharedAccountIds.Contains(a.Id));
        modelBuilder.Entity<Transaction>().HasQueryFilter(t =>
            ScopeUnrestricted || ScopeOwnedSourceIds.Contains(t.SourceId)
            || (t.AccountId.HasValue && ScopeSharedAccountIds.Contains(t.AccountId.Value)));
        modelBuilder.Entity<Holding>().HasQueryFilter(h =>
            ScopeUnrestricted || ScopeOwnedSourceIds.Contains(h.SourceId) || ScopeSharedAccountIds.Contains(h.AccountId));
        modelBuilder.Entity<PortfolioChartPoint>().HasQueryFilter(p =>
            ScopeUnrestricted || ScopeOwnedSourceIds.Contains(p.SourceId) || ScopeSharedAccountIds.Contains(p.AccountId));
        modelBuilder.Entity<PpPortfolioLink>().HasQueryFilter(l =>
            ScopeUnrestricted || ScopeOwnedSourceIds.Contains(l.SourceId) || ScopeSharedAccountIds.Contains(l.AccountId));

        // Bank logins are strictly personal. Statements follow their account (shared viewers may read them); account-less ones are owner-only.
        modelBuilder.Entity<BankLogin>().HasQueryFilter(b => ScopeUnrestricted || b.OwnerUserId == ScopeUserId);
        modelBuilder.Entity<BankLoginAccount>().HasQueryFilter(a => ScopeUnrestricted || a.BankLogin.OwnerUserId == ScopeUserId);
        modelBuilder.Entity<BankStatement>().HasQueryFilter(s =>
            ScopeUnrestricted || s.OwnerUserId == ScopeUserId
            || (s.AccountId.HasValue && (ScopeSharedAccountIds.Contains(s.AccountId.Value) || (s.SourceId.HasValue && ScopeOwnedSourceIds.Contains(s.SourceId.Value)))));

        // PortfolioPerformance data feeds the project-scoped rebalance view, so it follows the
        // source's visibility; the owner-only mapping/tax pages are guarded separately.
        modelBuilder.Entity<SecurityTaxonomy>().HasQueryFilter(e => ScopeUnrestricted || ScopeVisibleSourceIds.Contains(e.SourceId));
        modelBuilder.Entity<PpSecurity>().HasQueryFilter(e => ScopeUnrestricted || ScopeVisibleSourceIds.Contains(e.SourceId));
        modelBuilder.Entity<PpPortfolio>().HasQueryFilter(e => ScopeUnrestricted || ScopeVisibleSourceIds.Contains(e.SourceId));
        modelBuilder.Entity<PpLot>().HasQueryFilter(e => ScopeUnrestricted || ScopeVisibleSourceIds.Contains(e.SourceId));
        modelBuilder.Entity<SecurityTaxSetting>().HasQueryFilter(e => ScopeUnrestricted || ScopeVisibleSourceIds.Contains(e.SourceId));
        modelBuilder.Entity<PpSecurityLink>().HasQueryFilter(e => ScopeUnrestricted || ScopeVisibleSourceIds.Contains(e.SourceId));
    }

    // ---- Write-side access control ----

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        if (Scope != null)
            throw new InvalidOperationException("User-scoped contexts must be saved with SaveChangesAsync (write checks may query).");
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        if (Scope != null) await EnforceWriteAccessAsync(Scope, cancellationToken);
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Refuses any pending change the scope's role doesn't allow. Rules:
    /// <list type="bullet">
    /// <item>budgets, budget entries/splits, category assignments, tracking items, income/expense
    /// types, project name/description: Editor or Owner of the project;</item>
    /// <item>account assignments, shares, parent, sort order, delete, creating projects: project Owner
    /// (and the source must be owned);</item>
    /// <item>sources and everything imported or configured per source: source Owner.</item>
    /// </list>
    /// </summary>
    private async Task EnforceWriteAccessAsync(AccessScope scope, CancellationToken ct)
    {
        var budgetEntries = new List<int>();
        var loginAccountLogins = new List<int>();

        foreach (var entry in ChangeTracker.Entries().ToList())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;

            switch (entry.Entity)
            {
                case ProjectShare share:
                    scope.RequireRole(share.ProjectId, ProjectRole.Owner);
                    break;

                case Project project:
                    EnforceProjectWrite(scope, entry, project);
                    break;

                case ProjectAccountAssignment a:
                    scope.RequireRole(a.ProjectId, ProjectRole.Owner);
                    break;

                case ProjectCategoryAssignment a:
                    scope.RequireRole(a.ProjectId, ProjectRole.Editor);
                    break;
                case ProjectTrackingItem a:
                    scope.RequireRole(a.ProjectId, ProjectRole.Editor);
                    break;
                case CategoryTypeAssignment a:
                    scope.RequireRole(a.ProjectId, ProjectRole.Editor);
                    break;
                case Budget b:
                    scope.RequireRole(b.ProjectId, ProjectRole.Editor);
                    break;

                case MonthlyBudgetEntry e:
                    budgetEntries.Add(e.BudgetId);
                    break;
                case BudgetContributionSplit s:
                    budgetEntries.Add(s.BudgetId);
                    break;

                case ImportSource source:
                    EnforceSourceWrite(scope, entry, source);
                    break;

                case BankLogin login:
                    EnforceLoginWrite(scope, entry, login);
                    break;
                case BankStatement statement:
                    if (!scope.IsAuthenticated || statement.OwnerUserId != scope.UserId)
                        throw new AccessDeniedException("Statements can only be changed by the login's owner.");
                    break;
                case BankLoginAccount remote:
                    loginAccountLogins.Add(remote.BankLoginId);
                    break;

                default:
                    // Imported/per-source rows (accounts, transactions, mappings, ...) carry a SourceId.
                    if (entry.Metadata.FindProperty("SourceId") != null)
                        scope.RequireSourceOwner((int)entry.Property("SourceId").CurrentValue!);
                    else if (entry.Entity is AppUser)
                        throw new AccessDeniedException("Users are managed through the CLI only.");
                    break;
            }
        }

        // Remote accounts belong to a login; only that login's owner may touch them. The login is
        // resolved through the (owner-filtered) set, so someone else's login is simply not found.
        var loginIds = loginAccountLogins.Distinct().ToList();
        if (loginIds.Count > 0)
        {
            var visible = await Set<BankLogin>().AsNoTracking().Where(l => loginIds.Contains(l.Id)).Select(l => l.Id).ToListAsync(ct);
            foreach (var id in loginIds)
            {
                var pending = ChangeTracker.Entries<BankLogin>().Any(e => e.State == EntityState.Added && e.Entity.Id == id);
                if (!visible.Contains(id) && !pending)
                    throw new AccessDeniedException($"Bank login {id} is not accessible.");
            }
        }

        // Budget children: resolve the owning project through the (filtered) Budgets set, so a
        // budget outside the scope is refused just like a read-only one.
        var budgetIds = budgetEntries.Distinct().ToList();
        if (budgetIds.Count > 0)
        {
            var projectByBudget = await Set<Budget>().AsNoTracking()
                .Where(b => budgetIds.Contains(b.Id))
                .Select(b => new { b.Id, b.ProjectId })
                .ToDictionaryAsync(b => b.Id, b => b.ProjectId, ct);
            foreach (var id in budgetIds)
            {
                if (!projectByBudget.TryGetValue(id, out var projectId))
                {
                    // Not found in scope. A budget added in this same save is pending, not loaded.
                    var pending = ChangeTracker.Entries<Budget>().Any(e => e.State == EntityState.Added && e.Entity.Id == id);
                    if (pending) continue;
                    throw new AccessDeniedException($"Budget {id} is not accessible.");
                }
                scope.RequireRole(projectId, ProjectRole.Editor);
            }
        }
    }

    private static void EnforceProjectWrite(AccessScope scope, Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry, Project project)
    {
        if (entry.State == EntityState.Added)
        {
            if (!scope.IsAuthenticated || project.OwnerUserId != scope.UserId)
                throw new AccessDeniedException("A new project must be owned by the creating user.");
            scope.RequireSourceOwner(project.SourceId);
            if (project.ParentProjectId is { } parent) scope.RequireRole(parent, ProjectRole.Owner);
            return;
        }

        if (entry.State == EntityState.Deleted)
        {
            scope.RequireRole(project.Id, ProjectRole.Owner);
            return;
        }

        // Modified: editors may rename / re-describe; everything else (parent, order, source, owner) is owner-only.
        var changed = entry.Properties.Where(p => p.IsModified).Select(p => p.Metadata.Name).ToList();
        var editorOnly = changed.TrueForAll(n => n is nameof(Project.Name) or nameof(Project.Description));
        scope.RequireRole(project.Id, editorOnly ? ProjectRole.Editor : ProjectRole.Owner);
        if (changed.Contains(nameof(Project.OwnerUserId), StringComparer.Ordinal) || changed.Contains(nameof(Project.SourceId), StringComparer.Ordinal))
            throw new AccessDeniedException("Project ownership and source cannot be changed here.");
        if (changed.Contains(nameof(Project.ParentProjectId), StringComparer.Ordinal) && project.ParentProjectId is { } newParent)
            scope.RequireRole(newParent, ProjectRole.Owner);
    }

    private static void EnforceLoginWrite(AccessScope scope, Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry, BankLogin login)
    {
        if (!scope.IsAuthenticated || login.OwnerUserId != scope.UserId)
            throw new AccessDeniedException("Bank logins can only be changed by their owner.");
        if (entry.State == EntityState.Modified && entry.Property(nameof(BankLogin.OwnerUserId)).IsModified
            && !string.Equals((string?)entry.Property(nameof(BankLogin.OwnerUserId)).OriginalValue, scope.UserId, StringComparison.Ordinal))
            throw new AccessDeniedException("Bank login ownership cannot be changed.");
    }

    private static void EnforceSourceWrite(AccessScope scope, Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry, ImportSource source)
    {
        if (entry.State == EntityState.Added)
        {
            if (!scope.IsAuthenticated || source.OwnerUserId != scope.UserId)
                throw new AccessDeniedException("A new source must be owned by the creating user.");
            return;
        }
        scope.RequireSourceOwner(source.Id);
        if (entry.State == EntityState.Modified && entry.Property(nameof(ImportSource.OwnerUserId)).IsModified)
            throw new AccessDeniedException("Source ownership can only be changed through the CLI.");
    }
}
