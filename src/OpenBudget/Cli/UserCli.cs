using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Auth;
using OpenBudget.Models;
using OpenBudget.Services;
using System.Text.RegularExpressions;

namespace OpenBudget.Cli;

/// <summary>
/// Operator CLI for user management, run inside the container:
/// <c>docker exec openbudget dotnet OpenBudget.dll users add alice --admin</c>.
/// There is deliberately no web sign-up: this is the only way users come into existence.
/// </summary>
public static class UserCli
{
    public const string Usage = """
        OpenBudget user management

          users add <name> [--admin] [--password-stdin | --generate-password]
              Create a user. The initial password is prompted for on a terminal, read from stdin
              with --password-stdin, or generated and printed once (the default without a terminal,
              e.g. plain `docker exec`). The new user gets a project named after them.
          users list
          users set-password <name> [--password-stdin | --generate-password]
          users remove <name> [--yes]
              Deletes the login. Its data and projects stay but become unowned (invisible to
              everyone); its shares are deleted.

        Usernames: lowercase letters, digits and hyphens.
        """;

    private static readonly Regex NameRegex = new("^[a-z0-9][a-z0-9-]{0,49}$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>Entry point used by Program: builds a minimal host (no web server) over the real data directory.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddLogging();
        builder.Services.AddOpenBudgetDatabase(OpenBudgetPaths.DatabasePath(builder.Environment.ContentRootPath));
        builder.Services.AddOpenBudgetIdentityCore();
        Directory.CreateDirectory(OpenBudgetPaths.DataDir(builder.Environment.ContentRootPath));

        using var host = builder.Build();
        await using (var db = await host.Services.GetRequiredService<SystemDbContextFactory>().CreateDbContextAsync())
            await db.Database.MigrateAsync();

        var interactive = !Console.IsInputRedirected;
        return await RunAsync(args, host.Services, Console.In, Console.Out, Console.Error, interactive ? ReadSecret : null);
    }

    /// <summary>Testable core. <paramref name="readSecret"/> non-null means a terminal is attached.</summary>
    public static async Task<int> RunAsync(
        string[] args, IServiceProvider services, TextReader stdin, TextWriter stdout, TextWriter stderr,
        Func<string, string?>? readSecret = null)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            await stdout.WriteLineAsync(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var users = sp.GetRequiredService<UserManager<AppUser>>();
        var system = sp.GetRequiredService<SystemDbContextFactory>();
        var cmd = args[0].ToUpperInvariant();
        var rest = args.Skip(1).ToList();
        var flags = rest.Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        var positional = rest.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();

        switch (cmd)
        {
            case "LIST":
                return await ListAsync(users, system, stdout);

            case "ADD":
            case "SET-PASSWORD":
            case "REMOVE":
                if (positional.Count != 1)
                {
                    await stderr.WriteLineAsync($"users {args[0]} needs exactly one <name>.");
                    return 2;
                }
                // Usernames are lowercase slugs; normalising input to that form is not a case-insensitive comparison, so CA1308 doesn't apply.
#pragma warning disable CA1308
                var name = positional[0].Trim().ToLowerInvariant();
#pragma warning restore CA1308
                return cmd switch
                {
                    "ADD" => await AddAsync(users, system, name, flags, stdin, stdout, stderr, readSecret),
                    "SET-PASSWORD" => await SetPasswordAsync(users, name, flags, stdin, stdout, stderr, readSecret),
                    _ => await RemoveAsync(users, system, name, flags, stdin, stdout, stderr, readSecret != null),
                };

            default:
                await stderr.WriteLineAsync($"Unknown command 'users {args[0]}'.");
                await stderr.WriteLineAsync(Usage);
                return 2;
        }
    }

    private static async Task<int> AddAsync(
        UserManager<AppUser> users, SystemDbContextFactory system, string name, HashSet<string> flags,
        TextReader stdin, TextWriter stdout, TextWriter stderr, Func<string, string?>? readSecret)
    {
        if (!NameRegex.IsMatch(name))
        {
            await stderr.WriteLineAsync("Invalid username: use lowercase letters, digits and hyphens (max 50, starting with a letter or digit).");
            return 2;
        }
        if (await users.FindByNameAsync(name) != null)
        {
            await stderr.WriteLineAsync($"User '{name}' already exists.");
            return 1;
        }

        var (password, generated) = await ResolvePasswordAsync(flags, stdin, stderr, readSecret);
        if (password == null) return 2;

        var user = new AppUser { UserName = name, IsAdmin = flags.Contains("--admin"), CreatedAt = DateTime.UtcNow };
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            await stderr.WriteLineAsync("Could not create user: " + string.Join("; ", created.Errors.Select(e => e.Description)));
            return 1;
        }

