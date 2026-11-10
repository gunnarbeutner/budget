using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBudget.Services;

namespace OpenBudget.Tests;

/// <summary>
/// Applying all migrations to a fresh SQLite file costs far more than copying one: every test host would otherwise
/// migrate its own empty database on startup. The migrations run once per test process into a template file; hosts
/// start from a copy of it, and the app's own MigrateAsync on startup then finds nothing pending (it still runs, so a
/// missing migration would still be applied and noticed).
/// </summary>
internal static class MigratedDatabase
{
    private static readonly Lazy<string> Template = new(CreateTemplate, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Writes a fully migrated, otherwise empty database to <paramref name="path"/>.</summary>
    public static void CopyTo(string path) => File.Copy(Template.Value, path, overwrite: true);

    private static string CreateTemplate()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ob-template-{Environment.ProcessId}-{Guid.NewGuid():N}.db");
        // The Identity part of the model depends on the app's schema version, read from the application services
        // (as in BudgetDbContextDesignTimeFactory).
        var services = new ServiceCollection();
        services.Configure<IdentityOptions>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3);
        using (var provider = services.BuildServiceProvider())
        using (var db = new BudgetDbContext(new DbContextOptionsBuilder<BudgetDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False")
            .UseApplicationServiceProvider(provider)
            // Its own EF internals (model cache): the contexts other tests build in parallel without the app's
            // services (TestDb) must not hand this one a model without the Identity v3 tables.
            .EnableServiceProviderCaching(false)
            .Options))
        {
            db.Database.Migrate();
        }
        SqliteConnection.ClearAllPools();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { File.Delete(path); } catch (IOException) { /* best-effort */ }
        };
        return path;
    }
}
