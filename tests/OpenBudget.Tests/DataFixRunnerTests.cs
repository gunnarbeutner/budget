using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBudget.Models;
using OpenBudget.Services;
using OpenBudget.Services.DataFixes;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>Data fixes run once per database, are recorded, and a failing one is retried at the next start.</summary>
public sealed class DataFixRunnerTests
{
    private static DataFixRunner Runner(TestDb t) =>
        new(t.System, new ServiceCollection().BuildServiceProvider(), NullLogger<DataFixRunner>.Instance, TimeProvider.System);

    private static async Task<List<string>> RecordedAsync(TestDb t)
    {
        await using var db = await t.System.CreateDbContextAsync();
        return await db.AppliedDataFixes.OrderBy(f => f.Name).Select(f => f.Name).ToListAsync();
    }

    [Fact]
    public async Task A_fix_runs_once_and_is_recorded()
    {
        await using var t = new TestDb();
        var runs = 0;
        DataFix[] fixes = [new("rename-sample", "Renames the default user", async c =>
        {
            runs++;
            (await c.Db.Users.SingleAsync(u => u.Id == TestDb.DefaultOwner)).DisplayName = "Sample";
            await c.Db.SaveChangesAsync();
        })];

        Assert.Equal(["rename-sample"], await Runner(t).RunAsync(fixes));
        Assert.Empty(await Runner(t).RunAsync(fixes));

        Assert.Equal(1, runs);
        Assert.Equal(["rename-sample"], await RecordedAsync(t));
        await using var db = await t.System.CreateDbContextAsync();
        Assert.Equal("Sample", (await db.Users.SingleAsync(u => u.Id == TestDb.DefaultOwner)).DisplayName);
    }

    [Fact]
    public async Task A_failing_fix_is_rolled_back_holds_back_later_ones_and_retries_next_start()
    {
        await using var t = new TestDb();
        var fail = true;
        var laterRuns = 0;
        DataFix[] fixes =
        [
            new("flaky", "Fails the first time", async c =>
            {
                (await c.Db.Users.SingleAsync(u => u.Id == TestDb.DefaultOwner)).DisplayName = "Half done";
                await c.Db.SaveChangesAsync();
                if (fail) throw new InvalidOperationException("example failure");
            }),
            new("later", "Runs after flaky", _ => { laterRuns++; return Task.CompletedTask; }),
        ];

        Assert.Empty(await Runner(t).RunAsync(fixes));   // does not throw: startup goes on
        Assert.Empty(await RecordedAsync(t));
        Assert.Equal(0, laterRuns);
        await using (var db = await t.System.CreateDbContextAsync())
            Assert.Null((await db.Users.SingleAsync(u => u.Id == TestDb.DefaultOwner)).DisplayName);   // rolled back

        fail = false;
        Assert.Equal(["flaky", "later"], await Runner(t).RunAsync(fixes));
        Assert.Equal(["flaky", "later"], await RecordedAsync(t));
        Assert.Equal(1, laterRuns);
    }

    [Fact]
    public void The_registered_fixes_have_unique_names_and_descriptions()
    {
        Assert.Equal(DataFixList.All.Count, DataFixList.All.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(DataFixList.All, f => Assert.False(string.IsNullOrWhiteSpace(f.Description)));
    }

    [Fact]
    public async Task The_app_records_every_registered_fix_at_startup()
    {
        using var host = new AppTestHost();
        await host.StartAsync();
        await using var db = await host.System.CreateDbContextAsync();
        Assert.Equal(DataFixList.All.Select(f => f.Name).Order(StringComparer.Ordinal), await db.AppliedDataFixes.Select(f => f.Name).OrderBy(n => n).ToListAsync());
    }
}
