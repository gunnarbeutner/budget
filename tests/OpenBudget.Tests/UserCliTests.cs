using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBudget.Cli;
using OpenBudget.Models;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// The operator CLI (`OpenBudget users ...`), driven through its testable core against the real
/// app services and a throwaway database.
/// </summary>
public sealed class UserCliTests : IDisposable
{
    private readonly AppTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private async Task<(int Code, string Out, string Err)> RunAsync(string stdin, params string[] args)
    {
        await _host.StartAsync();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var input = new StringReader(stdin);
        var code = await UserCli.RunAsync(args, _host.Services, input, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private async Task<Dictionary<string, string?>> ProjectOwnersAsync()
    {
        await using var db = await _host.System.CreateDbContextAsync();
        var projects = await db.Projects.Select(p => new { p.Name, p.OwnerUserId }).ToListAsync();
        var users = await db.Users.ToDictionaryAsync(u => u.Id, u => u.UserName, StringComparer.Ordinal);
        return projects.ToDictionary(p => p.Name, p => p.OwnerUserId == null ? null : users[p.OwnerUserId], StringComparer.Ordinal);
    }

    [Fact]
    public async Task The_generated_password_is_printed_once_and_works_for_login()
    {
        var (_, output, _) = await RunAsync("", "add", "alice", "--admin");   // no terminal => generated
        var password = output.Split("shown only once): ")[1].Trim();

        using var client = _host.CreateClient();
        using var login = await AppTestHost.LoginAsync(client, "alice", password);

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await AppTestHost.GetAsync(client, "/")).Status);   // to the own project
    }

    [Fact]
    public async Task Invalid_input_is_refused()
    {
        Assert.Equal(2, (await RunAsync("", "add", "Not A Slug", "--generate-password")).Code);
        Assert.Equal(2, (await RunAsync("", "add", "--generate-password")).Code);
        Assert.Equal(2, (await RunAsync("", "frobnicate")).Code);
        Assert.Equal(2, (await RunAsync("", Array.Empty<string>())).Code);
        Assert.Equal(2, (await RunAsync("", "add", "alice", "--password-stdin")).Code);   // nothing on stdin

        var (code, _, err) = await RunAsync("short\n", "add", "alice", "--password-stdin");
        Assert.Equal(1, code);
        Assert.Contains("Could not create user", err, StringComparison.Ordinal);
        await using var db = await _host.System.CreateDbContextAsync();
        Assert.Empty(await db.Users.ToListAsync());
    }

