using Microsoft.EntityFrameworkCore;

namespace OpenBudget.Services;

/// <summary>
/// Unrestricted database access (no <see cref="AccessScope"/>): for the upload API (token-
/// authenticated, not user-authenticated), the CLI, migrations, and Identity's own stores. It is a
/// separate, explicitly named type so that nothing can pick it up by accident — application
/// services receive <see cref="IDbContextFactory{TContext}"/>, which resolves to the user-scoped
/// <see cref="ScopedDbContextFactory"/> in the running app.
/// </summary>
public sealed class SystemDbContextFactory : IDbContextFactory<BudgetDbContext>
{
    private readonly DbContextOptions<BudgetDbContext> _options;

    public SystemDbContextFactory(DbContextOptions<BudgetDbContext> options) => _options = options;

    public BudgetDbContext CreateDbContext() => new(_options);

    public Task<BudgetDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());
}

/// <summary>
/// The <see cref="IDbContextFactory{TContext}"/> application services get. Every context it creates
/// carries the current user's <see cref="AccessScope"/> (anonymous = sees nothing when nobody is
/// logged in), which makes the context filter every read and guard every write.
/// </summary>
public sealed class ScopedDbContextFactory : IDbContextFactory<BudgetDbContext>
{
    private readonly SystemDbContextFactory _system;
    private readonly AccessScopeProvider _access;

    public ScopedDbContextFactory(SystemDbContextFactory system, AccessScopeProvider access)
    {
        _system = system;
        _access = access;
    }

    public BudgetDbContext CreateDbContext() =>
        throw new NotSupportedException("User-scoped contexts are created asynchronously (the access scope is loaded from the database).");

    public async Task<BudgetDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        var scope = await _access.GetAsync();
        var db = _system.CreateDbContext();
        db.Scope = scope;
        return db;
    }
}