        var project = await UserDefaultProject.EnsureAsync(system, user.Id);
        await stdout.WriteLineAsync($"Created user '{name}'{(user.IsAdmin ? " (admin)" : "")}.");
        if (project != null)
            await stdout.WriteLineAsync($"Created project '{name}' as the default for new accounts.");
        if (generated)
            await stdout.WriteLineAsync($"Initial password (shown only once): {password}");
        return 0;
    }

    private static async Task<int> SetPasswordAsync(
        UserManager<AppUser> users, string name, HashSet<string> flags,
        TextReader stdin, TextWriter stdout, TextWriter stderr, Func<string, string?>? readSecret)
    {
        var user = await users.FindByNameAsync(name);
        if (user == null)
        {
            await stderr.WriteLineAsync($"No such user '{name}'.");
            return 1;
        }

        var (password, generated) = await ResolvePasswordAsync(flags, stdin, stderr, readSecret);
        if (password == null) return 2;

        // Validate against the password policy first so a rejected password can't leave the user without one.
        foreach (var validator in users.PasswordValidators)
        {
            var check = await validator.ValidateAsync(users, user, password);
            if (!check.Succeeded)
            {
                await stderr.WriteLineAsync("Could not set password: " + string.Join("; ", check.Errors.Select(e => e.Description)));
                return 1;
            }
        }

        user.PasswordHash = users.PasswordHasher.HashPassword(user, password);
        var result = await users.UpdateAsync(user);
        if (!result.Succeeded)
        {
            await stderr.WriteLineAsync("Could not set password: " + string.Join("; ", result.Errors.Select(e => e.Description)));
            return 1;
        }
        // A new security stamp signs out every existing session of this user.
        await users.UpdateSecurityStampAsync(user);
        await users.ResetAccessFailedCountAsync(user);
        await users.SetLockoutEndDateAsync(user, null);
        await stdout.WriteLineAsync($"Password updated for '{name}' (existing sessions are signed out).");
        if (generated)
            await stdout.WriteLineAsync($"New password (shown only once): {password}");
        return 0;
    }

    private static async Task<int> RemoveAsync(
        UserManager<AppUser> users, SystemDbContextFactory system, string name, HashSet<string> flags,
        TextReader stdin, TextWriter stdout, TextWriter stderr, bool interactive)
    {
        var user = await users.FindByNameAsync(name);
        if (user == null)
        {
            await stderr.WriteLineAsync($"No such user '{name}'.");
            return 1;
        }

        if (!flags.Contains("--yes"))
        {
            if (!interactive)
            {
                await stderr.WriteLineAsync("Refusing to remove a user without --yes.");
                return 2;
            }
            await stdout.WriteAsync($"Remove user '{name}'? [y/N] ");
            var answer = await stdin.ReadLineAsync();
            if (!string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) return 1;
        }

        int projects;
        await using (var db = await system.CreateDbContextAsync())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            // The data stays but is visible to nobody: its owner id matches no user any more, and projects
            // become unowned. Shares cascade with the user.
            var ownedProjects = await db.Projects.Where(p => p.OwnerUserId == user.Id).ToListAsync();
            foreach (var p in ownedProjects) p.OwnerUserId = null!;
            projects = ownedProjects.Count;
            await db.SaveChangesAsync();
            // Delete the user's dependent rows explicitly: the app's SQLite connections don't
            // enforce foreign keys, so ON DELETE CASCADE can't be relied on.
            var id = user.Id;
            await db.ProjectShares.Where(sh => sh.UserId == id).ExecuteDeleteAsync();
            await db.Set<IdentityUserPasskey<string>>().Where(p => p.UserId == id).ExecuteDeleteAsync();
            await db.Set<IdentityUserClaim<string>>().Where(c => c.UserId == id).ExecuteDeleteAsync();
            await db.Set<IdentityUserLogin<string>>().Where(l => l.UserId == id).ExecuteDeleteAsync();
            await db.Set<IdentityUserToken<string>>().Where(t => t.UserId == id).ExecuteDeleteAsync();
            await tx.CommitAsync();
        }

        var deleted = await users.DeleteAsync(user);
        if (!deleted.Succeeded)
        {
            await stderr.WriteLineAsync("Could not remove user: " + string.Join("; ", deleted.Errors.Select(e => e.Description)));
            return 1;
        }
        await stdout.WriteLineAsync($"Removed user '{name}'. Their data and {projects} project(s) are now unowned (invisible to everyone).");
        return 0;
    }

    private static async Task<int> ListAsync(UserManager<AppUser> users, SystemDbContextFactory system, TextWriter stdout)
    {
        await using var db = await system.CreateDbContextAsync();
        var rows = await users.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync();
        var accountCounts = await db.Accounts
            .GroupBy(a => a.OwnerUserId).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, StringComparer.Ordinal);
        var projectCounts = await db.Projects.Where(p => p.OwnerUserId != null)
            .GroupBy(p => p.OwnerUserId).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, StringComparer.Ordinal);
        var passkeyCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var u in rows) passkeyCounts[u.Id] = (await users.GetPasskeysAsync(u)).Count;

        if (rows.Count == 0)
        {
            await stdout.WriteLineAsync("No users yet.");
            return 0;
        }
        await stdout.WriteLineAsync("NAME            ROLE   ACCOUNTS  PROJECTS  PASSKEYS");
        foreach (var u in rows)
        {
            await stdout.WriteLineAsync(
                $"{u.UserName,-15} {(u.IsAdmin ? "admin" : "user"),-6} {accountCounts.GetValueOrDefault(u.Id),8}  {projectCounts.GetValueOrDefault(u.Id),8}  {passkeyCounts[u.Id],8}");
        }
        return 0;
    }

    private static async Task<(string? Password, bool Generated)> ResolvePasswordAsync(
        HashSet<string> flags, TextReader stdin, TextWriter stderr, Func<string, string?>? readSecret)
    {
        if (flags.Contains("--password-stdin"))
        {
            var line = await stdin.ReadLineAsync();
            if (string.IsNullOrEmpty(line))
            {
                await stderr.WriteLineAsync("--password-stdin: no password on stdin.");
                return (null, false);
            }
            return (line, false);
        }

        if (!flags.Contains("--generate-password") && readSecret != null)
        {
            var first = readSecret("Password: ");
            var second = readSecret("Repeat password: ");
            if (string.IsNullOrEmpty(first) || first != second)
            {
                await stderr.WriteLineAsync("Passwords are empty or do not match.");
                return (null, false);
            }
            return (first, false);
        }

        return (TokenGenerator.NewPassword(), true);
    }

    private static string? ReadSecret(string prompt)
    {
        Console.Error.Write(prompt);
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) sb.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
            }
        }
        Console.Error.WriteLine();
        return sb.ToString();
    }
}