    [Fact]
    public async Task Adding_an_existing_user_fails_without_changing_anything()
    {
        await RunAsync("", "add", "alice", "--admin", "--generate-password");

        var (code, _, err) = await RunAsync("", "add", "alice", "--generate-password");

        Assert.Equal(1, code);
        Assert.Contains("already exists", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_shows_users_with_role_and_ownership_counts()
    {
        await RunAsync("", "add", "alice", "--admin", "--generate-password");
        await RunAsync("", "add", "bob", "--generate-password");
        await using (var db = await _host.System.CreateDbContextAsync())
        {
            var aliceId = (await db.Users.SingleAsync(u => u.UserName == "alice")).Id;
            db.Accounts.Add(new Account { Id = 900_001, OwnerUserId = aliceId, Name = "Example Giro" });
            db.Projects.Add(new Project { Name = "Alice Second", OwnerUserId = aliceId });
            await db.SaveChangesAsync();
        }

        var (code, output, _) = await RunAsync("", "list");

        Assert.Equal(0, code);
        var aliceLine = output.Split('\n').Single(l => l.StartsWith("alice", StringComparison.Ordinal));
        var bobLine = output.Split('\n').Single(l => l.StartsWith("bob", StringComparison.Ordinal));
        Assert.Matches(@"alice\s+admin\s+1\s+2\s+0", aliceLine);
        Assert.Matches(@"bob\s+user\s+0\s+1\s+0", bobLine);
    }

    [Fact]
    public async Task Set_password_replaces_the_password_and_ends_existing_sessions()
    {
        await RunAsync("First-Password-1\n", "add", "alice", "--password-stdin");
        using var session = await _host.LoggedInClientAsync("alice", "First-Password-1");
        Assert.Equal(HttpStatusCode.Redirect, (await AppTestHost.GetAsync(session, "/")).Status);   // to the own project

        var (code, _, _) = await RunAsync("Second-Password-2\n", "set-password", "alice", "--password-stdin");

        Assert.Equal(0, code);
        using var fresh = _host.CreateClient();
        using var old = await AppTestHost.LoginAsync(fresh, "alice", "First-Password-1");
        Assert.Equal(HttpStatusCode.OK, old.StatusCode);   // refused: form re-rendered
        using var current = await AppTestHost.LoginAsync(fresh, "alice", "Second-Password-2");
        Assert.Equal(HttpStatusCode.Redirect, current.StatusCode);
        Assert.Equal(1, (await RunAsync("", "set-password", "nobody", "--generate-password")).Code);   // unknown user
    }

    [Fact]
    public async Task Remove_releases_ownership_and_shares_but_keeps_the_data()
    {
        await RunAsync("", "add", "alice", "--admin", "--generate-password");
        await RunAsync("", "add", "bob", "--generate-password");
        await using (var db = await _host.System.CreateDbContextAsync())
        {
            var bobId = (await db.Users.SingleAsync(u => u.UserName == "bob")).Id;
            var aliceProject = await db.Projects.Where(p => p.Name == "alice").Select(p => p.Id).SingleAsync();
            db.ProjectShares.Add(new ProjectShare { ProjectId = aliceProject, UserId = bobId, Role = ProjectRole.Viewer });
            db.Accounts.Add(new Account { Id = 900_002, OwnerUserId = bobId, Name = "Bob Giro" });
            await db.SaveChangesAsync();
        }

        var (refused, _, _) = await RunAsync("", "remove", "bob");
        Assert.Equal(2, refused);   // needs --yes without a terminal

        var (code, output, _) = await RunAsync("", "remove", "bob", "--yes");

        Assert.Equal(0, code);
        Assert.Contains("Their data and 1 project(s) are now unowned", output, StringComparison.Ordinal);
        Assert.Null((await ProjectOwnersAsync())["bob"]);
        await using var check = await _host.System.CreateDbContextAsync();
        Assert.Equal(1, await check.Projects.CountAsync(p => p.Name == "bob"));   // data kept
        Assert.Equal(1, await check.Accounts.CountAsync(a => a.Name == "Bob Giro"));
        Assert.DoesNotContain(await check.ProjectShares.ToListAsync(), _ => true);          // share gone with the user
        Assert.Null(await check.Users.SingleOrDefaultAsync(u => u.UserName == "bob"));
    }

    [Fact]
    public async Task Usage_is_printed_for_help()
    {
        var (code, output, _) = await RunAsync("", "--help");

        Assert.Equal(0, code);
        Assert.Contains("users add <name>", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Users_are_created_with_identitys_default_password_hashing_and_lockout()
    {
        await RunAsync("Some-Password-3\n", "add", "alice", "--password-stdin");

        await using var scope = _host.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = (await users.FindByNameAsync("alice"))!;

        Assert.StartsWith("AQAAAA", user.PasswordHash, StringComparison.Ordinal);   // Identity v3 PBKDF2 format
        Assert.True(user.LockoutEnabled);
        Assert.NotEqual("Some-Password-3", user.PasswordHash);
    }

    [Fact]
    public async Task A_new_user_gets_a_default_project_named_after_them()
    {
        var (code, output, _) = await RunAsync("Some-Password-3\n", "add", "carol", "--password-stdin");

        Assert.Equal(0, code);
        Assert.Contains("Created project 'carol'", output, StringComparison.Ordinal);
        await using var db = await _host.System.CreateDbContextAsync();
        var userId = await db.Users.Where(u => u.UserName == "carol").Select(u => u.Id).SingleAsync();
        var project = await db.Projects.SingleAsync(p => p.OwnerUserId == userId);
        Assert.Equal("carol", project.Name);
        Assert.True(project.IsDefaultForNewAccounts);
    }
}
