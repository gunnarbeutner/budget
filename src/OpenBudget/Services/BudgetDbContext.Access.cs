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
        modelBuilder.Entity<Project>().HasIndex(p => p.OwnerUserId, "IX_projects_OwnerUserId_Default")
            .IsUnique().HasFilter("IsDefaultForNewAccounts = 1");
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
        modelBuilder.Entity<CategoryRule>().HasQueryFilter(e => ScopeUnrestricted || ScopeVisibleProjectIds.Contains(e.ProjectId));
        modelBuilder.Entity<MonthlyBudgetEntry>().HasQueryFilter(e => ScopeUnrestricted || ScopeVisibleProjectIds.Contains(e.Budget.ProjectId));
        modelBuilder.Entity<BudgetContributionSplit>().HasQueryFilter(e => ScopeUnrestricted || ScopeVisibleProjectIds.Contains(e.Budget.ProjectId));

        // Sources, and the category tree (names/hierarchy) of every source that has a visible project.
        modelBuilder.Entity<ImportSource>().HasQueryFilter(s => ScopeUnrestricted || ScopeVisibleSourceIds.Contains(s.Id));
        modelBuilder.Entity<Category>().HasQueryFilter(e => ScopeUnrestricted || ScopeVisibleSourceIds.Contains(e.SourceId));
        modelBuilder.Entity<CategoryGroup>().HasQueryFilter(e => ScopeUnrestricted || ScopeVisibleSourceIds.Contains(e.SourceId));
        modelBuilder.Entity<AccountGroup>().HasQueryFilter(e => ScopeUnrestricted || ScopeVisibleSourceIds.Contains(e.SourceId));
        modelBuilder.Entity<RemovedImportItem>().HasQueryFilter(e => ScopeUnrestricted || ScopeOwnedSourceIds.Contains(e.SourceId));
        modelBuilder.Entity<RemovedImportItem>().HasIndex(e => new { e.SourceId, e.Kind, e.ItemId }).IsUnique();

        // Accounts and what hangs off them: all of an owned source, otherwise ONLY the accounts
        // assigned to a project shared with the user.
        modelBuilder.Entity<Account>().HasQueryFilter(a =>
            ScopeUnrestricted || ScopeOwnedSourceIds.Contains(a.SourceId) || ScopeSharedAccountIds.Contains(a.Id));
        modelBuilder.Entity<Transaction>().HasQueryFilter(t =>
            ScopeUnrestricted || ScopeOwnedSourceIds.Contains(t.SourceId)
            || (t.AccountId.HasValue && ScopeSharedAccountIds.Contains(t.AccountId.Value)));
        modelBuilder.Entity<Holding>().HasQueryFilter(h =>
            ScopeUnrestricted || ScopeOwnedSourceIds.Contains(h.SourceId) || ScopeSharedAccountIds.Contains(h.AccountId));
        modelBuilder.Entity<ScheduledPayment>().HasQueryFilter(p =>
            ScopeUnrestricted || ScopeOwnedSourceIds.Contains(p.SourceId) || ScopeSharedAccountIds.Contains(p.AccountId));
        modelBuilder.Entity<PortfolioChartPoint>().HasQueryFilter(p =>
            ScopeUnrestricted || ScopeOwnedSourceIds.Contains(p.SourceId) || ScopeSharedAccountIds.Contains(p.AccountId));
        modelBuilder.Entity<PpPortfolioLink>().HasQueryFilter(l =>
            ScopeUnrestricted || ScopeOwnedSourceIds.Contains(l.SourceId) || ScopeSharedAccountIds.Contains(l.AccountId));

        // Bank logins are strictly personal. Statements follow their account (shared viewers may read them); account-less ones are owner-only.
        modelBuilder.Entity<BankLogin>().HasQueryFilter(b => ScopeUnrestricted || b.OwnerUserId == ScopeUserId);
        modelBuilder.Entity<CloudConnection>().HasQueryFilter(c => ScopeUnrestricted || c.OwnerUserId == ScopeUserId);
        modelBuilder.Entity<CloudFileLink>().HasQueryFilter(f => ScopeUnrestricted || f.OwnerUserId == ScopeUserId);
        modelBuilder.Entity<BankLoginAccount>().HasQueryFilter(a => ScopeUnrestricted || a.BankLogin.OwnerUserId == ScopeUserId);
        modelBuilder.Entity<BankStatement>().HasQueryFilter(s =>
            ScopeUnrestricted || s.OwnerUserId == ScopeUserId
            || (s.AccountId.HasValue && (ScopeSharedAccountIds.Contains(s.AccountId.Value) || (s.SourceId.HasValue && ScopeOwnedSourceIds.Contains(s.SourceId.Value)))));
        // Read marks and the order of the project list are personal.
        modelBuilder.Entity<UserProjectOrder>().HasQueryFilter(o => ScopeUnrestricted || o.UserId == ScopeUserId);
        modelBuilder.Entity<StatementRead>().HasQueryFilter(r => ScopeUnrestricted || r.UserId == ScopeUserId);

        // PortfolioPerformance data feeds the project-scoped rebalance view, so it follows the
        // source's visibility; the owner-only mapping/tax pages are guarded separately.
        modelBuilder.Entity<SecurityTaxonomy>().HasQueryFilter(e => ScopeUnrestricted || ScopeVisibleSourceIds.Contains(e.SourceId));
        modelBuilder.Entity<PpSecurity>().HasQueryFilter(e => ScopeUnrestricted || ScopeVisibleSourceIds.Contains(e.SourceId));
        modelBuilder.Entity<PpPortfolio>().HasQueryFilter(e => ScopeUnrestricted || ScopeVisibleSourceIds.Contains(e.SourceId));
        modelBuilder.Entity<PpPortfolioSetting>().HasQueryFilter(e => ScopeUnrestricted || ScopeOwnedSourceIds.Contains(e.SourceId));
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
    /// <item>account assignments, shares, parent, delete, creating projects: project Owner
    /// (and the source must be owned);</item>
    /// <item>a transaction's category, note, check mark and new marker (and nothing else of it): the source Owner or an
    /// Editor of a project the account is in - and only while the account is not owned by MoneyMoney;</item>
    /// <item>a user's own project-list order: that user, for projects they can see;</item>
    /// <item>sources and everything imported or configured per source: source Owner.</item>
    /// </list>
    /// </summary>
    private async Task EnforceWriteAccessAsync(AccessScope scope, CancellationToken ct)
    {
        var budgetEntries = new List<int>();
        var loginAccountLogins = new List<int>();
        var readStatements = new List<long>();
        var editedTransactions = new List<(int SourceId, long? AccountId)>();

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
                case CategoryRule r:
                    // Categorisation rules: editors of the project (like its budgets).
                    scope.RequireRole(r.ProjectId, ProjectRole.Editor);
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

                case Transaction transaction:
                    EnforceTransactionWrite(scope, entry, transaction, editedTransactions);
                    break;

                case BankLogin login:
                    EnforceLoginWrite(scope, entry, login);
                    break;
                case BankStatement statement:
                    if (!scope.IsAuthenticated || statement.OwnerUserId != scope.UserId)
                        throw new AccessDeniedException("Statements can only be changed by the login's owner.");
                    break;
                case CloudConnection connection:
                    if (!scope.IsAuthenticated || connection.OwnerUserId != scope.UserId)
                        throw new AccessDeniedException("Cloud connections belong to their user.");
                    break;
                case CloudFileLink link:
                    if (!scope.IsAuthenticated || link.OwnerUserId != scope.UserId)
                        throw new AccessDeniedException("Linked cloud files belong to their user.");
                    break;
                case StatementRead read:
                    if (!scope.IsAuthenticated || read.UserId != scope.UserId)
                        throw new AccessDeniedException("Read marks belong to their user.");
                    readStatements.Add(read.StatementId);
                    break;
                case UserProjectOrder order:
                    if (!scope.IsAuthenticated || order.UserId != scope.UserId)
                        throw new AccessDeniedException("The order of the project list belongs to its user.");
                    if (entry.State != EntityState.Deleted) scope.RequireRole(order.ProjectId, ProjectRole.Viewer);
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

        // Edits of user-owned transaction fields additionally require an account that is not MoneyMoney's any more.
        // The account is looked up through the (filtered) set; an account the user cannot see counts as read-only.
        if (editedTransactions.Count > 0)
        {
            var accountIds = editedTransactions.Where(e => e.AccountId.HasValue).Select(e => e.AccountId!.Value).Distinct().ToList();
            var providers = await Set<Account>().AsNoTracking().Where(a => accountIds.Contains(a.Id)).Select(a => new { a.Id, a.Provider }).ToDictionaryAsync(a => a.Id, a => a.Provider, ct);
            foreach (var (sourceId, accountId) in editedTransactions)
            {
                string? provider = accountId is { } id && providers.TryGetValue(id, out var p) ? p : null;
                var block = TransactionEditRules.Check(scope, sourceId, accountId, provider);
                if (block == EditBlock.MoneyMoney)
                    throw new AccessDeniedException("Transactions of accounts still owned by MoneyMoney are read-only.");
                if (block == EditBlock.NoPermission)
                    throw new AccessDeniedException("Not allowed to edit these transactions.");
            }
        }

        // A read mark needs a statement the user can see (looked up through the filtered set).
        if (readStatements.Count > 0)
        {
            var ids = readStatements.Distinct().ToList();
            var visible = await Set<BankStatement>().AsNoTracking().CountAsync(s => ids.Contains(s.Id), ct);
            if (visible != ids.Count) throw new AccessDeniedException("Statement not found.");
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

    /// <summary>
    /// Changing only user-owned fields (category, note, check mark, new marker) of an existing transaction is allowed to
    /// the source's owner and to editors of a project the account is assigned to (provider rule: see the caller);
    /// everything else on a transaction is refused for a changed bank field, and creating or deleting is the source owner's alone
    /// (except split parts, see <see cref="TransactionEditRules.IsOwnSplitPart"/>, which follow the edit rights).
    /// </summary>
    private static void EnforceTransactionWrite(
        AccessScope scope, Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry, Transaction transaction,
        List<(int SourceId, long? AccountId)> edited)
    {
        var onlyEditable = entry.State == EntityState.Modified
            && entry.Properties.Where(p => p.IsModified).All(p => TransactionEditRules.EditableProperties.Contains(p.Metadata.Name));
        if (!onlyEditable && entry.State is EntityState.Added or EntityState.Deleted && TransactionEditRules.IsOwnSplitPart(transaction))
            onlyEditable = true;
        if (!onlyEditable)
        {
            // Bank fields (amount, date, payee, purpose, ...) are never edited through a user's context; only the
            // import and the connectors (system contexts) write them. Creating/deleting rows stays the source owner's.
            if (entry.State == EntityState.Modified)
                throw new AccessDeniedException("The bank's fields of a transaction cannot be changed.");
            scope.RequireSourceOwner(transaction.SourceId);
            return;
        }
        if (!scope.CanEditTransactionsOf(transaction.SourceId, transaction.AccountId))
            throw new AccessDeniedException("Not allowed to edit these transactions.");
        edited.Add((transaction.SourceId, transaction.AccountId));
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
