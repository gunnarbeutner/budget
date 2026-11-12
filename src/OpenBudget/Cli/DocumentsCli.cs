using Microsoft.EntityFrameworkCore;
using OpenBudget.Auth;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Sync;
using OpenBudget.Services;

namespace OpenBudget.Cli;

/// <summary>
/// Operator command that folds duplicate documents (same owner, content and date) into one, run inside the container:
/// <c>docker exec openbudget dotnet OpenBudget.dll dedupe-documents [--apply]</c>. Without <c>--apply</c> it only reports.
/// </summary>
public static class DocumentsCli
{
    public const string Command = "dedupe-documents";

    public static async Task<int> RunAsync(string[] args)
    {
        var unknown = args.Where(a => a != "--apply").ToList();
        if (unknown.Count > 0)
        {
            await Console.Error.WriteLineAsync($"Usage: {Command} [--apply]");
            return 2;
        }
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddLogging();
        var contentRoot = builder.Environment.ContentRootPath;
        builder.Services.AddOpenBudgetDatabase(OpenBudgetPaths.DatabasePath(contentRoot));
        builder.Services.AddOpenBudgetIdentityCore();
        using var host = builder.Build();

        var store = new BankStatementStore(new BankStorageOptions(
            builder.Configuration[BankConnectorsRegistration.StatementsDirVariable] is { Length: > 0 } custom
                ? custom
                : Path.Combine(OpenBudgetPaths.DataDir(contentRoot), "statements")));
        await using var db = await host.Services.GetRequiredService<SystemDbContextFactory>().CreateDbContextAsync();
        await db.Database.MigrateAsync();
        var report = new List<string>();
        await DocumentDedupe.RunAsync(db, store, args.Contains("--apply"), report);
        foreach (var line in report) await Console.Out.WriteLineAsync(line);
        return 0;
    }
}
