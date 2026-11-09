using System.Net;
using System.Net.Http.Headers;
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
using OpenBudget.Compat;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Tests;

/// <summary>
/// The real app (same DI + middleware as production, "Test" environment) over a throwaway SQLite
/// file, driven through HTTP with a cookie-keeping client: log in through the real login form, then
/// request real pages. Tests using this must join the "ProcessEnvironment" collection because the
/// legacy upload token is read from a process-wide environment variable.
/// </summary>
internal sealed partial class AppTestHost : IDisposable
{
    public const string LegacyToken = "legacy-token";
    public const string Password = "Correct-Horse-9";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ob-app-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public AppTestHost(params (string Key, string Value)[] settings)
    {
        Environment.SetEnvironmentVariable(LegacyCompat.UploadTokenVariable, null);
        Environment.SetEnvironmentVariable(LegacyCompat.LegacyUploadTokenVariable, LegacyToken);

#pragma warning disable CA2000 // the derived factory is disposed in Dispose and owns the root factory
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Test");
            b.ConfigureLogging(l => l.AddProvider(new ProblemLogger(LoggedProblems)));
            if (settings.Length > 0) b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(settings.Select(x => KeyValuePair.Create(x.Key, (string?)x.Value))));
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

    public HttpClient CreateClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        BaseAddress = new Uri("https://localhost"),
        HandleCookies = true,
    });

    /// <summary>Make sure the app has started and migrated its database.</summary>
    public async Task StartAsync()
    {
        using var client = CreateClient();
        using var response = await client.GetAsync(new Uri("/healthz", UriKind.Relative));
        response.EnsureSuccessStatusCode();
    }

    public async Task<AppUser> AddUserAsync(string name, bool admin = false, string password = Password)
    {
        await using var scope = Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser { UserName = name, IsAdmin = admin };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded) throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Description)));
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

    public static HttpRequestMessage Upload(string path, byte[] body, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return request;
    }

    /// <summary>A minimal database in the shape the exporter produces: one account, one transaction.</summary>
    public static byte[] BuildSnapshot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ob-snap-{Guid.NewGuid():N}.sqlite");
        try
        {
            using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE accounts (name TEXT, iban TEXT, currency TEXT, type INTEGER, portfolio INTEGER, balances TEXT, sort INTEGER, refresh_tsp INTEGER, error TEXT, group_key INTEGER);
                    CREATE TABLE account_groups (name TEXT, group_key INTEGER, sort INTEGER);
                    CREATE TABLE category_groups (name TEXT, group_key INTEGER, sort INTEGER, color TEXT);
                    CREATE TABLE categories (name TEXT, type INTEGER, color TEXT, rules TEXT, group_key INTEGER, sort INTEGER);
                    CREATE TABLE transactions (local_account_key INTEGER, amount REAL, currency TEXT, timestamp INTEGER, name TEXT, comment TEXT, unformatted_purpose TEXT, category_key INTEGER, checkmark INTEGER, splitted INTEGER);
                    INSERT INTO accounts (name, currency, type, balances) VALUES ('Giro', 'EUR', 1, '11:100.000000,3:EUR,');
                    INSERT INTO transactions (local_account_key, amount, currency, timestamp, name, category_key, checkmark, splitted)
                        VALUES (1, -12.5, 'EUR', 1700000000, 'Shop', 0, 0, 0);
                    """;
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            return File.ReadAllBytes(path);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort */ }
        }
    }

    /// <summary>Seeds the AccessWorld data (sources, projects, shares) into this host's database as an unscoped writer.</summary>
    public async Task SeedWorldAsync(ProjectRole? bobOnWp = ProjectRole.Editor)
    {
        var alice = await AddUserAsync("alice", admin: true);
        var bob = await AddUserAsync("bob");

        await using var db = await System.CreateDbContextAsync();
        db.ImportSources.Add(new ImportSource { Id = AccessWorld.SourceAlice, Name = "alice", OwnerUserId = alice.Id, UploadToken = "tok-alice" });
        db.ImportSources.Add(new ImportSource { Id = AccessWorld.SourceBob, Name = "bob", OwnerUserId = bob.Id, UploadToken = "tok-bob" });
        foreach (var (source, group, leaf) in new[]
                 {
                     (AccessWorld.SourceAlice, AccessWorld.SharedGroup, AccessWorld.SharedLeaf),
                     (AccessWorld.SourceBob, AccessWorld.Id(AccessWorld.SourceBob, 100), AccessWorld.Id(AccessWorld.SourceBob, 200)),
                 })
        {
            db.CategoryGroups.Add(new CategoryGroup { Id = group, SourceId = source, Name = $"Group{source}" });
            db.Categories.Add(new Category { Id = leaf, SourceId = source, Name = $"Leaf{source}", GroupId = group, Type = 1 });
        }
        foreach (var (id, name, source) in new[]
                 {
                     (AccessWorld.AccountWp, "Giro Shared", AccessWorld.SourceAlice),
                     (AccessWorld.AccountPrivate, "Giro Private", AccessWorld.SourceAlice),
                     (AccessWorld.AccountProperty, "Property Account", AccessWorld.SourceAlice),
                     (AccessWorld.AccountBob, "Bob Giro", AccessWorld.SourceBob),
                 })
            db.Accounts.Add(new Account { Id = id, Name = name, SourceId = source });

        var created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        db.Projects.Add(new Project { Id = AccessWorld.Shared, Name = "Shared", SourceId = AccessWorld.SourceAlice, OwnerUserId = alice.Id, CreatedAt = created });
        db.Projects.Add(new Project { Id = AccessWorld.Private, Name = "Alice Private", SourceId = AccessWorld.SourceAlice, OwnerUserId = alice.Id, CreatedAt = created });
        db.Projects.Add(new Project { Id = AccessWorld.Property, Name = "Property", SourceId = AccessWorld.SourceAlice, OwnerUserId = alice.Id, ParentProjectId = AccessWorld.Shared, CreatedAt = created });
        db.Projects.Add(new Project { Id = AccessWorld.BobProject, Name = "Bob Private", SourceId = AccessWorld.SourceBob, OwnerUserId = bob.Id, CreatedAt = created });
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
                     (1L, AccessWorld.AccountWp, AccessWorld.SourceAlice, AccessWorld.SharedLeaf, AccessWorld.SharedCents, "Payee Shared"),
                     (2L, AccessWorld.AccountPrivate, AccessWorld.SourceAlice, AccessWorld.SharedLeaf, AccessWorld.PrivateCents, "Payee Private"),
                     (3L, AccessWorld.AccountProperty, AccessWorld.SourceAlice, AccessWorld.SharedLeaf, AccessWorld.PropertyCents, "Payee Property"),
                     (4L, AccessWorld.AccountBob, AccessWorld.SourceBob, AccessWorld.Id(AccessWorld.SourceBob, 200), -700L, "Payee Bob"),
                 })
            db.Transactions.Add(new Transaction { Id = id, SourceId = source, AccountId = account, CategoryId = leaf, Amount = cents, Timestamp = march, Name = payee });

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
        Environment.SetEnvironmentVariable(LegacyCompat.LegacyUploadTokenVariable, null);
        try { File.Delete(_dbPath); } catch { /* best-effort */ }
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
