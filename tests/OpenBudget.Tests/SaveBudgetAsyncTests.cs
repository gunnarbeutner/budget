using Microsoft.EntityFrameworkCore;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// SaveBudgetAsync is the budget editor's save path: it writes metadata + the full monthly-entry
/// set in one transaction, guarded by an optimistic-concurrency check on the Version the editor
/// loaded. A concurrent save by another session must be rejected, not silently overwritten.
/// </summary>
public class SaveBudgetAsyncTests
{
    private static (long Id, CategoryItemType ItemType, decimal[] Months) Entry(long id, decimal monthly)
    {
        var months = new decimal[12];
        for (int i = 0; i < 12; i++) months[i] = monthly;
        return (id, CategoryItemType.Category, months);
    }

    [Fact]
    public async Task Saves_metadata_and_entries_and_bumps_version()
    {
        await using var t = new TestDb();
        t.AddGroup(100);
        t.AddLeaf(101, parent: 100);
        t.AddProject(1);
        var created = t.AddBudget(1, 2026, (101, CategoryItemType.Category, 50m));
        await t.Db.SaveChangesAsync();

        var loaded = await t.Entries.GetBudgetAsync(created.Id);
        Assert.NotNull(loaded);
        Assert.Equal(0, loaded!.Version);

        loaded.Name = "Revised";
        await t.Entries.SaveBudgetAsync(loaded, new[] { Entry(101, 75m) }, expectedVersion: 0);

        var reloaded = await t.Entries.GetBudgetAsync(created.Id);
        Assert.Equal("Revised", reloaded!.Name);
        Assert.Equal(1, reloaded.Version); // bumped
        var amounts = await t.Entries.GetMonthlyBudgetAmountsAsync(created.Id);
        Assert.Equal(75m, amounts[(101L, CategoryItemType.Category)][0]); // entry updated
        // The service also keeps the caller's in-memory copy current for re-render.
        Assert.Equal(1, loaded.Version);
    }

    [Fact]
    public async Task Rejects_a_stale_save_without_overwriting_the_winner()
    {
        await using var t = new TestDb();
        t.AddGroup(100);
        t.AddLeaf(101, parent: 100);
        t.AddProject(1);
        var created = t.AddBudget(1, 2026, (101, CategoryItemType.Category, 50m));
        await t.Db.SaveChangesAsync();

        // Two sessions open the same budget at version 0.
        var sessionA = await t.Entries.GetBudgetAsync(created.Id);
        var sessionB = await t.Entries.GetBudgetAsync(created.Id);
        Assert.Equal(0, sessionA!.Version);
        Assert.Equal(0, sessionB!.Version);

        // A saves first and wins (version 0 -> 1).
        sessionA.Name = "From A";
        await t.Entries.SaveBudgetAsync(sessionA, new[] { Entry(101, 60m) }, expectedVersion: 0);

        // B saves with its now-stale loaded version and is rejected.
        sessionB.Name = "From B";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            t.Entries.SaveBudgetAsync(sessionB, new[] { Entry(101, 999m) }, expectedVersion: 0));

        // A's write stands; B's was not applied.
        var reloaded = await t.Entries.GetBudgetAsync(created.Id);
        Assert.Equal("From A", reloaded!.Name);
        Assert.Equal(1, reloaded.Version);
        var amounts = await t.Entries.GetMonthlyBudgetAmountsAsync(created.Id);
        Assert.Equal(60m, amounts[(101L, CategoryItemType.Category)][0]); // not 999
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rejects_an_empty_name(string badName)
    {
        await using var t = new TestDb();
        t.AddProject(1);
        var created = t.AddBudget(1, 2026);
        await t.Db.SaveChangesAsync();

        var loaded = await t.Entries.GetBudgetAsync(created.Id);
        loaded!.Name = badName;

        await Assert.ThrowsAsync<ArgumentException>(() =>
            t.Entries.SaveBudgetAsync(loaded, System.Array.Empty<(long, CategoryItemType, decimal[])>(), expectedVersion: 0));

        // Nothing persisted: name unchanged, version not bumped.
        var reloaded = await t.Entries.GetBudgetAsync(created.Id);
        Assert.Equal("2026", reloaded!.Name);
        Assert.Equal(0, reloaded.Version);
    }

    [Fact]
    public async Task Rejects_an_out_of_range_year()
    {
        await using var t = new TestDb();
        t.AddProject(1);
        var created = t.AddBudget(1, 2026);
        await t.Db.SaveChangesAsync();

        var loaded = await t.Entries.GetBudgetAsync(created.Id);
        loaded!.Year = 1800;

        await Assert.ThrowsAsync<ArgumentException>(() =>
            t.Entries.SaveBudgetAsync(loaded, System.Array.Empty<(long, CategoryItemType, decimal[])>(), expectedVersion: 0));

        var reloaded = await t.Entries.GetBudgetAsync(created.Id);
        Assert.Equal(2026, reloaded!.Year);
        Assert.Equal(0, reloaded.Version);
    }
}
