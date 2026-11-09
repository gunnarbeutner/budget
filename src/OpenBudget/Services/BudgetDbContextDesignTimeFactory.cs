using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OpenBudget.Services;

/// <summary>
/// Used only by the EF tools (<c>dotnet ef migrations add</c>): the app's registered
/// <see cref="IDbContextFactory{TContext}"/> is user-scoped and can't be created synchronously.
/// Mirrors the runtime Identity schema version (3 = includes the passkeys table), which the model
/// reads from the application service provider.
/// </summary>
public sealed class BudgetDbContextDesignTimeFactory : IDesignTimeDbContextFactory<BudgetDbContext>
{
    public BudgetDbContext CreateDbContext(string[] args)
    {
        var services = new ServiceCollection();
        services.Configure<IdentityOptions>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3);
        var provider = services.BuildServiceProvider();
        return new BudgetDbContext(new DbContextOptionsBuilder<BudgetDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .UseApplicationServiceProvider(provider)
            .Options);
    }
}
