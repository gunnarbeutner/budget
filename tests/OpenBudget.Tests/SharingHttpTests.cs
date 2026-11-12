using System.Net;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// Sharing end to end: real login, real pages. A project outside the user's scope behaves exactly
/// like a URL that doesn't exist; only what a share grants is rendered.
/// </summary>
public sealed class SharingHttpTests : IDisposable
{
    private const string NotFoundText = "nothing at this address";

    private readonly AppTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private async Task<int> BudgetIdAsync(int projectId)
    {
        await using var db = await _host.System.CreateDbContextAsync();
        return await db.Budgets.Where(b => b.ProjectId == projectId).Select(b => b.Id).SingleAsync();
    }

    private async Task<(HttpStatusCode Status, string Body)> GetAsync(HttpClient client, string path)
    {
        var result = await AppTestHost.GetAsync(client, path);
        Assert.True(result.Status != HttpStatusCode.InternalServerError, $"{path}: {string.Join("\n", _host.LoggedProblems)}");
        return result;
    }

    private async Task AssertNotFoundAsync(HttpClient client, params string[] paths)
    {
        foreach (var path in paths)
        {
            var (status, body) = await GetAsync(client, path);
            Assert.True(status == HttpStatusCode.NotFound, $"{path} -> {(int)status}");
            Assert.Contains(NotFoundText, body, StringComparison.Ordinal);
            Assert.DoesNotContain("Alice Private", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Editor_sees_the_shared_project_and_gets_not_found_for_everything_else_of_the_owner()
    {
        await _host.SeedWorldAsync(ProjectRole.Editor);
        using var bob = await _host.LoggedInClientAsync("bob");
        var privateBudget = await BudgetIdAsync(AccessWorld.Private);

        foreach (var path in new[] { "/projects/1/overview", "/projects/1/browse", "/projects/1/budgets", "/projects/1/cashflow", "/projects/1/rebalance", "/projects/1/portfolio", "/projects/1/settings" })
        {
            var (status, body) = await GetAsync(bob, path);
            // Some pages canonicalise their URL (add ?year=) with a redirect; either way: not "not found".
            Assert.True(status is HttpStatusCode.OK or HttpStatusCode.Found, $"{path} -> {(int)status}");
            Assert.DoesNotContain(NotFoundText, body, StringComparison.Ordinal);
            Assert.DoesNotContain("Alice Private", body, StringComparison.Ordinal);   // not even in the project switcher
        }

        await AssertNotFoundAsync(bob,
            "/projects/2/overview", "/projects/2/browse", "/projects/2/budgets", "/projects/2/cashflow",
            "/projects/2/rebalance", "/projects/2/portfolio", "/projects/2/settings", "/projects/2/budgets/new", $"/budgets/{privateBudget}/edit",
            "/projects/999/overview");
    }

    [Fact]
    public async Task Overview_and_browse_of_a_shared_project_show_only_its_accounts()
    {
        await _host.SeedWorldAsync(ProjectRole.Editor);
        using var bob = await _host.LoggedInClientAsync("bob");

        var (_, browse) = await GetAsync(bob, "/projects/1/browse?year=2026&month=3");

        Assert.Contains("Giro Shared", browse, StringComparison.Ordinal);
        Assert.DoesNotContain("Giro Private", browse, StringComparison.Ordinal);
        Assert.DoesNotContain("Payee Private", browse, StringComparison.Ordinal);
        Assert.DoesNotContain("Bob Giro", browse, StringComparison.Ordinal);   // her own source's account isn't part of Shared either

        var (_, overview) = await GetAsync(bob, "/projects/1/overview?year=2026&month=3");
        Assert.DoesNotContain("Giro Private", overview, StringComparison.Ordinal);
        Assert.DoesNotContain("Payee Private", overview, StringComparison.Ordinal);

        // The owner, by contrast, can see the private account in the same project's browse.
        using var alice = await _host.LoggedInClientAsync("alice");
        var (_, ownerBrowse) = await GetAsync(alice, "/projects/1/browse?year=2026&month=3");
        Assert.Contains("Giro Shared", ownerBrowse, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_old_project_list_redirects_into_a_project_in_scope()
    {
        await _host.SeedWorldAsync(ProjectRole.Editor);

        // /projects/manage is gone (the switcher has its features); it lands in one of the user's projects like "/".
        using var bob = await _host.LoggedInClientAsync("bob");
        var bobTarget = await RedirectTargetAsync(bob, "/projects/manage");
        Assert.Contains(bobTarget, new[] { $"/projects/{AccessWorld.Shared}/browse", $"/projects/{AccessWorld.Property}/browse", $"/projects/{AccessWorld.BobProject}/browse" });

        using var alice = await _host.LoggedInClientAsync("alice");
        var aliceTarget = await RedirectTargetAsync(alice, "/projects/manage?new=1");
        Assert.Contains(aliceTarget, new[] { $"/projects/{AccessWorld.Shared}/browse", $"/projects/{AccessWorld.Private}/browse", $"/projects/{AccessWorld.Property}/browse" });
    }

    private async Task<string> RedirectTargetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        Assert.True(response.StatusCode == HttpStatusCode.Found, $"{path} -> {(int)response.StatusCode}: {string.Join("\n", _host.LoggedProblems)}");
        var location = response.Headers.Location!;
        return location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;
    }

    [Fact]
    public async Task Viewer_cannot_open_editing_pages_but_can_open_the_read_views()
    {
        await _host.SeedWorldAsync(ProjectRole.Viewer);
        using var bob = await _host.LoggedInClientAsync("bob");
        var sharedBudget = await BudgetIdAsync(AccessWorld.Shared);

        foreach (var path in new[] { "/projects/1/overview", "/projects/1/browse", "/projects/1/budgets", "/projects/1/cashflow", "/projects/1/rebalance" })
            Assert.True((await GetAsync(bob, path)).Status is HttpStatusCode.OK or HttpStatusCode.Found, path);

        await AssertNotFoundAsync(bob, "/projects/1/settings", "/projects/1/budgets/new", $"/budgets/{sharedBudget}/edit");

        // The budgets page offers no edit actions to a viewer.
        var (_, budgets) = await GetAsync(bob, "/projects/1/budgets");
        Assert.DoesNotContain("/budgets/new", budgets, StringComparison.Ordinal);
        Assert.DoesNotContain("/edit", budgets, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Editor_gets_budget_editing_but_not_the_owner_only_settings_sections()
    {
        await _host.SeedWorldAsync(ProjectRole.Editor);
        using var bob = await _host.LoggedInClientAsync("bob");
        var sharedBudget = await BudgetIdAsync(AccessWorld.Shared);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(bob, $"/budgets/{sharedBudget}/edit")).Status);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(bob, "/projects/1/budgets/new")).Status);

        var (_, editorSettings) = await GetAsync(bob, "/projects/1/settings");
        Assert.DoesNotContain("sharing-card", editorSettings, StringComparison.Ordinal);
        Assert.DoesNotContain("accounts-card", editorSettings, StringComparison.Ordinal);
        foreach (var ownerOnly in new[] { "/projects/1/settings/sharing", "/projects/1/settings/accounts" })
        {
            var (_, editorView) = await GetAsync(bob, ownerOnly);
            Assert.DoesNotContain("sharing-card", editorView, StringComparison.Ordinal);
            Assert.DoesNotContain("accounts-card", editorView, StringComparison.Ordinal);
        }

        using var alice = await _host.LoggedInClientAsync("alice");
        var (_, ownerSharing) = await GetAsync(alice, "/projects/1/settings/sharing");
        Assert.Contains("sharing-card", ownerSharing, StringComparison.Ordinal);
        var (_, ownerAccounts) = await GetAsync(alice, "/projects/1/settings/accounts");
        Assert.Contains("accounts-card", ownerAccounts, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Source_pages_are_owner_only()
    {
        await _host.SeedWorldAsync(ProjectRole.Editor);
        using var bob = await _host.LoggedInClientAsync("bob");

        await AssertNotFoundAsync(bob, "/portfolio-performance/2/depots", "/portfolio-performance/2/instruments", "/portfolio-performance/2/tax");
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(bob, "/portfolio-performance/3/depots")).Status);   // her own source
        Assert.Equal("/portfolio-performance/3/depots", await RedirectTargetAsync(bob, "/sources/3/depots"));   // old links

        // The sources list shows only her own source, and never alice's token.
        var (_, sources) = await GetAsync(bob, "/sources");
        Assert.Contains("bob", sources, StringComparison.Ordinal);
        Assert.DoesNotContain("tok-alice", sources, StringComparison.Ordinal);
        Assert.DoesNotContain(">alice<", sources, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unshared_user_gets_not_found_for_every_project_of_the_other()
    {
        await _host.SeedWorldAsync(bobOnWp: null);
        using var bob = await _host.LoggedInClientAsync("bob");

        await AssertNotFoundAsync(bob, "/projects/1/overview", "/projects/1/browse", "/projects/3/overview", "/projects/2/settings");
        Assert.Equal($"/projects/{AccessWorld.BobProject}/browse", await RedirectTargetAsync(bob, "/"));
        var (_, own) = await GetAsync(bob, $"/projects/{AccessWorld.BobProject}/browse");
        Assert.DoesNotContain(">Shared<", own, StringComparison.Ordinal);
        Assert.Contains("Bob Private", own, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admin_has_no_view_into_other_users_data()
    {
        await _host.SeedWorldAsync(bobOnWp: null);
        using var alice = await _host.LoggedInClientAsync("alice");   // the admin

        await AssertNotFoundAsync(alice, "/projects/4/overview", "/projects/4/browse", "/projects/4/settings", "/sources/3/depots");
        var (_, sources) = await GetAsync(alice, "/sources");
        Assert.DoesNotContain("tok-bob", sources, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unclaimed_data_is_invisible_to_everyone_including_admins()
    {
        await _host.AddUserAsync("alice", admin: true);
        await using (var db = await _host.System.CreateDbContextAsync())
        {
            db.ImportSources.Add(new ImportSource { Id = 7, Name = "orphan" });
            db.Projects.Add(new Project { Id = 7, Name = "Orphan Project", SourceId = 7 });
            await db.SaveChangesAsync();
        }
        using var alice = await _host.LoggedInClientAsync("alice");

        await AssertNotFoundAsync(alice, "/projects/7/overview", "/sources/7/depots");
        // Without a project of her own, "/" (and the old /projects/manage) show the first-run welcome.
        var (status, home) = await GetAsync(alice, "/projects/manage");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Welcome", home, StringComparison.Ordinal);
        Assert.DoesNotContain("Orphan Project", home, StringComparison.Ordinal);
    }
}
