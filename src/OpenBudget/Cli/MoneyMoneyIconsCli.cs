using Microsoft.EntityFrameworkCore;
using OpenBudget.Auth;
using OpenBudget.Importers.MoneyMoney;
using OpenBudget.Services;

namespace OpenBudget.Cli;

/// <summary>
/// Operator command that takes MoneyMoney's account and group icons over onto data imported before the import did,
/// run inside the container:
/// <c>docker exec openbudget dotnet OpenBudget.dll import-moneymoney-icons --user alice --db /data/MoneyMoney.sqlite [--password pw] [--apply]</c>.
/// The database may be MoneyMoney's encrypted one or a plain copy. Without <c>--apply</c> it only reports.
/// </summary>
public static class MoneyMoneyIconsCli
{
    public const string Command = "import-moneymoney-icons";

    private const string Usage = $"Usage: {Command} --user <name> --db <MoneyMoney.sqlite> [--password <pw>] [--apply]";

    public static async Task<int> RunAsync(string[] args)
    {
        string? user = null, path = null, password = null;
        bool apply = false;
        var queue = new Queue<string>(args);
        while (queue.TryDequeue(out var arg))
        {
            if (arg == "--apply") apply = true;
            else if (arg == "--user" && queue.TryDequeue(out var u)) user = u;
            else if (arg == "--db" && queue.TryDequeue(out var d)) path = d;
            else if (arg == "--password" && queue.TryDequeue(out var p)) password = p;
            else
            {
                await Console.Error.WriteLineAsync(Usage);
                return 2;
            }
        }
        if (user == null || path == null)
        {
            await Console.Error.WriteLineAsync(Usage);
            return 2;
        }
        if (!File.Exists(path))
        {
            await Console.Error.WriteLineAsync($"No file at {path}.");
            return 1;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddLogging();
        builder.Services.AddOpenBudgetDatabase(OpenBudgetPaths.DatabasePath(builder.Environment.ContentRootPath));
        builder.Services.AddOpenBudgetIdentityCore();
        using var host = builder.Build();
        await using var db = await host.Services.GetRequiredService<SystemDbContextFactory>().CreateDbContextAsync();
        await db.Database.MigrateAsync();
        var lookup = user.ToUpperInvariant();
        var ownerId = await db.Users.Where(u => u.NormalizedUserName == lookup).Select(u => u.Id).SingleOrDefaultAsync();
        if (ownerId == null)
        {
            await Console.Error.WriteLineAsync($"No user '{user}'.");
            return 1;
        }

        string? plain = null;
        try
        {
            if (!IsPlainSqlite(path))
            {
                plain = Path.Combine(Path.GetTempPath(), $"ob-mm-icons-{Guid.NewGuid():N}.sqlite");
                MoneyMoneyCipher.Decrypt(path, plain, password);
            }
            var report = new List<string>();
            await MoneyMoneyIconBackfill.RunAsync(db, ownerId, plain ?? path, apply, report);
            foreach (var line in report) await Console.Out.WriteLineAsync(line);
            if (!apply) await Console.Out.WriteLineAsync("Dry run: pass --apply to save.");
            return 0;
        }
        catch (MoneyMoneyWrongPasswordException e)
        {
            await Console.Error.WriteLineAsync(e.Message);
            return 1;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (plain != null) File.Delete(plain);
        }
    }

    /// <summary>Whether the file starts with SQLite's plain header (an encrypted database does not).</summary>
    internal static bool IsPlainSqlite(string path)
    {
        var header = "SQLite format 3\0"u8;
        Span<byte> buffer = stackalloc byte[16];
        using var f = File.OpenRead(path);
        return f.ReadAtLeast(buffer, 16, throwOnEndOfStream: false) == 16 && buffer.SequenceEqual(header);
    }
}
