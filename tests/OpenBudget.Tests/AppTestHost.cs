using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenBudget.Auth;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Tests;

/// <summary>
/// The real app (same DI + middleware as production, "Test" environment) over a throwaway SQLite
/// file, driven through HTTP with a cookie-keeping client: log in through the real login form, then
/// request real pages. Each host has its own database, so hosts run in parallel.
/// </summary>
internal sealed partial class AppTestHost : IDisposable
{
    public const string Password = "Correct-Horse-9";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ob-app-{Guid.NewGuid():N}.db");
    private readonly string _stagingDir = Path.Combine(Path.GetTempPath(), $"ob-app-uploads-{Guid.NewGuid():N}");
    private readonly WebApplicationFactory<Program> _factory;

    public AppTestHost(params (string Key, string Value)[] settings)
    {
        MigratedDatabase.CopyTo(_dbPath);

#pragma warning disable CA2000 // the derived factory is disposed in Dispose and owns the root factory
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Test");
            // Every app start clears the MoneyMoney upload staging; its own keeps the hosts (and the E2E suite running
            // alongside) from deleting each other's uploads.
            b.UseSetting("OPENBUDGET_UPLOAD_STAGING_DIR", _stagingDir);
            b.ConfigureLogging(l => l.AddProvider(new ProblemLogger(LoggedProblems)));
            b.ConfigureAppConfiguration((_, c) =>
            {
                if (settings.Length > 0) c.AddInMemoryCollection(settings.Select(x => KeyValuePair.Create(x.Key, (string?)x.Value)));
            });
            b.ConfigureServices(services =>
            {
                var toRemove = services
                    .Where(d => d.ServiceType == typeof(IDbContextFactory<BudgetDbContext>)
                             || d.ServiceType == typeof(DbContextOptions<BudgetDbContext>)
                             || d.ServiceType == typeof(DbContextOptions))
                    .ToList();
                foreach (var d in toRemove) services.Remove(d);
                services.AddOpenBudgetDatabase(_dbPath);
            });
        });
