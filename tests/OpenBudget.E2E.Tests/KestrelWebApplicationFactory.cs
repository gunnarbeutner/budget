using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Identity;
using OpenBudget.Auth;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// Boots the real <c>Program</c> app (same DI + middleware wiring as production) on a real Kestrel
/// port so a browser can connect, pointed at a throwaway temp SQLite file. The default
/// <see cref="WebApplicationFactory{T}"/> serves over an in-memory TestServer that Playwright can't
/// reach, so we build a second, real-Kestrel host and return a composite that owns both.
/// </summary>
public sealed class KestrelWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mmb-e2e-{Guid.NewGuid():N}.db");

    /// <summary>Starts from a copy of a migrated database (the hosts of a parallel run don't each apply every migration).</summary>
    public KestrelWebApplicationFactory() => OpenBudget.Tests.MigratedDatabase.CopyTo(_dbPath);

    /// <summary>The fake Microsoft identity platform and Graph every host of this factory talks to.</summary>
    internal OpenBudget.Tests.Connectors.FakeMicrosoftCloud Microsoft { get; } = new();

    /// <summary>The http://127.0.0.1:&lt;port&gt; address Playwright should navigate to.</summary>
    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>The services of the Kestrel host the browser talks to — for in-process singletons such as the
    /// change notifier, which <see cref="WebApplicationFactory{TEntryPoint}.Services"/> (the TestServer host) doesn't share.</summary>
    public IServiceProvider BrowserHostServices { get; private set; } = null!;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
        // Bank connectors need a secrets key (the fake bank is always on in the Test environment).
        builder.UseSetting("OPENBUDGET_SECRETS_KEY", Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray()));
        builder.UseSetting("OPENBUDGET_STATEMENTS_DIR", Path.Combine(Path.GetTempPath(), $"mmb-e2e-stmts-{Guid.NewGuid():N}"));
        // OneDrive against the fake Microsoft (see Microsoft).
        builder.UseSetting(OpenBudget.Connectors.OneDrive.OneDriveOptions.ClientIdVariable, OpenBudget.Tests.Connectors.FakeMicrosoftCloud.ClientId);
        builder.UseSetting(OpenBudget.Connectors.OneDrive.OneDriveOptions.ClientSecretVariable, OpenBudget.Tests.Connectors.FakeMicrosoftCloud.ClientSecret);
        builder.ConfigureServices(services =>
        {
            services.AddHttpClient(OpenBudget.Connectors.OneDrive.OneDriveOptions.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Microsoft);
            // Surface circuit-fatal exceptions to the browser console so E2E failures point at the
            // real server error instead of a bare "circuit terminated".
            services.Configure<Microsoft.AspNetCore.Components.Server.CircuitOptions>(
                o => o.DetailedErrors = true);

            // Swap the app's DbContextFactory (data/budgets.db) for one over our temp file.
            // Migrations still auto-apply on startup (Program.cs), creating the schema fresh.
            var toRemove = services
                .Where(d => d.ServiceType == typeof(IDbContextFactory<BudgetDbContext>)
                         || d.ServiceType == typeof(DbContextOptions<BudgetDbContext>)
                         || d.ServiceType == typeof(DbContextOptions))
                .ToList();
            foreach (var d in toRemove) services.Remove(d);

            // Same wiring as production: user-scoped factory for the app, unrestricted
            // SystemDbContextFactory for seeding.
            services.AddOpenBudgetDatabase(_dbPath);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // The in-memory host WAF wants for its own bookkeeping.
        var testHost = builder.Build();

        // A second, real Kestrel host on an OS-assigned free port.
        builder.ConfigureWebHost(webHostBuilder =>
            webHostBuilder
                .UseKestrel()
                .UseUrls("http://127.0.0.1:0")
                // Component-scoped CSS (OpenBudget.styles.css) and other static web assets
                // are auto-loaded only in the Development environment. We run as "Test", so force
                // the static-web-assets file provider on; otherwise the scoped bundle 404/500s and
                // the UI renders unstyled (e.g. stretched-link overlays intercept clicks).
                .UseStaticWebAssets());
        var kestrelHost = builder.Build();
        kestrelHost.Start();

        var addresses = kestrelHost.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!;
        BaseUrl = addresses.Addresses.First();
        BrowserHostServices = kestrelHost.Services;

        testHost.Start();
        return new CompositeHost(testHost, kestrelHost);
    }

    /// <summary>Wipe app + imported data and reseed the deterministic baseline world.</summary>
    public async Task ResetAndSeedAsync()
    {
        var factory = Services.GetRequiredService<SystemDbContextFactory>();
        await E2ESeed.ResetAsync(factory);
        Microsoft.Reset();
        // The login every test uses (owns the baseline world) and a second user to share with.
        var owner = await CreateUserAsync(E2ESeed.UserName, E2ESeed.Password);
        await CreateUserAsync(E2ESeed.SecondUserName, E2ESeed.Password);
        await E2ESeed.SeedBaselineAsync(factory, owner);
    }

    private async Task<string> CreateUserAsync(string name, string password)
    {
        await using var scope = Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser { UserName = name };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded) throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Description)));
        return user.Id;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        // Release SQLite handles before deleting the file so the unlink succeeds on Windows too.
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best-effort */ }
    }

    /// <summary>Drives the in-memory and Kestrel hosts as one. Browser requests are served by
    /// Kestrel, but <see cref="Services"/> resolves from the TestServer host: the base
    /// <see cref="WebApplicationFactory{T}"/> casts this host's <c>IServer</c> to <c>TestServer</c>
    /// right after <see cref="CreateHost"/> returns, so it must be the TestServer's provider.
    /// Seeding through it still reaches the running app because both hosts open the same temp
    /// SQLite file.</summary>
    private sealed class CompositeHost(IHost testHost, IHost kestrelHost) : IHost
    {
        public IServiceProvider Services => testHost.Services;

        public Task StartAsync(CancellationToken ct = default) =>
            Task.WhenAll(testHost.StartAsync(ct), kestrelHost.StartAsync(ct));

        public Task StopAsync(CancellationToken ct = default) =>
            Task.WhenAll(testHost.StopAsync(ct), kestrelHost.StopAsync(ct));

        public void Dispose()
        {
            testHost.Dispose();
            kestrelHost.Dispose();
        }
    }
}