#pragma warning restore CA2000
    }

    /// <summary>Warnings/errors the app logged; handy context when an HTTP assertion fails.</summary>
    public List<string> LoggedProblems { get; } = new();

    public IServiceProvider Services => _factory.Services;

    public SystemDbContextFactory System => Services.GetRequiredService<SystemDbContextFactory>();

    public HttpClient CreateClient(string baseAddress = "https://localhost") => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        BaseAddress = new Uri(baseAddress),
        HandleCookies = true,
    });

    /// <summary>Make sure the app has started and migrated its database.</summary>
    public async Task StartAsync()
    {
        using var client = CreateClient();
        using var response = await client.GetAsync(new Uri("/healthz", UriKind.Relative));
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Adds a user; unless <paramref name="inGetStarted"/>, one who is through with Get started.</summary>
    public async Task<AppUser> AddUserAsync(string name, bool admin = false, string password = Password, bool inGetStarted = false)
    {
        await using var scope = Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser { UserName = name, IsAdmin = admin };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded) throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Description)));
        if (!inGetStarted)
        {
            await using var db = await System.CreateDbContextAsync();
            (await RowIds.StateAsync(db, user.Id)).GetStartedDismissedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        return user;
    }

    /// <summary>Logs in through the real login form; the client keeps the auth cookie.</summary>
    public static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string name, string password = Password)
    {
        var token = await FetchAntiforgeryTokenAsync(client, "/Account/Login");
        using var form = new FormUrlEncodedContent(new[] {
            KeyValuePair.Create("UserName", name),
            KeyValuePair.Create("Password", password),
            KeyValuePair.Create("__RequestVerificationToken", token),
        });
        return await client.PostAsync(new Uri("/Account/Login", UriKind.Relative), form);
    }

    public static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string path, params KeyValuePair<string, string>[] fields)
    {
        using var content = new FormUrlEncodedContent(fields);
        return await client.PostAsync(new Uri(path, UriKind.Relative), content);
    }

    public static async Task<string> FetchAntiforgeryTokenAsync(HttpClient client, string path)
    {
        using var page = await client.GetAsync(new Uri(path, UriKind.Relative));
        var html = await page.Content.ReadAsStringAsync();
        var match = TokenRegex().Match(html);
        if (!match.Success) throw new InvalidOperationException($"No antiforgery token on {path} ({(int)page.StatusCode}).");
        return WebUtility.HtmlDecode(match.Groups["token"].Value);
    }

    public async Task<HttpClient> LoggedInClientAsync(string name, string password = Password)
    {
        var client = CreateClient();
        using var response = await LoginAsync(client, name, password);
        if (response.StatusCode != HttpStatusCode.Redirect) throw new InvalidOperationException($"Login as {name} failed: {(int)response.StatusCode}");
        return client;
    }

    public static async Task<(HttpStatusCode Status, string Body)> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Seeds the AccessWorld data (sources, projects, shares) into this host's database as an unscoped writer.</summary>
    public async Task SeedWorldAsync(ProjectRole? bobOnWp = ProjectRole.Editor)
    {
        var alice = await AddUserAsync("alice", admin: true);
        var bob = await AddUserAsync("bob");

        await using var db = await System.CreateDbContextAsync();
        (await db.UserDataStates.SingleAsync(s => s.UserId == alice.Id)).IdSpace = AccessWorld.SourceAlice;
        (await db.UserDataStates.SingleAsync(s => s.UserId == bob.Id)).IdSpace = AccessWorld.SourceBob;
        foreach (var (source, group, leaf) in new[]
                 {
                     (alice.Id, AccessWorld.SharedGroup, AccessWorld.SharedLeaf),
                     (bob.Id, AccessWorld.Id(AccessWorld.SourceBob, 100), AccessWorld.Id(AccessWorld.SourceBob, 200)),
                 })
        {
            db.CategoryGroups.Add(new CategoryGroup { Id = group, OwnerUserId = source, Name = $"Group{group}" });
            db.Categories.Add(new Category { Id = leaf, OwnerUserId = source, Name = $"Leaf{leaf}", GroupId = group, Type = 1 });
        }
        foreach (var (id, name, source) in new[]
                 {
                     (AccessWorld.AccountWp, "Giro Shared", alice.Id),
                     (AccessWorld.AccountPrivate, "Giro Private", alice.Id),
                     (AccessWorld.AccountProperty, "Property Account", alice.Id),
                     (AccessWorld.AccountBob, "Bob Giro", bob.Id),
                 })
            db.Accounts.Add(new Account { Id = id, Name = name, OwnerUserId = source });

        var created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        db.Projects.Add(new Project { Id = AccessWorld.Shared, Name = "Shared", OwnerUserId = alice.Id, CreatedAt = created });
        db.Projects.Add(new Project { Id = AccessWorld.Private, Name = "Alice Private", OwnerUserId = alice.Id, CreatedAt = created });
        db.Projects.Add(new Project { Id = AccessWorld.Property, Name = "Property", OwnerUserId = alice.Id, ParentProjectId = AccessWorld.Shared, CreatedAt = created });
        db.Projects.Add(new Project { Id = AccessWorld.BobProject, Name = "Bob Private", OwnerUserId = bob.Id, CreatedAt = created });
        foreach (var p in new[] { AccessWorld.Shared, AccessWorld.Private, AccessWorld.Property })
        {
            db.ProjectTrackingItems.Add(new ProjectTrackingItem { ProjectId = p, CategoryGroupId = AccessWorld.SharedGroup });
            db.ProjectCategoryAssignments.Add(new ProjectCategoryAssignment { ProjectId = p, CategoryId = AccessWorld.SharedGroup });
        }
        db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = AccessWorld.Shared, AccountId = AccessWorld.AccountWp, Scope = AccountScope.Exclusive });
        db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = AccessWorld.Private, AccountId = AccessWorld.AccountPrivate, Scope = AccountScope.Exclusive });
        db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = AccessWorld.Property, AccountId = AccessWorld.AccountProperty, Scope = AccountScope.Exclusive });
        db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = AccessWorld.BobProject, AccountId = AccessWorld.AccountBob, Scope = AccountScope.Exclusive });

        var march = new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        foreach (var (id, account, source, leaf, cents, payee) in new[]
                 {
                     (1L, AccessWorld.AccountWp, alice.Id, AccessWorld.SharedLeaf, AccessWorld.SharedCents, "Payee Shared"),
                     (2L, AccessWorld.AccountPrivate, alice.Id, AccessWorld.SharedLeaf, AccessWorld.PrivateCents, "Payee Private"),
                     (3L, AccessWorld.AccountProperty, alice.Id, AccessWorld.SharedLeaf, AccessWorld.PropertyCents, "Payee Property"),
                     (4L, AccessWorld.AccountBob, bob.Id, AccessWorld.Id(AccessWorld.SourceBob, 200), -700L, "Payee Bob"),
                 })
            db.Transactions.Add(new Transaction { Id = id, OwnerUserId = source, AccountId = account, CategoryId = leaf, Amount = cents, Timestamp = march, Name = payee });

        await db.SaveChangesAsync();
        foreach (var p in new[] { AccessWorld.Shared, AccessWorld.Private })
        {
            var budget = new Budget { ProjectId = p, Year = 2026, Name = $"Budget{p}", IsActive = true, CreatedAt = created };
            db.Budgets.Add(budget);
            await db.SaveChangesAsync();
            var entry = new MonthlyBudgetEntry { BudgetId = budget.Id, CategoryId = AccessWorld.SharedGroup, ItemType = CategoryItemType.Group };
            for (int m = 1; m <= 12; m++) entry.SetMonthAmount(m, 100m);
            db.MonthlyBudgetEntries.Add(entry);
        }
        if (bobOnWp is { } role)
            db.ProjectShares.Add(new ProjectShare { ProjectId = AccessWorld.Shared, UserId = bob.Id, Role = role });
        await db.SaveChangesAsync();
    }

    public void Dispose()
    {
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* best-effort */ }
        try { Directory.Delete(_stagingDir, recursive: true); } catch { /* best-effort */ }
    }

    private sealed class ProblemLogger(List<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Sink(sink, categoryName);

        public void Dispose()
        {
            // Nothing to release.
        }

        private sealed class Sink(List<string> sink, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                lock (sink) sink.Add($"{category}: {formatter(state, exception)} {exception}");
            }
        }
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 2000)]
    private static partial Regex TokenRegex();
}
